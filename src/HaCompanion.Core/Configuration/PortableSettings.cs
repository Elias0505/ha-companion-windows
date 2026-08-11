// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaCompanion.Core.Configuration;

/// <summary>
/// Which settings a config backup may carry between machines, and what shape each must
/// have. Lives in Core so the rules are unit-tested: an import writes straight into
/// settings.json, so a mistake here is a security bug, not a cosmetic one.
/// </summary>
public static class PortableSettings
{
    /// <summary>
    /// Keys an import is allowed to write. Cosmetic/behavioural only.
    ///
    /// Never add: the token, webhook id or device id (secrets), and no SECURITY DECISION —
    /// certificate handling, the HA→PC command permissions and the launch whitelist are
    /// choices the user makes locally. A shared bundle that could flip them would turn
    /// "import my dashboard setup" into remote code execution on the importer's PC.
    /// BaseUrl is allowed (the point of moving to a new PC) but the importer must drop the
    /// stored credentials when it changes — see <see cref="ForcesCredentialReset"/>.
    /// QuickPanelMonitor is device-specific and deliberately not portable.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "BaseUrl", "Hotkey", "AutoHideQuickPanel", "QuickPanelWidth",
        "Language", "QuickPanelStartView", "QuickPanelLastView", "QuickPanelDragResize",
        "QuickPanelSortByCategory", "ShowHaNotifications", "IdleSensorThresholdMinutes",
    };

    /// <summary>
    /// Settings keys that must never be importable, listed explicitly so a regression test
    /// can prove they are absent from <see cref="Keys"/> even if someone "restores" them.
    /// </summary>
    public static readonly IReadOnlyList<string> NeverImportable = new[]
    {
        "TokenProtected", "WebhookIdProtected", "MobileAppDeviceId",
        "IgnoreCertificateErrors", "LaunchWhitelist",
        "AllowCmdLock", "AllowCmdMonitorOff", "AllowCmdVolume",
        "AllowCmdSleep", "AllowCmdShutdown", "AllowCmdLaunch",
    };

    /// <summary>Credentials must be discarded when this key changes on import.</summary>
    public static bool ForcesCredentialReset(string key) =>
        string.Equals(key, "BaseUrl", StringComparison.Ordinal);

    /// <summary>
    /// True when every present key has the JSON type settings.json expects. A wrong type
    /// makes the file undeserializable, and the load path then falls back to defaults —
    /// which the next save would write over the encrypted token. So a mismatch must reject
    /// the whole bundle rather than apply part of it.
    /// </summary>
    public static bool TypesValid(JsonObject imported)
    {
        foreach (var key in Keys)
        {
            if (!imported.TryGetPropertyValue(key, out var node) || node is null)
                continue;
            if (!KindMatches(key, node.GetValueKind()))
                return false;
        }
        return true;
    }

    private static bool KindMatches(string key, JsonValueKind kind) => key switch
    {
        "BaseUrl" or "Hotkey" or "Language" or "QuickPanelStartView" or "QuickPanelLastView"
            => kind == JsonValueKind.String,
        "AutoHideQuickPanel" or "QuickPanelDragResize" or "QuickPanelSortByCategory"
            or "ShowHaNotifications"
            => kind is JsonValueKind.True or JsonValueKind.False,
        "QuickPanelWidth" or "IdleSensorThresholdMinutes"
            => kind == JsonValueKind.Number,
        _ => true,
    };
}
