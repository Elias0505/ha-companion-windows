// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using HaCompanion.App.Infrastructure;
using HaCompanion.Core.Automations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HaCompanion.App.Services;

/// <summary>Current values of everything the monitor observes (immutable snapshot).</summary>
public sealed record WindowsStateSnapshot
{
    public bool IsLocked { get; init; }
    public string SessionState { get; init; } = "active"; // active | locked | logged_out
    public int IdleMinutes { get; init; }
    public string? ForegroundProcess { get; init; }       // normalized (A3)
    public bool IsFullscreen { get; init; }               // (A3)
    public bool MicInUse { get; init; }                   // (A3)
    public bool CamInUse { get; init; }                   // (A3)
    public bool DisplayOn { get; init; } = true;
    public bool? AudioPlaying { get; init; }              // null = probe unavailable (A3)
    public DateTimeOffset AppStartedAt { get; init; }
}

/// <summary>A Windows-side trigger together with the state at the moment it fired.</summary>
public sealed record WindowsStateEvent(WindowsTrigger Trigger, string? Param, WindowsStateSnapshot Snapshot);

/// <summary>
/// Observes the Windows session (lock/logon/power/shutdown), the console display state,
/// input idle time and — via the pollers — foreground app, fullscreen, mic/cam and audio.
/// One shared source for both the local rules engine and the HA sensor publisher.
/// </summary>
public interface IWindowsStateMonitor
{
    /// <summary>Hook system events + subclass the window's WndProc. Call once at startup.</summary>
    void Initialize(Window window);

    WindowsStateSnapshot Current { get; }

    /// <summary>All discrete transitions except idle thresholds (those are rule-level,
    /// derived from <see cref="IdleMinutesChanged"/>). Always raised on the UI thread.</summary>
    event EventHandler<WindowsStateEvent>? TriggerFired;

    /// <summary>Raised when the integer idle-minute value changes (incl. drop to 0). UI thread.</summary>
    event EventHandler<int>? IdleMinutesChanged;
}

/// <inheritdoc cref="IWindowsStateMonitor"/>
public sealed class WindowsStateMonitor : IWindowsStateMonitor, IDisposable
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_ENDSESSION = 0x0016;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WTS_SESSION_LOGON = 0x5;
    private const int WTS_SESSION_LOGOFF = 0x6;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const long ENDSESSION_LOGOFF = 0x80000000L;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IUiDispatcher _ui;
    private readonly ILogger<WindowsStateMonitor> _logger;
    private readonly object _gate = new();
    private readonly AudioPlaybackProbe _audioProbe = new();
    private readonly Dictionary<uint, string> _processNameCache = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    // debounce state for the flappier probes (2 identical reads to flip; audio 3 to stop)
    private bool _fullscreenRead;
    private int _fullscreenAgree;
    private bool _micRead;
    private int _micAgree;
    private bool _camRead;
    private int _camAgree;
    private int _audioQuietReads;
    private bool _audioErrorLogged;

    private WndProc? _newProc; // held to prevent GC of the delegate
    private IntPtr _oldProc;
    private IntPtr _hwnd;
    private Timer? _timer;
    private int _timerBusy;
    private long _tick;

    // observed state (mutated under _gate, read via Current)
    private bool _isLocked;
    private string _sessionState = "active";
    private bool _displayOn = true;
    private bool _displayBaselineSeen;
    private int _idleMinutes;
    private string? _foregroundProcess;
    private bool _isFullscreen;
    private bool _micInUse;
    private bool _camInUse;
    private bool? _audioPlaying;
    private readonly DateTimeOffset _appStartedAt = DateTimeOffset.Now;

    public event EventHandler<WindowsStateEvent>? TriggerFired;

    public event EventHandler<int>? IdleMinutesChanged;

    public WindowsStateMonitor(IUiDispatcher ui, ILogger<WindowsStateMonitor> logger)
    {
        _ui = ui;
        _logger = logger;
    }

    public WindowsStateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return new WindowsStateSnapshot
                {
                    IsLocked = _isLocked,
                    SessionState = _sessionState,
                    IdleMinutes = _idleMinutes,
                    ForegroundProcess = _foregroundProcess,
                    IsFullscreen = _isFullscreen,
                    MicInUse = _micInUse,
                    CamInUse = _camInUse,
                    DisplayOn = _displayOn,
                    AudioPlaying = _audioPlaying,
                    AppStartedAt = _appStartedAt,
                };
            }
        }
    }

    public void Initialize(Window window)
    {
        if (_hwnd != IntPtr.Zero)
            return; // already hooked — never subclass twice

        // Second WndProc subclass on the main window (HotkeyService installed the first).
        // Chains compose because neither hook is ever removed for the process lifetime.
        // Everything comes in through this one window: session changes (WTS), power
        // transitions and display state (power broadcasts), shutdown (WM_ENDSESSION).
        // SystemEvents.SessionSwitch was tried first and never delivered in this
        // unpackaged WinUI app — WTSRegisterSessionNotification on our own hwnd does.
        _hwnd = WindowNative.GetWindowHandle(window);
        _newProc = HandleMessage;
        _oldProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));
        RegisterPowerSettingNotification(_hwnd, in GuidConsoleDisplayState, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (!WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION))
            _logger.LogWarning("WTSRegisterSessionNotification failed — lock/unlock triggers unavailable");

        // Baseline: the app may start INSIDE a locked session (e.g. relaunch while away) —
        // WTS only reports transitions, so probe the initial state once. OpenInputDesktop
        // fails while the secure desktop (lock screen) is active.
        if (IsWorkstationLocked())
        {
            lock (_gate)
            {
                _isLocked = true;
                _sessionState = "locked";
            }
        }

        _timer = new Timer(OnTimerTick, null, dueTime: 1000, period: 1000);

        // Startup fires late enough that every subscriber registered during OnLaunched is
        // attached; the rules engine additionally defers execution until HA connects.
        _ = Task.Delay(3000).ContinueWith(_ => Fire(WindowsTrigger.Startup));
    }

    // ----- push sources (all via the window procedure) -----

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // An exception must never escape into the window procedure — it would take the
        // whole process down.
        try
        {
            switch (msg)
            {
                case WM_WTSSESSION_CHANGE:
                    OnSessionChange((int)wParam);
                    break;
                case WM_POWERBROADCAST:
                    OnPowerBroadcast((int)wParam, lParam);
                    break;
                case WM_ENDSESSION when wParam != IntPtr.Zero:
                    Fire(WindowsTrigger.Shutdown, ((long)lParam & ENDSESSION_LOGOFF) != 0 ? "logoff" : "shutdown");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor message handler failed");
        }
        return CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
    }

    private void OnSessionChange(int reason)
    {
        switch (reason)
        {
            case WTS_SESSION_LOCK:
                lock (_gate) { _isLocked = true; _sessionState = "locked"; }
                Fire(WindowsTrigger.Lock);
                break;
            case WTS_SESSION_UNLOCK:
                lock (_gate) { _isLocked = false; _sessionState = "active"; }
                Fire(WindowsTrigger.Unlock);
                break;
            case WTS_SESSION_LOGON:
                lock (_gate) { _isLocked = false; _sessionState = "active"; }
                Fire(WindowsTrigger.Logon);
                break;
            case WTS_SESSION_LOGOFF:
                lock (_gate) { _sessionState = "logged_out"; }
                Fire(WindowsTrigger.Logoff);
                break;
        }
    }

    private void OnPowerBroadcast(int what, IntPtr lParam)
    {
        switch (what)
        {
            case PBT_APMSUSPEND:
                Fire(WindowsTrigger.Suspend);
                break;
            case PBT_APMRESUMEAUTOMATIC: // fires on every resume (RESUMESUSPEND only on user input)
                Fire(WindowsTrigger.Resume);
                break;
            case PBT_POWERSETTINGCHANGE when lParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == GuidConsoleDisplayState)
                {
                    var on = setting.Data != 0; // 0 = off, 1 = on, 2 = dimmed (counts as on)
                    bool changed;
                    lock (_gate)
                    {
                        // The registration itself delivers one immediate notification with
                        // the CURRENT state — that's baseline sync, not a transition.
                        changed = _displayBaselineSeen && _displayOn != on;
                        _displayBaselineSeen = true;
                        _displayOn = on;
                    }
                    if (changed)
                        Fire(on ? WindowsTrigger.DisplayOn : WindowsTrigger.DisplayOff);
                }
                break;
        }
    }

    // ----- polled probes -----

    private void OnTimerTick(object? state)
    {
        if (Interlocked.Exchange(ref _timerBusy, 1) == 1)
            return; // a slow probe must not stack ticks
        try
        {
            var tick = ++_tick;
            PollIdle();
            if (tick % 2 == 0)
            {
                PollForeground();
                PollFullscreen();
                PollAudio();
            }
            if (tick % 3 == 0)
                PollMediaConsent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _timerBusy, 0);
        }
    }

    private void PollIdle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return;

        // Both values are 32-bit tick counts; unchecked subtraction stays correct across
        // the 49.7-day wrap.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        var minutes = (int)(idleMs / 60_000);

        bool changed;
        lock (_gate)
        {
            changed = _idleMinutes != minutes;
            _idleMinutes = minutes;
        }
        if (changed)
            _ui.Post(() => IdleMinutesChanged?.Invoke(this, minutes));
    }

    private void PollForeground()
    {
        var hwnd = GetForegroundWindow();
        string? name = null;
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && pid != _ownPid)
                name = ProcessNameOf(pid);
        }
        var normalized = string.IsNullOrEmpty(name) ? null : RuleMatcher.NormalizeProcessName(name);

        string? previous;
        bool changed;
        lock (_gate)
        {
            previous = _foregroundProcess;
            changed = !string.Equals(previous, normalized, StringComparison.Ordinal);
            if (changed)
                _foregroundProcess = normalized;
        }
        if (!changed)
            return;
        // foreground semantics: the old app "stops being active", the new one "becomes active"
        if (previous is not null)
            Fire(WindowsTrigger.AppStop, previous);
        if (normalized is not null)
            Fire(WindowsTrigger.AppStart, normalized);
    }

    private string? ProcessNameOf(uint pid)
    {
        if (_processNameCache.TryGetValue(pid, out var cached))
            return cached;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (_processNameCache.Count > 128)
                _processNameCache.Clear(); // PIDs recycle — keep the cache small and fresh
            _processNameCache[pid] = name;
            return name;
        }
        catch
        {
            return null; // the process may already be gone
        }
    }

    private void PollFullscreen()
    {
        var read = ReadFullscreen();
        if (read == _fullscreenRead)
            _fullscreenAgree++;
        else
        {
            _fullscreenRead = read;
            _fullscreenAgree = 1;
        }
        if (_fullscreenAgree < 2)
            return; // QUNS flickers around transitions — two identical reads to flip

        bool changed;
        lock (_gate)
        {
            changed = _isFullscreen != read;
            _isFullscreen = read;
        }
        if (changed)
            Fire(read ? WindowsTrigger.FullscreenStart : WindowsTrigger.FullscreenEnd);
    }

    private bool ReadFullscreen()
    {
        // Session-level signal first (F11 browsers = BUSY, games = D3D, PowerPoint = PRESENTATION)...
        if (SHQueryUserNotificationState(out var quns) == 0 && quns is 2 or 3 or 4)
            return true;

        // ...plus a heuristic for borderless-windowed apps QUNS misses: the foreground
        // window exactly covers its monitor and has no caption bar.
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid)
            return false;
        var cls = new System.Text.StringBuilder(64);
        _ = GetClassNameW(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW")
            return false; // the desktop itself covers the monitor caption-less
        if (!GetWindowRect(hwnd, out var rect))
            return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi))
            return false;
        const long WS_CAPTION = 0x00C00000;
        var style = (long)GetWindowLongPtr(hwnd, -16 /* GWL_STYLE */);
        return (style & WS_CAPTION) != WS_CAPTION
               && rect.Left <= mi.rcMonitor.Left && rect.Top <= mi.rcMonitor.Top
               && rect.Right >= mi.rcMonitor.Right && rect.Bottom >= mi.rcMonitor.Bottom;
    }

    private void PollMediaConsent()
    {
        var mic = MediaConsentProbe.IsInUse(MediaConsentProbe.Microphone);
        if (mic == _micRead)
            _micAgree++;
        else
        {
            _micRead = mic;
            _micAgree = 1;
        }
        if (_micAgree >= 2)
        {
            bool changed;
            lock (_gate)
            {
                changed = _micInUse != mic;
                _micInUse = mic;
            }
            if (changed)
                Fire(mic ? WindowsTrigger.MicOn : WindowsTrigger.MicOff);
        }

        var cam = MediaConsentProbe.IsInUse(MediaConsentProbe.Webcam);
        if (cam == _camRead)
            _camAgree++;
        else
        {
            _camRead = cam;
            _camAgree = 1;
        }
        if (_camAgree >= 2)
        {
            bool changed;
            lock (_gate)
            {
                changed = _camInUse != cam;
                _camInUse = cam;
            }
            if (changed)
                Fire(cam ? WindowsTrigger.CamOn : WindowsTrigger.CamOff);
        }
    }

    private void PollAudio()
    {
        var peak = _audioProbe.ReadPeak();
        bool? target;
        if (peak is null)
        {
            if (!_audioErrorLogged && _audioProbe.LastError is { } err)
            {
                _logger.LogWarning(err, "Audio probe unavailable — audio triggers/sensor stay inert");
                _audioErrorLogged = true;
            }
            _audioQuietReads = 0;
            target = null; // probe unavailable — consumers degrade, no triggers
        }
        else if (peak.Value > 0.001f)
        {
            _audioQuietReads = 0;
            target = true;
        }
        else
        {
            // ~6 s of silence before "off" so inter-track gaps don't flap the trigger
            if (++_audioQuietReads < 3)
                return;
            target = false;
        }

        bool? previous;
        bool changed;
        lock (_gate)
        {
            previous = _audioPlaying;
            changed = previous != target;
            _audioPlaying = target;
        }
        // first observation (previous null) is baseline sync, not a transition
        if (changed && previous is not null && target is bool playing)
            Fire(playing ? WindowsTrigger.AudioStart : WindowsTrigger.AudioStop);
    }

    // ----- plumbing -----

    private void Fire(WindowsTrigger trigger, string? param = null)
    {
        var snapshot = Current;
        _logger.LogInformation("Windows trigger: {Key}{Param}",
            WindowsTriggers.ToKey(trigger), param is null ? "" : $" ({param})");
        _ui.Post(() => TriggerFired?.Invoke(this, new WindowsStateEvent(trigger, param, snapshot)));
    }

    private static bool IsWorkstationLocked()
    {
        // WTSINFOEXW: Level(4)+pad(4) | Data: SessionId(4), SessionState(4), SessionFlags(4)
        // SessionFlags: 0 = locked, 1 = unlocked (Win8+; the Win7 inversion is history).
        const int WTSSessionInfoEx = 25;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1 /* current session */, WTSSessionInfoEx,
                out var buffer, out var length) || buffer == IntPtr.Zero)
            return false; // unknown — assume unlocked rather than inventing a lock
        try
        {
            if (length < 20)
                return false;
            var flags = Marshal.ReadInt32(buffer, 16);
            return flags == 0;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _audioProbe.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // first payload byte — enough for the display state
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass,
        out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
