// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Models;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HotkeyComboTests
{
    [Theory]
    [InlineData("Win+Ctrl+H", HotkeyCombo.ModWin | HotkeyCombo.ModControl, 'H')]
    [InlineData("ctrl+alt+k", HotkeyCombo.ModControl | HotkeyCombo.ModAlt, 'K')]
    [InlineData("Shift+9", HotkeyCombo.ModShift, '9')]
    [InlineData("Ctrl+Space", HotkeyCombo.ModControl, 0x20)]
    [InlineData("Alt+F1", HotkeyCombo.ModAlt, 0x70)]
    [InlineData("Alt+F12", HotkeyCombo.ModAlt, 0x7B)]
    public void Parses_valid_combos(string combo, uint expMods, int expVk)
    {
        Assert.True(HotkeyCombo.TryParse(combo, out var mods, out var vk));
        Assert.Equal(expMods, mods);
        Assert.Equal((uint)expVk, vk);
    }

    [Theory]
    [InlineData("")]           // empty
    [InlineData("H")]          // no modifier
    [InlineData("Ctrl+")]      // no main key
    [InlineData("Ctrl+F13")]   // F13 unsupported
    [InlineData("Foo+H")]      // unknown modifier
    [InlineData("Ctrl+Esc")]   // unsupported main key
    public void Rejects_invalid_combos(string combo) =>
        Assert.False(HotkeyCombo.TryParse(combo, out _, out _));
}
