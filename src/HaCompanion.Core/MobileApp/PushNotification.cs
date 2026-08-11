// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json;

namespace HaCompanion.Core.MobileApp;

/// <summary>One clickable button on a pushed notification.</summary>
public sealed record PushAction(string Action, string Title);

/// <summary>
/// A notification pushed by HA over the mobile_app websocket channel
/// (what an automation's notify.mobile_app_&lt;device&gt; call delivers).
/// </summary>
public sealed record PushMessage(
    string? Title,
    string Message,
    string? ConfirmId,
    IReadOnlyList<PushAction> Actions,
    string? Tag);

/// <summary>Parses the event payload of the push channel. Tolerant — HA only guarantees "message".</summary>
public static class PushMessageParser
{
    public static bool TryParse(JsonElement payload, out PushMessage message)
    {
        message = null!;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("message", out var msgEl)
            || msgEl.ValueKind != JsonValueKind.String)
            return false;

        string? title = null;
        if (payload.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            title = titleEl.GetString();

        string? confirmId = null;
        if (payload.TryGetProperty("hass_confirm_id", out var confirmEl) && confirmEl.ValueKind == JsonValueKind.String)
            confirmId = confirmEl.GetString();

        string? tag = null;
        var actions = new List<PushAction>();
        if (payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                tag = tagEl.GetString();
            if (data.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in actionsEl.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.Object
                        && a.TryGetProperty("action", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        && a.TryGetProperty("title", out var atEl) && atEl.ValueKind == JsonValueKind.String)
                        actions.Add(new PushAction(idEl.GetString()!, atEl.GetString()!));
                }
            }
        }

        message = new PushMessage(title, msgEl.GetString()!, confirmId, actions, tag);
        return true;
    }

    /// <summary>The push channel wraps command parameters in data — extract one by name.</summary>
    public static string? DataString(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(name, out var el))
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null,
            };
        }
        return null;
    }
}

/// <summary>PC commands HA can send via the notify service's message field.</summary>
public enum PcCommand
{
    Lock,
    Sleep,
    Shutdown,
    MonitorOff,
    Volume,   // data.level 0..100
    Mute,
    Launch,   // data.app (must be whitelisted by the app)
}

public static class PcCommands
{
    /// <summary>message-value → command. Returns false for ordinary notifications.</summary>
    public static bool TryParse(string message, out PcCommand command)
    {
        switch (message)
        {
            case "command_lock": command = PcCommand.Lock; return true;
            case "command_sleep": command = PcCommand.Sleep; return true;
            case "command_shutdown": command = PcCommand.Shutdown; return true;
            case "command_monitor_off": command = PcCommand.MonitorOff; return true;
            case "command_volume": command = PcCommand.Volume; return true;
            // HA's Android companion calls this command_volume_level (level in title or
            // data) — accepting the alias makes examples copied from the HA docs work.
            case "command_volume_level": command = PcCommand.Volume; return true;
            case "command_mute": command = PcCommand.Mute; return true;
            case "command_launch": command = PcCommand.Launch; return true;
            default: command = default; return false;
        }
    }

    public static string ToKey(PcCommand command) => command switch
    {
        PcCommand.Lock => "command_lock",
        PcCommand.Sleep => "command_sleep",
        PcCommand.Shutdown => "command_shutdown",
        PcCommand.MonitorOff => "command_monitor_off",
        PcCommand.Volume => "command_volume",
        PcCommand.Mute => "command_mute",
        PcCommand.Launch => "command_launch",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>Commands that stay OFF until the user explicitly enables them.</summary>
    public static bool IsCritical(PcCommand command) =>
        command is PcCommand.Shutdown or PcCommand.Sleep or PcCommand.Launch;

    /// <summary>Which data field carries the parameter (null = parameterless).</summary>
    public static string? ParamField(PcCommand command) => command switch
    {
        PcCommand.Volume => "level",
        PcCommand.Launch => "app",
        _ => null,
    };

    /// <summary>
    /// Parses a 0–100 volume level leniently: integers, decimals (rounded), a decimal
    /// comma and an optional trailing '%' are all accepted — HA templates render levels
    /// in several of these shapes, and a strict integer parse made valid automations
    /// look "blocked" (issue #6). Out-of-range values clamp instead of failing.
    /// </summary>
    public static bool TryParseLevel(string? raw, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var s = raw.Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
            return false;
        level = Math.Clamp((int)Math.Round(value), 0, 100);
        return true;
    }
}
