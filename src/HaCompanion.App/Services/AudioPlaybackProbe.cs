// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;

namespace HaCompanion.App.Services;

/// <summary>
/// Reads the peak meter of the default audio render device (Core Audio,
/// IAudioMeterInformation) — peak &gt; ~0 means something is audibly playing.
/// Plain COM interop, no NuGet. Any failure returns null and the probe re-resolves
/// the device on the next read (default-device switches invalidate the meter);
/// persistent failure just leaves the audio triggers/sensor inert.
/// </summary>
public sealed class AudioPlaybackProbe : IDisposable
{
    private const int ERender = 0;      // EDataFlow.eRender
    private const int EMultimedia = 1;  // ERole.eMultimedia
    private const int ClsCtxAll = 0x17;
    private static readonly Guid MeterIid = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private IAudioMeterInformation? _meter;

    /// <summary>Last failure (for one-time diagnostics by the monitor); null after a success.</summary>
    public Exception? LastError { get; private set; }

    public float? ReadPeak()
    {
        try
        {
            if (_meter is null)
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device);
                var iid = MeterIid;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var meterObj);
                _meter = (IAudioMeterInformation)meterObj;
            }
            _meter.GetPeakValue(out var peak);
            LastError = null;
            return peak;
        }
        catch (Exception ex)
        {
            LastError = ex;
            Release(); // e.g. default device changed — try a fresh resolve next tick
            return null;
        }
    }

    private void Release()
    {
        if (_meter is not null)
        {
            try { Marshal.ReleaseComObject(_meter); } catch { }
            _meter = null;
        }
    }

    public void Dispose() => Release();

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

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
