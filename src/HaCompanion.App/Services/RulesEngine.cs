// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using HaCompanion.App.Infrastructure;
using HaCompanion.Core.Automations;
using HaCompanion.Core.Models;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>Runs the user's Windows automation rules against the state monitor's triggers.</summary>
public interface IRulesEngine
{
    /// <summary>Subscribe to the monitor and load the rules. Call once at startup.</summary>
    void Initialize();

    /// <summary>Re-read the rule list after it changed (add/remove/enable).</summary>
    void Reload();

    /// <summary>When the rule last executed its actions this session (footer on the rule card).</summary>
    DateTimeOffset? LastFiredAt(AutomationRule rule);

    /// <summary>Raised after a rule executed (the page refreshes its "last fired" footers).</summary>
    event EventHandler? RuleFired;
}

/// <inheritdoc cref="IRulesEngine"/>
public sealed class RulesEngine : IRulesEngine
{
    private const int CooldownMs = 2000;      // one lock event storm = one execution
    private const int StartupGraceMs = 120_000;

    private readonly IRulesStore _store;
    private readonly IWindowsStateMonitor _monitor;
    private readonly IEntityActionService _actions;
    private readonly IHaConnection _connection;
    private readonly LocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<RulesEngine> _logger;

    private readonly IdleEdgeDetector _idle = new();
    private readonly HashSet<int> _activeIdleThresholds = new();
    private readonly Dictionary<AutomationRule, long> _cooldown = new();
    private readonly Dictionary<AutomationRule, DateTimeOffset> _lastFired = new();
    private IReadOnlyList<AutomationRule> _rules = Array.Empty<AutomationRule>();
    private long _startupPendingUntil; // TickCount64 deadline while waiting for HA to connect

    public event EventHandler? RuleFired;

    public RulesEngine(IRulesStore store, IWindowsStateMonitor monitor, IEntityActionService actions,
        IHaConnection connection, LocalizationService localization, IUiDispatcher ui, ILogger<RulesEngine> logger)
    {
        _store = store;
        _monitor = monitor;
        _actions = actions;
        _connection = connection;
        _localization = localization;
        _ui = ui;
        _logger = logger;
    }

    public void Initialize()
    {
        Reload();
        _monitor.TriggerFired += OnTrigger;
        _monitor.IdleMinutesChanged += OnIdleMinutes;
        _connection.StatusChanged += OnConnectionStatus;
    }

    public void Reload()
    {
        _rules = _store.Load();
        _idle.SetThresholds(_rules
            .Where(r => r.IsEnabled && r.IdleMinutes is not null
                        && WindowsTriggers.TryParse(r.Trigger, out var t)
                        && t is WindowsTrigger.IdleStart or WindowsTrigger.IdleEnd)
            .Select(r => r.IdleMinutes!.Value));
    }

    public DateTimeOffset? LastFiredAt(AutomationRule rule) =>
        _lastFired.TryGetValue(rule, out var at) ? at : null;

    // ----- trigger intake (UI thread — the monitor posts) -----

    private void OnTrigger(object? sender, WindowsStateEvent e)
    {
        try
        {
            if (e.Trigger == WindowsTrigger.Startup && _connection.Status != HaConnectionStatus.Connected)
            {
                // The one queued case: HA connects seconds after launch — a startup rule
                // would otherwise always fire into a dead connection.
                _startupPendingUntil = Environment.TickCount64 + StartupGraceMs;
                return;
            }
            Handle(e.Trigger, e.Param);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rule trigger handling failed");
        }
    }

    private void OnIdleMinutes(object? sender, int minutes)
    {
        try
        {
            var edges = _idle.Advance(minutes);
            foreach (var threshold in edges.Started)
            {
                _activeIdleThresholds.Add(threshold);
                Handle(WindowsTrigger.IdleStart, threshold.ToString(CultureInfo.InvariantCulture));
            }
            if (edges.Ended)
            {
                // idle_end rules are threshold-scoped too: "wieder aktiv nach >= X min"
                foreach (var threshold in _activeIdleThresholds)
                    Handle(WindowsTrigger.IdleEnd, threshold.ToString(CultureInfo.InvariantCulture));
                _activeIdleThresholds.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idle edge handling failed");
        }
    }

    private void OnConnectionStatus(object? sender, HaConnectionStatus status)
    {
        if (status != HaConnectionStatus.Connected || _startupPendingUntil == 0)
            return;
        var pendingValid = Environment.TickCount64 <= _startupPendingUntil;
        _startupPendingUntil = 0;
        if (pendingValid)
            _ui.Post(() => Handle(WindowsTrigger.Startup, null)); // engine runs on the UI thread
    }

    // ----- execution -----

    private void Handle(WindowsTrigger trigger, string? param)
    {
        var key = WindowsTriggers.ToKey(trigger);
        foreach (var rule in _rules)
        {
            if (!rule.IsEnabled || !RuleMatcher.Matches(rule, key, param))
                continue;
            RunRule(rule, trigger);
        }
    }

    private void RunRule(AutomationRule rule, WindowsTrigger trigger)
    {
        var now = Environment.TickCount64;
        if (_cooldown.TryGetValue(rule, out var last) && now - last < CooldownMs)
            return;
        _cooldown[rule] = now;

        var nowTime = TimeOnly.FromDateTime(DateTime.Now);
        if (!rule.EffectiveConditions.All(c => ConditionEvaluator.IsSatisfied(c, EntityIsOn, nowTime)))
        {
            _logger.LogInformation("Rule {Trigger} skipped: condition not met", rule.Trigger);
            return;
        }

        // Lock/suspend/shutdown race the network teardown — fire best-effort anyway; the
        // WS status may lag while REST still works for the first seconds.
        var bestEffort = trigger is WindowsTrigger.Lock or WindowsTrigger.Suspend or WindowsTrigger.Shutdown;
        if (_connection.Status != HaConnectionStatus.Connected && !bestEffort)
        {
            _logger.LogInformation("Rule {Trigger} skipped: HA not connected", rule.Trigger);
            return;
        }

        // No toast when the user can't see it anyway (machine is going down).
        var showOsd = trigger is not (WindowsTrigger.Suspend or WindowsTrigger.Shutdown);
        var title = _localization["Trig_" + rule.Trigger];

        _lastFired[rule] = DateTimeOffset.Now;
        _logger.LogInformation("Rule fired: {Trigger} -> {Count} action(s)", rule.Trigger, rule.Actions.Count);

        if (rule.Actions.Count == 1)
        {
            var action = rule.Actions[0];
            _actions.Trigger(action.EntityId, action.Action, osdTitle: title, showOsd: showOsd);
        }
        else
        {
            _ = RunManyAsync(rule);
            if (showOsd)
                _actions.ShowToast(title, string.Format(CultureInfo.CurrentCulture, _localization["Osd_NActions"], rule.Actions.Count));
        }
        RuleFired?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunManyAsync(AutomationRule rule)
    {
        foreach (var action in rule.Actions)
        {
            _actions.Trigger(action.EntityId, action.Action, showOsd: false);
            await Task.Delay(300); // be gentle: staggered, not a burst
        }
    }

    private bool? EntityIsOn(string entityId) =>
        _connection.Entities.TryGetValue(entityId, out var state) ? state.IsOn : null;
}
