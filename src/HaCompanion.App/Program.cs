// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>
    /// Internal switch used by "reset to factory settings": the freshly started process has to
    /// wait for its predecessor to exit before registering the single-instance key — otherwise
    /// it would be redirected straight back into the instance that is on its way out.
    /// </summary>
    public const string RelaunchAfterArg = "--relaunch-after";

    private const string SingleInstanceKey = "HaCompanion.Main";

    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var predecessor = WaitForPredecessor(); // must happen before any AppInstance call

        if (DecideRedirection(predecessor))
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

    /// <summary>
    /// Honour <see cref="RelaunchAfterArg"/>: block until the given process id is gone, so the
    /// single-instance key it holds is free. Anything unexpected (already exited, bad argument,
    /// hung predecessor) just falls through — worst case we redirect like a normal second start.
    /// Returns the predecessor's process id, or null if we were not started as a relaunch.
    /// </summary>
    private static int? WaitForPredecessor()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, RelaunchAfterArg);
        if (index < 0 || index + 1 >= args.Length ||
            !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            return null;

        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // already gone - nothing to wait for
        }
        catch (InvalidOperationException)
        {
            // it exited while we were looking at it - same thing
        }

        Thread.Sleep(300); // the key is released as the process dies; give the SDK a moment
        return pid;
    }

    private static bool DecideRedirection(int? predecessor)
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        // A relaunch after a factory reset is the one case where a *stale* registration is
        // likely: if the predecessor died badly, its key can linger for a moment. Redirecting
        // into a process that no longer exists would leave the user with no app at all, so
        // wait the registration out instead of trusting it.
        for (var attempt = 0; attempt < 10 && !keyInstance.IsCurrent &&
                              predecessor is int dead && keyInstance.ProcessId == dead; attempt++)
        {
            Thread.Sleep(300);
            keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        }

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, e) => App.OnRedirected(e);
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
            try
            {
                await keyInstance.RedirectActivationToAsync(args);
            }
            catch (Exception)
            {
                // The target died mid-handover; releasing below unblocks Main so this process
                // exits normally instead of waiting on a handover that will never complete.
            }
            finally
            {
                redirected.Release();
            }
        });
        redirected.Wait();
    }
}
