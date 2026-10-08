using System.Reflection;
using System.Xml.Linq;
using AIHappey.Desktop.Core;

internal static class TranscriptRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Desktop.Resources." + language)!;
            var values = XDocument.Load(stream).Root!.Elements("data")
                .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value);
            string Format(TimeSpan elapsed) => RelativeTime.Format(now - elapsed, now, key => values[key]);
            foreach (var (elapsed, key, count) in new[]
            {
                (TimeSpan.FromSeconds(-30), "TimeJustNow", 0),
                (TimeSpan.FromSeconds(59), "TimeJustNow", 0),
                (TimeSpan.FromMinutes(1), "TimeMinuteAgo", 1),
                (TimeSpan.FromMinutes(2), "TimeMinutesAgo", 2),
                (TimeSpan.FromMinutes(59), "TimeMinutesAgo", 59),
                (TimeSpan.FromHours(1), "TimeHourAgo", 1),
                (TimeSpan.FromHours(2), "TimeHoursAgo", 2),
                (TimeSpan.FromDays(1), "TimeDayAgo", 1),
                (TimeSpan.FromDays(2), "TimeDaysAgo", 2),
                (TimeSpan.FromDays(7), "TimeWeekAgo", 1),
                (TimeSpan.FromDays(14), "TimeWeeksAgo", 2),
                (TimeSpan.FromDays(30), "TimeMonthAgo", 1),
                (TimeSpan.FromDays(60), "TimeMonthsAgo", 2),
                (TimeSpan.FromDays(365), "TimeYearAgo", 1),
                (TimeSpan.FromDays(730), "TimeYearsAgo", 2)
            }) check(Format(elapsed) == string.Format(values[key], count), $"{language}: relative time {elapsed} selects translated singular/plural unit");
            check(Format(TimeSpan.FromMinutes(2)) == (language == "en" ? "2 minutes ago" : "2 minuten geleden"), language + ": relative time uses real translations");
            check(values["GeneratedByAi"].Length > 0 && values["GeneratedByAiWarning"].Length > 0, language + ": AI disclosure translations present");
        }
    }
}
