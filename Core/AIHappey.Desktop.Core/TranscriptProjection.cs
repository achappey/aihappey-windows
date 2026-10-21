using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record TranscriptBlock(string Key, bool Activity, IReadOnlyList<UIMessagePart> Parts,
    IReadOnlyList<MessageAttachment>? Images = null);

/// <summary>Presentation only. Never merge messages or reorder/mutate their portable parts.</summary>
public static class TranscriptProjection
{
    public static IReadOnlyList<TranscriptBlock> Project(ConversationMessage message)
    {
        var blocks = new List<TranscriptBlock>();
        var run = new List<UIMessagePart>();
        var superseded = MessageDetails.SupersededImages(message.Message.Parts);
        var start = 0;
        void Flush()
        {
            if (run.Count == 0) return;
            blocks.Add(new(message.Message.Id + ":activity:" + start, true, run.ToArray())); run.Clear();
        }
        for (var index = 0; index < message.Message.Parts.Count; index++)
        {
            var part = message.Message.Parts[index];
            if (superseded.Contains(part)) continue;
            var images = MessageDetails.Attachments(part).Where(MessageDetails.IsImage).ToArray();
            if (part.Type == "file" && images.Length > 0)
            {
                Flush();
                blocks.Add(new(message.Message.Id + ":image:" + (MessageDetails.GeneratedImageKey(part) ?? index.ToString()), false, [part], images));
                continue;
            }
            if (part.Type is "step-start" or "file" or "source-url" or "source-document" || part.Type == "reasoning" && string.IsNullOrWhiteSpace(PortableConversations.Text(part))) continue;
            if (part.Type == "reasoning" || PortableConversations.IsTool(part))
            {
                if (run.Count == 0) start = index;
                run.Add(part);
                if (images.Length > 0)
                {
                    Flush();
                    blocks.Add(new(message.Message.Id + ":tool-images:" + index, false, [part], images));
                }
                continue;
            }
            Flush(); blocks.Add(new(message.Message.Id + ":part:" + index, false, new[] { part }));
        }
        Flush();
        // Errors are shown only by the top notification bar. Preserve partial answer/activity
        // blocks, but do not render an empty failed assistant card or a misleading Busy label.
        if (blocks.Count == 0 && message.Status is not "complete" and not "failed" && message.ErrorMessage is null)
            blocks.Add(new(message.Message.Id + ":pending", false, []));
        return blocks;
    }
}
