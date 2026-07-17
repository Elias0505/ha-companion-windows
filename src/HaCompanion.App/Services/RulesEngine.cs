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

    /// <summary>When the rule last executed its actions (persisted; footer on the rule card).</summary>
    DateTimeOffset? LastFiredAt(AutomationRule rule);

    /// <summary>How often the rule has executed (persisted).</summary>
    int RunCount(AutomationRule rule);

    /// <summary>"Jetzt testen": run the rule's actions immediately, ignoring trigger/condition/cooldown.</summary>
    void RunActionsNow(AutomationRule rule);

    /// <summary>Raised after a rule executed (the page refreshes its "last fired" footers).</summary>
    event EventHandler? RuleFired;
}

/// <inheritdoc cref="IRulesEngine"/>
public sealed class RulesEngine : IRulesEngine, IDisposable
{
    private const int CooldownMs = 2000;      // one lock event storm = one execution
    private const int StartupGraceMs = 120_000;

    private readonly IRulesStore _store;
    private readonly IWindowsStateMonitor _monitor;
    private readonly IEntityActionService _actions;
    private readonly IHaConnection _connection;
    private readonly IAutomationStatsStore _stats;
    private readonly LocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<RulesEngine> _logger;

    private readonly IdleEdgeDetector _idle = new();
    private readonly HashSet<int> _activeIdleThresholds = new();
    private readonly Dictionary<string, long> _cooldown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _scheduleFiredMinute = new(StringComparer.Ordinal);
    private IReadOnlyList<AutomationRule> _rules = Array.Empty<AutomationRule>();
    private long _startupPendingUntil; // TickCount64 deadline while waiting for HA to connect
    private Timer? _scheduleTimer;

    public event EventHandler? RuleFired;

    public RulesEngine(IRulesStore store, IWindowsStateMonitor monitor, IEntityActionService actions,
        IHaConnection connection, IAutomationStatsStore stats, LocalizationService localization,
        IUiDispatcher ui, ILogger<RulesEngine> logger)
    {
        _store = store;
        _monitor = monitor;
        _actions = actions;
        _connection = connection;
        _stats = stats;
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
        // Schedule triggers aren't Windows events — poll each minute (tick a bit faster so a
        // minute is never missed; a per-minute dedup guard prevents double firing).
        _scheduleTimer = new Timer(_ => _ui.Post(TickSchedules), null, 20_000, 20_000);
    }

    /// <summary>Stable key for cooldown/stats — the rule id, with a content fallback.</summary>
    private static string RuleKey(AutomationRule r) =>
        r.Id ?? $"{r.Trigger}|{r.Param}|{(r.Actions.Count > 0 ? r.Actions[0].EntityId : "")}";

    public void Reload()
    {
        _rules = _store.Load();
        _idle.SetThresholds(_rules
            .Where(r => r.IsEnabled && r.IdleMinutes is not null
                        && WindowsTriggers.TryParse(r.Trigger, out var t)
                        && t is WindowsTrigger.IdleStart or WindowsTrigger.IdleEnd)
            .Select(r => r.IdleMinutes!.Value));
        _stats.Prune(_rules.Select(RuleKey)); // drop stats for deleted rules
    }

    public void Dispose() => _scheduleTimer?.Dispose();

    public DateTimeOffset? LastFiredAt(AutomationRule rule) => _stats.GetStat(RuleKey(rule))?.LastFired;

    public int RunCount(AutomationRule rule) => _stats.GetStat(RuleKey(rule))?.RunCount ?? 0;

    public void RunActionsNow(AutomationRule rule)
    {
        // Test-run: fire the actions on demand, ignoring the trigger, conditions and cooldown,
        // so the user can verify the effect. Does not count towards the run statistics.
        var title = string.Format(CultureInfo.CurrentCulture, _localization["Au_TestTitle"],
            rule.Name is { Length: > 0 } n ? n : _localization["Trig_" + rule.Trigger]);
        FireActions(rule, title, showOsd: true);
    }

    // ----- schedule triggers (not Windows events — polled per minute) -----

    private void TickSchedules()
    {
        try
        {
            var now = DateTime.Now;
            var minuteStamp = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            foreach (var rule in _rules)
            {
                if (!rule.IsEnabled || rule.Trigger != WindowsTriggers.ToKey(WindowsTrigger.Schedule))
                    continue;
                if (!ScheduleSpec.TryParse(rule.Param, out var spec) || !spec.Matches(now))
                    continue;
                var key = RuleKey(rule);
                if (_scheduleFiredMinute.TryGetValue(key, out var fired) && fired == minuteStamp)
                    continue; // already fired this minute
                _scheduleFiredMinute[key] = minuteStamp;
                RunRule(rule, WindowsTrigger.Schedule);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schedule tick failed");
        }
    }

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
        var ruleKey = RuleKey(rule);
        var now = Environment.TickCount64;
        if (_cooldown.TryGetValue(ruleKey, out var last) && now - last < CooldownMs)
            return;
        _cooldown[ruleKey] = now;

        if (!ConditionEvaluator.AllSatisfied(rule.EffectiveConditions, EntityState, PcState, DateTime.Now))
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
        _stats.Record(ruleKey);
        FireActions(rule, _localization["Trig_" + rule.Trigger], showOsd);
    }

    /// <summary>Execute a rule's action(s) with its optional service data and show the OSD.</summary>
    private void FireActions(AutomationRule rule, string title, bool showOsd)
    {
        _logger.LogInformation("Rule fired: {Trigger} -> {Count} action(s)", rule.Trigger, rule.Actions.Count);
        if (rule.Actions.Count == 1)
        {
            var action = rule.Actions[0];
            _actions.Trigger(action.EntityId, action.Action, osdTitle: title, showOsd: showOsd, data: action.Data);
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
            _actions.Trigger(action.EntityId, action.Action, showOsd: false, data: action.Data);
            await Task.Delay(300); // be gentle: staggered, not a burst
        }
    }

    private string? EntityState(string entityId) =>
        _connection.Entities.TryGetValue(entityId, out var state) ? state.State : null;

    /// <summary>Map a pc-condition field to the live snapshot (null = unknown → fails closed).</summary>
    private bool? PcState(string field)
    {
        var s = _monitor.Current;
        return field switch
        {
            "locked" => s.IsLocked,
            "display_on" => s.DisplayOn,
            "fullscreen" => s.IsFullscreen,
            "mic" => s.MicInUse,
            "cam" => s.CamInUse,
            "audio" => s.AudioPlaying,       // null when the probe is unavailable
            "idle" => s.IdleMinutes >= 1,
            _ => null,
        };
    }
}
