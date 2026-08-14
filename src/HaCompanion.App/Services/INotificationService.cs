// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Services;

/// <summary>A clicked toast button: the action id plus the HA tag that toast was shown with.</summary>
public sealed record ToastActionInvokedArgs(string Action, string? Tag);

/// <summary>Native Windows toast notifications (best-effort; may be unavailable in self-contained builds).</summary>
public interface INotificationService
{
    /// <summary>Register with the OS notification system. Call once at startup, before any UI.</summary>
    void Initialize();

    /// <summary>
    /// Change the toast heading (attribution line) at runtime by re-registering the
    /// notification identity (#9). Empty = the default "HA Companion". Applies to toasts
    /// shown from now on; already-visible ones keep the old heading.
    /// </summary>
    void ApplyDisplayName(string? displayName);

    void Show(string title, string message);

    /// <summary>
    /// Toast with clickable buttons; button clicks raise <see cref="ActionInvoked"/>.
    /// <paramref name="haTag"/> travels inside each button's activation arguments so a
    /// click always reports the tag of ITS OWN toast, not of whichever arrived last.
    /// </summary>
    void ShowWithActions(string title, string message, IReadOnlyList<(string Action, string Title)> actions, string? haTag);

    /// <summary>Raised when the user clicks a toast button.</summary>
    event EventHandler<ToastActionInvokedArgs>? ActionInvoked;
}
