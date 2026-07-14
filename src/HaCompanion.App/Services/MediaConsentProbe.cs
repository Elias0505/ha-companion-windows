// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Win32;

namespace HaCompanion.App.Services;

/// <summary>
/// Detects microphone/webcam use via the CapabilityAccessManager consent store:
/// any app whose LastUsedTimeStart is set while LastUsedTimeStop is 0 is using the
/// device right now. Classic apps live under NonPackaged (path with '#' separators),
/// packaged apps directly under the store key — both carry the same value names.
/// Registry-shape variations across Windows builds are contained in this one file;
/// anything unexpected simply reads as "not in use".
/// </summary>
public static class MediaConsentProbe
{
    public const string Microphone = "microphone";
    public const string Webcam = "webcam";

    public static bool IsInUse(string store)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{store}");
            if (root is null)
                return false;
            if (AnyActive(root))
                return true;
            using var nonPackaged = root.OpenSubKey("NonPackaged");
            return nonPackaged is not null && AnyActive(nonPackaged);
        }
        catch
        {
            return false; // a probe must never take the monitor down
        }
    }

    private static bool AnyActive(RegistryKey parent)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged")
                continue;
            using var key = parent.OpenSubKey(name);
            var start = key?.GetValue("LastUsedTimeStart");
            var stop = key?.GetValue("LastUsedTimeStop");
            if (start is long s && s != 0 && stop is long e && e == 0)
                return true;
        }
        return false;
    }
}
