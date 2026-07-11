// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.App.Infrastructure;

/// <summary>Marshals actions onto the UI thread (wraps the WinUI DispatcherQueue).</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Run <paramref name="action"/> on the UI thread (immediately if already on it).</summary>
    void Post(Action action);
}
