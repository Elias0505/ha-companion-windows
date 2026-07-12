// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Infrastructure;
using HaCompanion.App.Models;
using HaCompanion.App.Windows;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>Registers the user's entity shortcuts as global hotkeys and runs them when pressed.</summary>
public interface IShortcutManager
{
    /// <summary>Hook the hotkey service and register the stored shortcuts (call once at startup).</summary>
    void Initialize();

    /// <summary>Re-register everything after the shortcut list changed.</summary>
    void Reload();

    /// <summary>Whether the binding's hotkey could actually be registered system-wide.</summary>
    bool IsActive(ShortcutBinding binding);

    /// <summary>Raised after a reload so open views can refresh their status display.</summary>
    event EventHandler? Changed;
}

/// <inheritdoc cref="IShortcutManager"/>
/// <remarks>
/// A press resolves the entity's toggle/run service from its current state and fires it,
/// then shows the small OSD toast in the bottom-right corner. The hotkey message arrives
/// on the main window's UI thread, so the toast window can be driven directly.
/// </remarks>
public sealed class ShortcutManager : IShortcutManager
{
    private readonly IShortcutStore _store;
    private readonly IHotkeyService _hotkeys;
    private readonly IHaConnection _connection;
    private readonly MdiIconProvider _icons;
    private readonly LocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ShortcutManager> _logger;
    private readonly Dictionary<string, bool> _activeByKey = new(StringComparer.Ordinal);
    private ShortcutOsdWindow? _osd;
    private string? _lastEntityId;   // entity of the toast currently on screen
    private long _lastTriggerMs;

    /// <summary>Domains whose toggle cleanly maps to a turned-on/off (or opened/closed) display.</summary>
    private static readonly HashSet<string> OnOffDomains = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "input_boolean", "media_player", "climate", "automation",
    };

    public event EventHandler? Changed;

    public ShortcutManager(IShortcutStore store, IHotkeyService hotkeys, IHaConnection connection,
        MdiIconProvider icons, LocalizationService localization, IUiDispatcher ui, ILogger<ShortcutManager> logger)
    {
        _store = store;
        _hotkeys = hotkeys;
        _connection = connection;
        _icons = icons;
        _localization = localization;
        _ui = ui;
        _logger = logger;
    }

    public void Initialize()
    {
        _hotkeys.ActionPressed += (_, entityId) => OnShortcutPressed(entityId);
        _connection.EntityUpdated += OnEntityUpdated; // live-confirm the toast's on/off text
        Reload();
    }

    public void Reload()
    {
        _hotkeys.ClearActions();
        _activeByKey.Clear();
        foreach (var binding in _store.Load())
            _activeByKey[Key(binding)] = _hotkeys.RegisterAction(binding.Hotkey, binding.EntityId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsActive(ShortcutBinding binding) => _activeByKey.GetValueOrDefault(Key(binding));

    private static string Key(ShortcutBinding b) => b.Hotkey + "\u0001" + b.EntityId;

    private void OnShortcutPressed(string entityId)
    {
        // Resolve the action from the entity's current state (locks etc. are state-dependent).
        var known = _connection.Entities.TryGetValue(entityId, out var state);
        var domain = entityId.Split('.')[0];
        var (serviceDomain, service) = DomainCatalog.ResolveAction(domain, known && state!.IsOn);

        _ = TriggerAsync(serviceDomain, service, entityId);

        // Predict the resulting state for toggles ("turned on/off"); scripts/scenes/buttons
        // just show "triggered". The prediction is corrected live once HA reports back.
        var subtitle = _localization["Osd_Done"];
        var accent = true;
        var predictedKey = known ? StateKey(domain, !state!.IsOn) : null;
        if (predictedKey is not null)
        {
            subtitle = _localization[predictedKey];
            accent = !state!.IsOn;
        }

        _lastEntityId = entityId;
        _lastTriggerMs = Environment.TickCount64;

        var glyph = known ? _icons.Resolve(state!) : _icons.DomainGlyph(domain);
        var title = known ? state!.FriendlyName : entityId;
        _ui.Post(() =>
        {
            _osd ??= new ShortcutOsdWindow();
            _osd.ShowToast(glyph, title, subtitle, accent);
        });
    }

    /// <summary>Localization key describing an entity being in the given state, or null if n/a.</summary>
    private static string? StateKey(string domain, bool isOn) => domain switch
    {
        "cover" => isOn ? "Osd_Opened" : "Osd_Closed",
        _ when OnOffDomains.Contains(domain) => isOn ? "Osd_On" : "Osd_Off",
        _ => null,
    };

    private void OnEntityUpdated(object? sender, Core.Models.HaEntityState state)
    {
        // Only correct the toast that is (still) on screen for the entity we just triggered.
        if (!string.Equals(state.EntityId, _lastEntityId, StringComparison.Ordinal)
            || Environment.TickCount64 - _lastTriggerMs > 2600)
            return;

        var key = StateKey(state.Domain, state.IsOn);
        if (key is null)
            return;

        var subtitle = _localization[key];
        var accent = state.IsOn;
        _ui.Post(() => _osd?.UpdateState(subtitle, accent));
    }

    private async Task TriggerAsync(string domain, string service, string entityId)
    {
        try
        {
            await _connection.CallServiceAsync(domain, service, entityId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shortcut action for {EntityId} failed", entityId);
        }
    }
}
