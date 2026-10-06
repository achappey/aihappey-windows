using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record TranscriptBlock(string Key, bool Activity, IReadOnlyList<UIMessagePart> Parts);

/// <summary>Presentation only. Never merge messages or reorder/mutate their portable parts.</summary>
public static class TranscriptProjection
{
    public static IReadOnlyList<TranscriptBlock> Project(ConversationMessage message)
    {
        var blocks = new List<TranscriptBlock>();
        var run = new List<UIMessagePart>();
        var start = 0;
        void Flush()
        {
            if (run.Count == 0) return;
            blocks.Add(new(message.Message.Id + ":activity:" + start, true, run.ToArray())); run.Clear();
        }
        for (var index = 0; index < message.Message.Parts.Count; index++)
        {
            var part = message.Message.Parts[index];
            if (part.Type is "step-start" or "file" or "source-url" or "source-document" || part.Type == "reasoning" && string.IsNullOrWhiteSpace(PortableConversations.Text(part))) continue;
            if (part.Type == "reasoning" || PortableConversations.IsTool(part))
            {
                if (run.Count == 0) start = index;
                run.Add(part); continue;
            }
            Flush(); blocks.Add(new(message.Message.Id + ":part:" + index, false, new[] { part }));
        }
        Flush();
        if (blocks.Count == 0 && message.Status != "complete") blocks.Add(new(message.Message.Id + ":pending", false, []));
        return blocks;
    }
}
