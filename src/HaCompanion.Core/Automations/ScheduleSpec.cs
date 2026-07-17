// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;

namespace HaCompanion.Core.Automations;

/// <summary>
/// The parameter of a <see cref="WindowsTrigger.Schedule"/> rule: a time of day plus the
/// weekdays it fires on. Serialized into <c>AutomationRule.Param</c> as <c>"HH:mm;days"</c>
/// where days are ISO weekday digits 1–7 (Mon=1 … Sun=7); an empty day set means every day.
/// Examples: <c>"07:00;12345"</c> (weekdays 07:00), <c>"22:30;"</c> (every day 22:30).
/// </summary>
public sealed record ScheduleSpec(TimeOnly Time, IReadOnlyList<int> Days)
{
    /// <summary>Serialize back into the <c>Param</c> string form.</summary>
    public string ToParam() =>
        Time.ToString("HH:mm", CultureInfo.InvariantCulture) + ";" + string.Concat(Days);

    /// <summary>True when <paramref name="now"/> falls on this schedule's minute and weekday.</summary>
    public bool Matches(DateTime now)
    {
        if (now.Hour != Time.Hour || now.Minute != Time.Minute)
            return false;
        return Days.Count == 0 || Days.Contains(IsoDay(now.DayOfWeek));
    }

    public static bool TryParse(string? param, out ScheduleSpec spec)
    {
        spec = new ScheduleSpec(default, []);
        if (string.IsNullOrWhiteSpace(param))
            return false;

        var parts = param.Split(';');
        if (!TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return false;

        var days = new List<int>();
        if (parts.Length > 1)
        {
            foreach (var c in parts[1])
            {
                if (c is < '1' or > '7')
                    return false;
                var d = c - '0';
                if (!days.Contains(d))
                    days.Add(d);
            }
        }
        days.Sort();
        spec = new ScheduleSpec(time, days);
        return true;
    }

    /// <summary>ISO weekday number: Monday = 1 … Sunday = 7.</summary>
    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
}
