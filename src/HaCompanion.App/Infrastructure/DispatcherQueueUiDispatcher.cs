// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Dispatching;

namespace HaCompanion.App.Infrastructure;

/// <inheritdoc cref="IUiDispatcher"/>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("IUiDispatcher must be constructed on the UI thread.");

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess)
            action();
        else
            _queue.TryEnqueue(() => action());
    }
}
