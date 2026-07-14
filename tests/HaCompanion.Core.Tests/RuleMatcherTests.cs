// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Automations;
using Xunit;

namespace HaCompanion.Core.Tests;

public class RuleMatcherTests
{
    private static AutomationRule Rule(string trigger, string? param = null) =>
        new(trigger, param, new[] { new RuleAction("light.buero", AutomationActions.TurnOff) });

    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("NOTEPAD", "notepad")]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("NOTEPAD.EXE", "notepad")]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData("/usr/bin/thing", "thing")]
    [InlineData("  spaced.exe ", "spaced")]
    [InlineData("", "")]
    public void NormalizeProcessName_strips_path_extension_case(string input, string expected) =>
        Assert.Equal(expected, RuleMatcher.NormalizeProcessName(input));

    [Fact]
    public void Plain_trigger_matches_on_key_alone()
    {
        Assert.True(RuleMatcher.Matches(Rule("lock"), "lock", null));
        Assert.False(RuleMatcher.Matches(Rule("lock"), "unlock", null));
    }

    [Theory]
    [InlineData("powerpnt", "POWERPNT.EXE", true)]
    [InlineData("powerpnt.exe", "powerpnt", true)]
    [InlineData("powerpnt", "excel", false)]
    [InlineData("", "anything", false)] // empty rule param must never wildcard-match
    public void Process_trigger_compares_normalized_names(string ruleParam, string firedParam, bool expected) =>
        Assert.Equal(expected, RuleMatcher.Matches(Rule("app_start", ruleParam), "app_start", firedParam));

    [Theory]
    [InlineData("15", "15", true)]
    [InlineData("15", "30", false)]
    [InlineData("abc", "15", false)]
    public void Idle_trigger_matches_its_own_threshold(string ruleParam, string firedParam, bool expected) =>
        Assert.Equal(expected, RuleMatcher.Matches(Rule("idle_start", ruleParam), "idle_start", firedParam));

    [Fact]
    public void Disabled_state_is_not_the_matchers_business()
    {
        // the engine filters IsEnabled; the matcher matches regardless
        var disabled = Rule("lock") with { IsEnabled = false };
        Assert.True(RuleMatcher.Matches(disabled, "lock", null));
    }
}
