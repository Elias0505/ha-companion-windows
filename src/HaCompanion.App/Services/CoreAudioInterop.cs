// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;

namespace HaCompanion.App.Services;

/// <summary>
/// The Core Audio COM declarations, in ONE place.
///
/// They used to be duplicated as private nested types in both AudioPlaybackProbe and
/// PcCommandExecutor. A CLSID/IID maps to exactly one managed type per process, so
/// whichever copy the runtime bound first won — and the other one's cast then failed with
/// the baffling "Unable to cast object of type 'MMDeviceEnumeratorComObject' to type
/// 'MMDeviceEnumeratorComObject'". In practice that broke command_volume as soon as the
/// audio sensor probe had run first (i.e. whenever PC sensors are on), which is why the
/// volume command worked only sometimes.
/// </summary>
internal static class CoreAudioInterop
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
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
