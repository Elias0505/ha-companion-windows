// SPDX-License-Identifier: AGPL-3.0-only
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
/// Execution (service call + OSD toast) lives in <see cref="IEntityActionService"/>, shared
/// with the quick panel's Ctrl+K launcher. The hotkey message arrives on the main window's
/// UI thread.
/// </remarks>
public sealed class ShortcutManager : IShortcutManager
{
    private readonly IShortcutStore _store;
    private readonly IHotkeyService _hotkeys;
    private readonly IEntityActionService _actions;
    private readonly Dictionary<string, bool> _activeByKey = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public ShortcutManager(IShortcutStore store, IHotkeyService hotkeys, IEntityActionService actions)
    {
        _store = store;
        _hotkeys = hotkeys;
        _actions = actions;
    }

    public void Initialize()
    {
        _hotkeys.ActionPressed += (_, entityId) => _actions.Trigger(entityId);
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
}
