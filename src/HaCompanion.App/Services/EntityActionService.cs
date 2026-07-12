// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.App.Infrastructure;
using HaCompanion.Core.Models;
using HaCompanion.App.Windows;
using HaCompanion.Core.Services;
using Microsoft.Extensions.Logging;

namespace HaCompanion.App.Services;

/// <summary>
/// Triggers an entity's toggle/run action and shows the bottom-right OSD toast — the shared
/// execution path behind entity shortcuts and the quick panel's Ctrl+K launcher.
/// </summary>
public interface IEntityActionService
{
    /// <summary>Resolve + fire the entity's action and show the OSD toast (UI thread only).</summary>
    void Trigger(string entityId);
}

/// <inheritdoc cref="IEntityActionService"/>
/// <remarks>
/// The toast subtitle is predicted from the toggle direction ("turned on/off") and corrected
/// in place once the entity's real state_changed arrives over the WebSocket.
/// </remarks>
public sealed class EntityActionService : IEntityActionService
{
    private readonly IHaConnection _connection;
    private readonly MdiIconProvider _icons;
    private readonly LocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<EntityActionService> _logger;
    private ShortcutOsdWindow? _osd;
    private string? _lastEntityId;   // entity of the toast currently on screen
    private long _lastTriggerMs;

    /// <summary>Domains whose toggle cleanly maps to a turned-on/off (or opened/closed) display.</summary>
    private static readonly HashSet<string> OnOffDomains = new(StringComparer.Ordinal)
    {
        "light", "switch", "fan", "input_boolean", "media_player", "climate", "automation",
    };

    public EntityActionService(IHaConnection connection, MdiIconProvider icons,
        LocalizationService localization, IUiDispatcher ui, ILogger<EntityActionService> logger)
    {
        _connection = connection;
        _icons = icons;
        _localization = localization;
        _ui = ui;
        _logger = logger;
        _connection.EntityUpdated += OnEntityUpdated; // live-confirm the toast's on/off text
    }

    public void Trigger(string entityId)
    {
        // Resolve the action from the entity's current state (locks etc. are state-dependent).
        var known = _connection.Entities.TryGetValue(entityId, out var state);
        var domain = entityId.Split('.')[0];
        var (serviceDomain, service) = DomainCatalog.ResolveAction(domain, known && state!.IsOn);

        _ = RunAsync(serviceDomain, service, entityId);

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

    private async Task RunAsync(string domain, string service, string entityId)
    {
        try
        {
            await _connection.CallServiceAsync(domain, service, entityId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity action for {EntityId} failed", entityId);
        }
    }
}
