namespace AIHappey.Desktop.Core;

/// <summary>Pure text operations shared by execution and history-only Canvas reconstruction.</summary>
internal static class LocalDocumentEdits
{
    private static readonly HashSet<string> TextExtensions = new(("bat c cc cmd conf cpp cs css csv cxx go h hpp htm html ini java js json jsx less log md markdown mdx mjs php py rb rs scss sh sql text toml ts tsx tsv txt xml yaml yml").Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static bool IsTextFile(string name) => TextExtensions.Contains(Path.GetExtension(name).TrimStart('.'));
    internal static string? CanvasMimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "text/markdown", ".txt" or ".text" => "text/plain", _ => null
    };

    internal static string Replace(string text, string oldText, string newText, bool unique)
    {
        if (oldText.Length == 0) throw new LocalToolException("old_str must be a non-empty string.");
        var first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) throw new LocalToolException("No match found for replacement. Check your text and try again.");
        if (unique && text.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            throw new LocalToolException("Multiple matches found. Provide more context to make a unique match.");
        return text[..first] + newText + text[(first + oldText.Length)..];
    }

    internal static string Insert(string text, int line, string inserted, bool anthropic)
    {
        var lines = (anthropic && text.Length == 0 ? [] : text.Split('\n')).ToList();
        if (anthropic)
        {
            if (line < 0 || line > lines.Count) throw new LocalToolException("insert_line is out of range.");
            lines.InsertRange(line, inserted.Split('\n'));
        }
        else lines.Insert(Math.Min(Math.Max(1, line) - 1, lines.Count), inserted);
        return string.Join('\n', lines);
    }
}
