// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Automations;

/// <summary>Matches fired Windows triggers against rules (enable state is the engine's job).</summary>
public static class RuleMatcher
{
    private static readonly System.Buffers.SearchValues<char> PathSeparators =
        System.Buffers.SearchValues.Create(['\\', '/']);

    /// <summary>"C:\...\POWERPNT.EXE" → "powerpnt" (lowercase, no directory, no extension).</summary>
    public static string NormalizeProcessName(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return string.Empty;
        var name = nameOrPath.Trim();
        var cut = name.AsSpan().LastIndexOfAny(PathSeparators);
        if (cut >= 0)
            name = name[(cut + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// True when the fired trigger key applies to the rule. Process params compare
    /// normalized; idle thresholds are handled upstream by the engine's edge detector,
    /// which passes the crossed threshold as <paramref name="param"/>.
    /// </summary>
    public static bool Matches(AutomationRule rule, string triggerKey, string? param)
    {
        if (!string.Equals(rule.Trigger, triggerKey, StringComparison.Ordinal))
            return false;
        if (!WindowsTriggers.TryParse(triggerKey, out var trigger))
            return false;

        return WindowsTriggers.ParamKind(trigger) switch
        {
            TriggerParamKind.ProcessName =>
                NormalizeProcessName(rule.Param ?? "") == NormalizeProcessName(param ?? "")
                && NormalizeProcessName(rule.Param ?? "").Length > 0,
            TriggerParamKind.Minutes =>
                rule.IdleMinutes is { } threshold
                && int.TryParse(param, out var fired)
                && threshold == fired,
            _ => true,
        };
    }
}
