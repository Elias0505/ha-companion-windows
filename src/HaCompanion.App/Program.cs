// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace HaCompanion.App;

/// <summary>
/// Custom entry point (the generated Main is disabled via DisableXamlGeneratedMain) so we can
/// single-instance the app the Windows App SDK way — <see cref="AppInstance"/> key registration
/// plus <see cref="AppInstance.RedirectActivationToAsync"/> — instead of a named mutex and a
/// Win32 FindWindow hack. A second launch is redirected to the running instance, which then
/// resurfaces its window (reliably, even from the tray) via the <c>Activated</c> event.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return; // another instance is already running; we redirected to it and now exit

        Application.Start(p =>
        {
            // A custom Main must install the DispatcherQueue sync context itself (the generated
            // Main normally does this), so async continuations land back on the UI thread.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(); // registers itself as Application.Current; kept alive by the framework
        });
    }

    private static bool DecideRedirection()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey("HaCompanion.Main");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, _) => App.OnRedirected();
            return false;
        }

        RedirectActivationTo(args, keyInstance);
        return true;
    }

    // RedirectActivationToAsync must be awaited, but Main is a plain STA thread with no message
    // pump yet — do the wait on a worker thread and block Main on a semaphore (the pattern from
    // Microsoft's app-instancing sample) to avoid a COM reentrancy deadlock.
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        using var redirected = new SemaphoreSlim(0, 1);
        _ = Task.Run(async () =>
        {
            await keyInstance.RedirectActivationToAsync(args);
            redirected.Release();
        });
        redirected.Wait();
    }
}
