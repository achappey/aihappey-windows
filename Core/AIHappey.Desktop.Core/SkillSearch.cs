using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public sealed record SkillSearchResult(string Query, IReadOnlyList<string> Keywords, int TotalMatches, IReadOnlyList<DesktopSkill> Skills);

/// <summary>Browser keyword coverage/relevance ranking. Search loads descriptors only, never instruction bodies.</summary>
public static class DesktopSkillSearch
{
    private static readonly HashSet<string> stopWords = new(("a about agent agents an and any are as available be best can capability capabilities capable could current discover do does find for from get give handle help i in is kind like look looking me my need needed needs of on or please query search skill skills some support task tasks that the this to tool tools use using want wants we what which who with would").Split(' '), StringComparer.Ordinal);
    public static string Normalize(string? text)
    {
        text = Regex.Replace(text ?? "", "([a-z0-9])([A-Z])", "$1 $2").Normalize(NormalizationForm.FormKD);
        return string.Concat(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).Trim().ToLowerInvariant();
    }
    private static string[] Tokens(string text, bool dropStopWords = false) => Regex.Split(Normalize(text), "[^a-z0-9]+")
        .Where(t => t.Length > 1 || t.Length > 0 && t.All(char.IsAsciiDigit)).Where(t => !dropStopWords || !stopWords.Contains(t)).Distinct().ToArray();
    private static int Score(string token, string? value, int weight)
    {
        var normalized = Normalize(value); if (normalized.Length == 0) return 0;
        if (normalized == token) return weight * 6;
        var tokens = Tokens(normalized);
        if (tokens.Contains(token)) return weight * 4;
        if (normalized.Contains(token, StringComparison.Ordinal)) return weight * 2;
        return tokens.Any(t => t.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(t, StringComparison.Ordinal)) ? weight : 0;
    }
    public static SkillSearchResult Find(IEnumerable<DesktopSkill> skills, string query, int limit = 10)
    {
        var normalized = Normalize(query); var tokens = Tokens(normalized, true); var catalog = skills.ToArray();
        var matches = tokens.Length == 0 ? catalog : catalog.Select((skill, index) =>
        {
            var fields = new[] { (skill.Id, 12), (skill.Name, 10), (skill.Description, 4), (skill.Origin, 2), (skill.Version, 1), (skill.LatestVersion, 1) };
            var scores = tokens.Select(t => fields.Max(f => Score(t, f.Item1, f.Item2))).ToArray();
            return new { skill, index, matched = scores.Count(s => s > 0), score = scores.Sum() };
        }).Where(s => s.matched > 0).OrderByDescending(s => s.matched).ThenByDescending(s => s.score).ThenBy(s => s.index).Select(s => s.skill).ToArray();
        return new(normalized, tokens, matches.Length, matches.Take(Math.Clamp(limit, 1, 50)).ToArray());
    }
    public static JsonElement Call(IEnumerable<DesktopSkill> catalog, JsonElement input)
    {
        var limit = input.TryGetProperty("limit", out var raw) && raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var n)
            && double.IsFinite(n) ? (int)Math.Clamp(Math.Floor(n), 1, 50) : 10;
        var result = Find(catalog, CatalogProjection.Text(input, "query") ?? "", limit);
        var lines = result.Skills.Select(s => $"- id={s.Id}; skill_id={s.Id}; name={s.Name}{(s.Version is null ? "" : " v" + s.Version)}, {s.Origin}: {s.Description}");
        var text = $"<skill_search query=\"{SecurityElement.Escape(result.Query)}\" total_matches=\"{result.TotalMatches}\" returned=\"{result.Skills.Count}\">\n"
            + (result.Skills.Count > 0 ? string.Join('\n', lines) : "No matching skills found.")
            + "\nUse activate_skill with a returned skill_id before following any skill instructions.\n</skill_search>";
        return DesktopLocalTools.Result(new { skillSearch = new { query = result.Query, keywords = result.Keywords, totalMatches = result.TotalMatches,
            returned = result.Skills.Count, skills = result.Skills.Select(s => new { id = s.Id, skill_id = s.Id, name = s.Name, description = s.Description,
                origin = s.Origin, version = s.Version, defaultVersion = s.DefaultVersion, latestVersion = s.LatestVersion, isDownloaded = s.IsDownloaded }) } }, DesktopLocalTools.Text(text));
    }
}
