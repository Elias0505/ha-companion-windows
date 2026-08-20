// SPDX-License-Identifier: AGPL-3.0-only
using System.IO;
using System.Runtime.InteropServices;
using HaCompanion.Core.MobileApp;
using static HaCompanion.App.Services.CoreAudioInterop;
using Microsoft.Extensions.Logging;

// Resolve P/Invoke targets from System32 rather than the default search order, which starts
// with the app's own (user-writable) directory. This is defence in depth, not a guarantee:
// user32/ole32/shell32 are KnownDLLs and were already safe, and the CLR still probes app-local
// paths for assemblies. It does close the gap for the non-KnownDLL imports below (powrprof).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace HaCompanion.App.Services;

/// <summary>
/// Why a PC command did (not) run. The received-log renders each case with its own
/// text — a single boolean once made every failure read as "blocked (not enabled)",
/// which sent users hunting through the permission toggles when the actual problem
/// was a missing data.level (issue #6).
/// </summary>
public enum PcCommandResult
{
    Ok,
    NotEnabled,
    BadParameter,
    Failed,
}

/// <summary>
/// Executes PC commands sent from Home Assistant via notify.mobile_app_&lt;device&gt;.
/// Every command is gated by its own settings toggle (critical ones default off);
/// command_launch additionally only starts executables from the user's whitelist.
/// </summary>
public interface IPcCommandExecutor
{
    /// <summary>Runs the command if allowed and reports why it did (not) run.</summary>
    PcCommandResult Execute(PcCommand command, string? param);

    /// <summary>Whether the settings currently allow this command (for the UI + receiver).</summary>
    bool IsAllowed(PcCommand command);
}

/// <inheritdoc cref="IPcCommandExecutor"/>
public sealed class PcCommandExecutor : IPcCommandExecutor
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<PcCommandExecutor> _logger;

    public PcCommandExecutor(ISettingsStore settings, ILogger<PcCommandExecutor> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsAllowed(PcCommand command)
    {
        var s = _settings.Load();
        return command switch
        {
            PcCommand.Lock => s.AllowCmdLock,
            PcCommand.MonitorOff => s.AllowCmdMonitorOff,
            PcCommand.Volume or PcCommand.Mute => s.AllowCmdVolume,
            PcCommand.Sleep => s.AllowCmdSleep,
            PcCommand.Shutdown => s.AllowCmdShutdown,
            PcCommand.Launch => s.AllowCmdLaunch,
            PcCommand.CloseApp => s.AllowCmdCloseApp,
            _ => false,
        };
    }

    public PcCommandResult Execute(PcCommand command, string? param)
    {
        if (!IsAllowed(command))
        {
            _logger.LogWarning("PC command {Command} rejected: not enabled in settings", command);
            return PcCommandResult.NotEnabled;
        }

        try
        {
            switch (command)
            {
                case PcCommand.Lock:
                    _ = LockWorkStation();
                    break;

                case PcCommand.Sleep:
                    // suspend (not hibernate), honor wake events
                    _ = SetSuspendState(false, false, false);
                    break;

                case PcCommand.Shutdown:
                    // short grace period so a mistaken automation can still be cancelled
                    // (shutdown /a) and open apps get their save prompts.
                    // Absolute path on purpose: with UseShellExecute=false and a bare name,
                    // CreateProcess searches the app's own directory and the working
                    // directory FIRST — both are user-writable (%LOCALAPPDATA%\Programs\…),
                    // so a planted shutdown.exe would run instead of Windows'.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                        Arguments = "/s /t 10",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    });
                    break;

                case PcCommand.MonitorOff:
                    _ = SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, (IntPtr)2);
                    break;

                case PcCommand.Volume:
                    if (!PcCommands.TryParseLevel(param, out var level))
                    {
                        _logger.LogWarning("command_volume without a usable level (got '{Param}')",
                            PcCommands.ForLog(param));
                        return PcCommandResult.BadParameter;
                    }
                    SetMasterVolume(level / 100f);
                    break;

                case PcCommand.Mute:
                    ToggleMute();
                    break;

                case PcCommand.Launch:
                    return LaunchWhitelisted(param);

                case PcCommand.CloseApp:
                    return CloseWhitelisted(param);
            }
            _logger.LogInformation("PC command executed: {Command}{Param}", command,
                param is null ? "" : $" ({PcCommands.ForLog(param)})");
            return PcCommandResult.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PC command {Command} failed", command);
            return PcCommandResult.Failed;
        }
    }

    private PcCommandResult LaunchWhitelisted(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
            return PcCommandResult.BadParameter;
        // The HA message only SELECTS which pre-approved entry runs (matched by the full
        // entry string incl. its arguments — that disambiguates two entries sharing one
        // exe, in the quoted stored form or the natural unquoted spelling —, by the full
        // path, or by the bare file name). What is STARTED is always the locally stored
        // entry: path and arguments both come from the whitelist, never from the received
        // string (no argument/path smuggling).
        string? matchedPath = null;
        string? matchedArgs = null;
        foreach (var candidate in _settings.Load().LaunchWhitelist)
        {
            if (!LaunchWhitelist.TryParseEntry(candidate, out var path, out var args))
                continue; // stale entry (file gone) — skip, maybe another matches
            if (LaunchWhitelist.SelectorMatches(candidate, path, args, app))
            {
                matchedPath = path;
                matchedArgs = args;
                break;
            }
        }
        if (matchedPath is null)
        {
            _logger.LogWarning("command_launch rejected: '{App}' is not whitelisted (or its entry no longer validates)",
                PcCommands.ForLog(app));
            return PcCommandResult.BadParameter;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = matchedPath,
            Arguments = matchedArgs ?? "",
            // No shell: a plain CreateProcess on the .exe — no PATH lookup, no
            // App-Paths aliases, no URL/.lnk/.bat handlers.
            UseShellExecute = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(matchedPath)!,
        });
        _logger.LogInformation("PC command executed: Launch ({Path}{Args})", matchedPath,
            string.IsNullOrEmpty(matchedArgs) ? "" : " " + matchedArgs);
        return PcCommandResult.Ok;
    }

    private PcCommandResult CloseWhitelisted(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)
            || !CloseAppWhitelist.TryValidateName(app, out var requested))
        {
            return PcCommandResult.BadParameter;
        }
        // Same philosophy as launch: HA picks from the locally approved names only — a
        // compromised HA must not be able to shoot down arbitrary processes (backup or
        // security tools, say).
        var allowed = _settings.Load().CloseAppWhitelist.Any(entry =>
            CloseAppWhitelist.TryValidateName(entry, out var normalized)
            && string.Equals(normalized, requested, StringComparison.Ordinal));
        if (!allowed)
        {
            _logger.LogWarning("command_close_app rejected: '{App}' is not in the close allowlist",
                PcCommands.ForLog(app));
            return PcCommandResult.BadParameter;
        }

        var ownSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var ownPid = Environment.ProcessId;
        var targets = System.Diagnostics.Process.GetProcessesByName(requested)
            .Where(p => p.Id != ownPid && SafeSessionId(p) == ownSession)
            .ToList();
        try
        {
            if (targets.Count == 0)
            {
                _logger.LogInformation("command_close_app: no running '{App}' — nothing to close", requested);
                return PcCommandResult.Ok; // the goal (not running) is already met
            }

            // Graceful first: give windowed apps their save prompt; force-kill only what is
            // still alive after the grace period (and everything windowless).
            foreach (var p in targets)
            {
                try { _ = p.CloseMainWindow(); }
                catch (Exception) { /* exited in between / no window */ }
            }
            var deadline = Environment.TickCount64 + 2000;
            foreach (var p in targets)
            {
                try
                {
                    var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (!p.WaitForExit(remaining))
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    // Access denied (elevated process) or it exited mid-kill — log, keep going.
                    _logger.LogWarning(ex, "command_close_app: could not close pid {Pid}", p.Id);
                }
            }
            _logger.LogInformation("PC command executed: CloseApp ({App}, {Count} process(es))",
                requested, targets.Count);
            return PcCommandResult.Ok;
        }
        finally
        {
            foreach (var p in targets)
                p.Dispose();
        }
    }

    private static int SafeSessionId(System.Diagnostics.Process p)
    {
        try { return p.SessionId; }
        catch (Exception) { return -1; } // exited / access denied → never a target
    }

    // ----- volume via Core Audio (declarations shared via CoreAudioInterop) -----

    private static void SetMasterVolume(float level)
    {
        var volume = GetEndpointVolume();
        try
        {
            var ctx = Guid.Empty;
            volume.SetMasterVolumeLevelScalar(level, ref ctx);
            if (level > 0)
                volume.SetMute(false, ref ctx);
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    private static void ToggleMute()
    {
        var volume = GetEndpointVolume();
        try
        {
            volume.GetMute(out var muted);
            var ctx = Guid.Empty;
            volume.SetMute(!muted, ref ctx);
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    private static IAudioEndpointVolume GetEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
                return (IAudioEndpointVolume)obj;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr SC_MONITORPOWER = new(0xF170);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
