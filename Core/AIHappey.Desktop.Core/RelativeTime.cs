namespace AIHappey.Desktop.Core;

public static class RelativeTime
{
    /// <summary>Resource keys keep grammar in native translations; now is injectable for boundary tests.</summary>
    public static string Format(DateTimeOffset timestamp, DateTimeOffset now, Func<string, string> resource)
    {
        var elapsed = now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return resource("TimeJustNow");
        var (unit, count) = elapsed.TotalMinutes < 60 ? ("Minute", (int)elapsed.TotalMinutes)
            : elapsed.TotalHours < 24 ? ("Hour", (int)elapsed.TotalHours)
            : elapsed.TotalDays < 7 ? ("Day", (int)elapsed.TotalDays)
            : elapsed.TotalDays < 30 ? ("Week", (int)(elapsed.TotalDays / 7))
            : elapsed.TotalDays < 365 ? ("Month", (int)(elapsed.TotalDays / 30))
            : ("Year", (int)(elapsed.TotalDays / 365));
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            resource("Time" + unit + (count == 1 ? "Ago" : "sAgo")), count);
    }
}
