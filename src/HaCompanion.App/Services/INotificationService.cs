// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Services;

/// <summary>Native Windows toast notifications (best-effort; may be unavailable in self-contained builds).</summary>
public interface INotificationService
{
    /// <summary>Register with the OS notification system. Call once at startup, before any UI.</summary>
    void Initialize();

    void Show(string title, string message);
}
