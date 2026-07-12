// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Models;

/// <summary>
/// Parses human-readable hotkey combos ("Win+Ctrl+H") into Win32 RegisterHotKey arguments.
/// Lives in Core so the parsing rules are unit-testable without any Windows UI dependency.
/// </summary>
public static class HotkeyCombo
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// Parse a combo like "Ctrl+Alt+K" into modifier flags and a virtual-key code.
    /// Requires at least one modifier and a main key of A–Z, 0–9, Space or F1–F12.
    /// </summary>
    public static bool TryParse(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo))
            return false;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "win":
                case "windows":
                case "meta":
                    modifiers |= ModWin;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                default:
                    return false;
            }
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
            vk = key[0];
        else if (key is "SPACE")
            vk = 0x20;
        else if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 12)
            vk = (uint)(0x70 + n - 1);
        else
            return false;

        return modifiers != 0;
    }
}
