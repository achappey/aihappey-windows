namespace AIHappey.Desktop.Core;

/// <summary>Port of OpenAI Agents SDK's headerless V4A applyDiff utility.
/// MIT licensed; see ApplyPatchOpenAILicense.txt. Keeps the reference's anchor,
/// whitespace matching, EOF and trailing-newline behavior. No filesystem access.</summary>
public static class V4ADiff
{
    private sealed record Chunk(int Index, string[] Deleted, string[] Inserted);
    public static string Apply(string input, string diff, bool create = false)
    {
        var lines = diff.Replace("\r\n", "\n").Split('\n').Select(s => s.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        if (create)
        {
            var output = new List<string>();
            foreach (var line in lines)
            {
                if (Terminator(line)) break;
                if (!line.StartsWith('+')) throw new LocalToolException("Invalid Add File Line: " + line);
                output.Add(line[1..]);
            }
            return string.Join('\n', output);
        }
        var crlf = input.Contains('\n') && !input.Replace("\r\n", "").Contains('\n');
        var source = (crlf ? input.Replace("\r\n", "\n") : input).Split('\n');
        lines.Add("*** End Patch");
        var chunks = new List<Chunk>(); var index = 0; var cursor = 0;
        while (index < lines.Count && !Terminator(lines[index]) && lines[index] != "*** End of File")
        {
            var anchors = new List<string>(); var count = 0;
            while (index < lines.Count && (lines[index] == "@@" || lines[index].StartsWith("@@ ", StringComparison.Ordinal)))
            {
                var anchor = lines[index].Length > 3 ? lines[index][3..] : "";
                index++; count++; if (!string.IsNullOrWhiteSpace(anchor)) anchors.Add(anchor);
            }
            if (count == 0 && cursor != 0) throw new LocalToolException("Invalid patch section.");
            for (var a = 0; a < anchors.Count; a++)
            {
                var anchor = anchors[a]; var found = false;
                foreach (var trim in new[] { false, true })
                {
                    bool Equal(string s) => trim ? s.Trim() == anchor.Trim() : s == anchor;
                    if (a == 0 && source.Take(cursor).Any(Equal)) { found = true; break; }
                    for (var s = cursor; s < source.Length; s++)
                        if (Equal(source[s])) { cursor = s + 1; found = true; break; }
                    if (found) break;
                }
                if (count > 1 && !found) throw new LocalToolException("Invalid Anchor: " + anchor);
            }
            var context = new List<string>(); var deleted = new List<string>(); var inserted = new List<string>();
            var local = new List<Chunk>(); var sectionStart = index; var mode = ' ';
            void Flush()
            {
                if (deleted.Count == 0 && inserted.Count == 0) return;
                local.Add(new(context.Count - deleted.Count, deleted.ToArray(), inserted.ToArray())); deleted.Clear(); inserted.Clear();
            }
            while (index < lines.Count)
            {
                var raw = lines[index];
                if (raw.StartsWith("@@", StringComparison.Ordinal) || Terminator(raw) || raw == "*** End of File" || raw == "***") break;
                if (raw.StartsWith("***", StringComparison.Ordinal)) throw new LocalToolException("Invalid Line: " + raw);
                index++;
                var line = raw.Length == 0 ? " " : raw; var nextMode = line[0];
                if (nextMode is not (' ' or '+' or '-')) throw new LocalToolException("Invalid Line: " + raw);
                if (nextMode == ' ' && mode != ' ') Flush();
                mode = nextMode;
                if (mode == '-') { deleted.Add(line[1..]); context.Add(line[1..]); }
                else if (mode == '+') inserted.Add(line[1..]);
                else context.Add(line[1..]);
            }
            Flush();
            var eof = index < lines.Count && lines[index] == "*** End of File";
            if (eof) index++;
            else if (index == sectionStart) throw new LocalToolException("Empty patch section.");
            var search = eof && source.Length > 1 && source[^1] == "" ? source[..^1] : source;
            var position = eof ? Find(search, context, Math.Max(0, search.Length - context.Count)) : -1;
            if (position < 0) position = Find(search, context, Math.Min(cursor, search.Length));
            if (position < 0) throw new LocalToolException((eof ? "Invalid EOF Context: " : "Invalid Context: ") + string.Join('\n', context));
            chunks.AddRange(local.Select(c => c with { Index = c.Index + position }));
            cursor = position + context.Count;
        }
        var result = new List<string>(); var originalIndex = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Index > source.Length || originalIndex > chunk.Index) throw new LocalToolException("Overlapping or invalid patch hunks.");
            result.AddRange(source[originalIndex..chunk.Index]); result.AddRange(chunk.Inserted);
            originalIndex = chunk.Index + chunk.Deleted.Length;
        }
        result.AddRange(source[originalIndex..]);
        return string.Join(crlf ? "\r\n" : "\n", result);
    }

    private static bool Terminator(string line) => new[] { "*** End Patch", "*** Update File:", "*** Delete File:", "*** Add File:" }
        .Any(marker => line.StartsWith(marker, StringComparison.Ordinal));

    private static int Find(string[] source, List<string> context, int start)
    {
        if (context.Count == 0) return start;
        foreach (var trim in new Func<string, string>[] { s => s, s => s.TrimEnd(), s => s.Trim() })
            for (var index = start; index + context.Count <= source.Length; index++)
                if (context.Select((line, i) => trim(source[index + i]) == trim(line)).All(equal => equal)) return index;
        return -1;
    }
}
