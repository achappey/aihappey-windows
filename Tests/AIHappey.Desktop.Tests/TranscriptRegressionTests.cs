using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using AIHappey.Desktop.Core;

internal static class TranscriptRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        Dictionary<string, object> Metadata(string json) => JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var (value, expected) in new[] { (0d, "0.00"), (.001d, "0.00>"), (.0049d, "0.00>"), (.005d, "0.01"), (.0123d, "0.01"), (1.005d, "1.01"), (42.125d, "42.13") })
            check(MessageUsage.FormatCost(value) == expected, $"price matches browser formatting for {value}");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            check(MessageUsage.FormatCost(value) is null, "non-finite prices omitted");
        foreach (var tokens in new[] { 0, -1, 17 })
        {
            var usage = MessageUsage.Read(Metadata("{\"usage\":{\"totalTokens\":" + tokens + "},\"providerMetadata\":{\"gateway\":{\"cost\":0.001}}}"), "test/model", timestamp);
            check(usage.Tokens == (tokens > 0 ? "17" : null) && usage.Price == "0.00>", "zero/negative tokens omitted independently of cost: " + tokens);
        }
        check(MessageUsage.Read(Metadata("""{"totalTokens":17,"gateway":{"cost":0}}"""), "test/model", timestamp) == ("17", "0.00"), "legacy metadata keeps tokens and zero cost");
        check(MessageUsage.Read(Metadata("{}"), "test/model", timestamp) == (null, null), "missing metadata omits both usage badges");
        foreach (var gateway in new[] { "null", "[]", "\"invalid\"", "{}", "{\"cost\":null}", "{\"cost\":\"0.12\"}", "{\"cost\":{}}", "{\"cost\":1e999}" })
            check(MessageUsage.Read(Metadata("{\"usage\":{\"totalTokens\":17},\"providerMetadata\":{\"gateway\":" + gateway + "}}"), "test/model", timestamp) == ("17", null), "invalid/missing cost preserves positive tokens: " + gateway);
        check(MessageUsage.Read(Metadata("""{"usage":"invalid"}"""), "test/model", timestamp) == (null, null), "malformed optional usage does not break rendering");
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
            check(values["MessagePrice"].Contains("{0}"), language + ": accessible price badge label translated");
        }
    }
}
