// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using HaCompanion.Core.MobileApp;
using Microsoft.Extensions.Logging;

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
                    // (shutdown /a) and open apps get their save prompts
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown",
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
                        _logger.LogWarning("command_volume without a usable level (got '{Param}')", param);
                        return PcCommandResult.BadParameter;
                    }
                    SetMasterVolume(level / 100f);
                    break;

                case PcCommand.Mute:
                    ToggleMute();
                    break;

                case PcCommand.Launch:
                    return LaunchWhitelisted(param);
            }
            _logger.LogInformation("PC command executed: {Command}{Param}", command,
                param is null ? "" : $" ({param})");
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
        // Match against the whitelist by full path or by file name — but always START the
        // whitelist entry, never the received string (no argument/path smuggling).
        var entry = _settings.Load().LaunchWhitelist.FirstOrDefault(w =>
            string.Equals(w, app, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(w), app, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            _logger.LogWarning("command_launch rejected: '{App}' is not whitelisted", app);
            return PcCommandResult.BadParameter;
        }
        if (!LaunchWhitelist.TryValidateEntry(entry, out var fullPath))
        {
            _logger.LogWarning(
                "command_launch rejected: whitelist entry '{Entry}' is not an existing absolute .exe path", entry);
            return PcCommandResult.Failed;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = fullPath,
            // No shell: a plain CreateProcess on the .exe — no PATH lookup, no
            // App-Paths aliases, no URL/.lnk/.bat handlers.
            UseShellExecute = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(fullPath)!,
        });
        _logger.LogInformation("PC command executed: Launch ({Entry})", fullPath);
        return PcCommandResult.Ok;
    }

    // ----- volume via Core Audio (no NuGet; interop mirrors AudioPlaybackProbe) -----

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

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr client);

        int UnregisterControlChangeNotify(IntPtr client);

        int GetChannelCount(out uint count);

        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        int GetMasterVolumeLevel(out float levelDb);

        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        int GetChannelVolumeLevel(uint channel, out float levelDb);

        int GetChannelVolumeLevelScalar(uint channel, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
