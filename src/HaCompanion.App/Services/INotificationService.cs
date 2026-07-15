// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Services;

/// <summary>Native Windows toast notifications (best-effort; may be unavailable in self-contained builds).</summary>
public interface INotificationService
{
    /// <summary>Register with the OS notification system. Call once at startup, before any UI.</summary>
    void Initialize();

    void Show(string title, string message);

    /// <summary>Toast with clickable buttons; button clicks raise <see cref="ActionInvoked"/>.</summary>
    void ShowWithActions(string title, string message, IReadOnlyList<(string Action, string Title)> actions);

    /// <summary>Raised with the action id when the user clicks a toast button.</summary>
    event EventHandler<string>? ActionInvoked;
}
