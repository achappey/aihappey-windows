namespace AIHappey.Desktop.Core;

/// <summary>Composer dictation never falls back to a different model or uses chat provider metadata.</summary>
public static class ComposerDictation
{
    public static ChatTarget? DefaultModel(DesktopSettings settings, IEnumerable<ChatTarget> catalog)
    {
        var id = settings.AiModels.TranscriptionModel;
        if (string.IsNullOrWhiteSpace(id)) return null;
        return catalog.FirstOrDefault(model => model.Id == id && model.ModelType == "transcription"
            && ProviderPreferences.IsEnabled(settings, "transcription", ModelOverviewCatalog.ProviderKey(model)));
    }

    public static string InsertText(string draft, int start, int length, string text, out int caret)
    {
        start = Math.Clamp(start, 0, draft.Length); length = Math.Clamp(length, 0, draft.Length - start);
        text = text.Trim();
        if (text.Length == 0) { caret = start; return draft; }
        var before = draft[..start]; var after = draft[(start + length)..];
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && !char.IsPunctuation(text[0])) text = " " + text;
        if (after.Length > 0 && !char.IsWhiteSpace(after[0]) && !char.IsPunctuation(after[0])) text += " ";
        caret = before.Length + text.Length;
        return before + text + after;
    }
}
