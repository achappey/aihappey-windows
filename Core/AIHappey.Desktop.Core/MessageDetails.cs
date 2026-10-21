using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed record MessageSource(string Id, string Title, string? Url, string? Filename)
{
    public string? Host => AttachmentDownloads.RemoteUri(Url)?.Host;
}

public sealed record MessageAttachment(string Name, string MediaType, string? Url = null, string? Base64 = null, string? Text = null, string? ResourceUri = null)
{
    public string Identity => AttachmentDownloads.Identity(this);
}

public sealed record TranscriptRow(ConversationMessage Message, TranscriptBlock Block, IReadOnlyList<MessageSource> Sources, IReadOnlyList<MessageAttachment> Attachments);

/// <summary>Derived footer data, never copied into or removed from portable history.
/// Sources follow their next answer; late citations and all attachments belong to the final
/// assistant answer of the current turn. Nothing is attached across a user/system boundary.</summary>
public static class MessageDetails
{
    public static IReadOnlyList<TranscriptRow> Project(IReadOnlyList<ConversationMessage> messages)
    {
        var result = new List<TranscriptRow>();
        var turn = new List<ConversationMessage>();
        void Flush()
        {
            if (turn.Count == 0) return;
            var rows = new List<TranscriptRow>();
            var pendingSources = new List<MessageSource>();
            var attachments = new List<MessageAttachment>();
            var finalText = -1;
            foreach (var message in turn)
            {
                var superseded = SupersededImages(message.Message.Parts);
                foreach (var part in message.Message.Parts.Where(part => !superseded.Contains(part))) attachments.AddRange(Attachments(part));
                var blocks = TranscriptProjection.Project(message);
                // Relate source positions to the original part indices, without changing message boundaries.
                var sourceIndex = 0;
                foreach (var block in blocks)
                {
                    var partIndex = block.Parts.Count > 0 ? message.Message.Parts.IndexOf(block.Parts[0]) : message.Message.Parts.Count;
                    while (sourceIndex <= partIndex && sourceIndex < message.Message.Parts.Count)
                    {
                        if (Source(message.Message.Parts[sourceIndex]) is { } source) pendingSources.Add(source);
                        sourceIndex++;
                    }
                    var isText = !block.Activity && (block.Parts.FirstOrDefault()?.Type == "text" || block.Images?.Count > 0);
                    rows.Add(new(message, block, isText ? DistinctSources(pendingSources) : [], []));
                    if (isText) { pendingSources.Clear(); finalText = rows.Count - 1; }
                }
                while (sourceIndex < message.Message.Parts.Count)
                {
                    if (Source(message.Message.Parts[sourceIndex++]) is { } source) pendingSources.Add(source);
                }
            }
            // Tool/file-only responses still get one usable footer without a raw file/source card.
            if (finalText < 0 && (pendingSources.Count > 0 || attachments.Count > 0))
            {
                var message = turn[^1];
                rows.Add(new(message, new(message.Message.Id + ":details", false, []), [], []));
                finalText = rows.Count - 1;
            }
            if (finalText >= 0)
            {
                var row = rows[finalText];
                rows[finalText] = row with { Sources = DistinctSources(row.Sources.Concat(pendingSources)),
                    Attachments = attachments.DistinctBy(a => a.Identity).ToArray() };
            }
            result.AddRange(rows); turn.Clear();
        }
        foreach (var message in messages)
        {
            if (message.Message.Role == Role.assistant) { turn.Add(message); continue; }
            Flush();
            if (message.Message.Role == Role.user)
            {
                var blocks = TranscriptProjection.Project(message);
                var superseded = SupersededImages(message.Message.Parts);
                var files = message.Message.Parts.Where(part => !superseded.Contains(part)).SelectMany(Attachments).DistinctBy(a => a.Identity).ToArray();
                if (blocks.Count == 0 && files.Length > 0) blocks = new[] { new TranscriptBlock(message.Message.Id + ":details", false, []) };
                for (var index = 0; index < blocks.Count; index++) result.Add(new(message, blocks[index], [], index == blocks.Count - 1 ? files : []));
            }
        }
        Flush(); return result;
    }

    private static MessageSource[] DistinctSources(IEnumerable<MessageSource> sources) => sources.DistinctBy(s => s.Url ?? s.Id).ToArray();
    public static MessageSource? Source(UIMessagePart part)
    {
        if (part.Type is not "source-url" and not "source-document") return null;
        var raw = PortableConversations.Element(part);
        var url = PortableConversations.String(raw, "url");
        var filename = PortableConversations.String(raw, "filename");
        var id = PortableConversations.String(raw, "sourceId") ?? url ?? filename ?? "Document";
        return new(id, PortableConversations.String(raw, "title") ?? filename ?? url ?? id, url, filename);
    }

    public static IReadOnlyList<MessageAttachment> Attachments(UIMessagePart part)
    {
        var raw = PortableConversations.Element(part);
        if (part.Type == "file") return new[] { File(raw) };
        if (!PortableConversations.IsTool(part) || !raw.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];
        var files = new List<MessageAttachment>();
        foreach (var item in content.EnumerateArray())
        {
            var type = PortableConversations.String(item, "type");
            var mime = PortableConversations.String(item, "mimeType") ?? "application/octet-stream";
            if (type is "image" or "audio" && PortableConversations.String(item, "data") is { } binary)
                files.Add(new(PortableConversations.String(item, "name") ?? type + AttachmentDownloads.Extension(mime), mime, Base64: binary));
            else if (type == "resource" && item.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
            {
                var uri = PortableConversations.String(resource, "uri");
                var mediaType = PortableConversations.String(resource, "mimeType") ?? "application/octet-stream";
                var blob = PortableConversations.String(resource, "blob");
                var text = PortableConversations.String(resource, "text");
                files.Add(new(PortableConversations.String(resource, "name") ?? AttachmentDownloads.NameFromUri(uri, mediaType), mediaType,
                    Url: blob is null && text is null ? uri : null, Base64: blob, Text: text, ResourceUri: uri));
            }
            else if (type == "resource_link")
            {
                var uri = PortableConversations.String(item, "uri");
                files.Add(new(PortableConversations.String(item, "name") ?? AttachmentDownloads.NameFromUri(uri, mime), mime, Url: uri, ResourceUri: uri));
            }
            else if (type == "file") files.Add(File(item));
        }
        return files;
    }

    private static MessageAttachment File(JsonElement item)
    {
        var url = PortableConversations.String(item, "url");
        var binary = PortableConversations.String(item, "data");
        if (url is null && binary?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        { url = binary; binary = null; }
        var mime = PortableConversations.String(item, "mediaType") ?? PortableConversations.String(item, "mimeType")
            ?? ProviderString(item, "media_type") ?? DataMediaType(url) ?? "application/octet-stream";
        var name = PortableConversations.String(item, "filename") ?? PortableConversations.String(item, "name")
            ?? ProviderString(item, "filename") ?? AttachmentDownloads.NameFromUri(url, mime);
        // The browser accepts plain base64 in file URLs too. Keep it as binary rather than
        // interpreting '/' at the start (e.g. JPEG /9j/) as a filesystem/relative URL.
        if (url is not null && !url.Contains(':')) { binary = url; url = null; }
        return new(name, mime, url, Base64: binary);
    }

    public static bool IsImage(MessageAttachment file) => file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string? DataMediaType(string? url) => url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true
        && url.IndexOfAny([';', ','], 5) is var end && end > 5 ? url[5..end] : null;

    private static string? ProviderString(JsonElement item, string name)
    {
        if (!item.TryGetProperty("providerMetadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        return metadata.EnumerateObject().Select(p => PortableConversations.String(p.Value, name)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    }

    public static string? GeneratedImageKey(UIMessagePart part)
    {
        if (part.Type != "file") return null;
        var raw = PortableConversations.Element(part);
        if (!raw.TryGetProperty("providerMetadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        foreach (var scoped in metadata.EnumerateObject())
        {
            var id = PortableConversations.String(scoped.Value, "generated_image_id") ?? PortableConversations.String(scoped.Value, "generatedImageId");
            if (!string.IsNullOrWhiteSpace(id)) return JsonSerializer.Serialize(new[] { scoped.Name, id });
        }
        return null;
    }

    public static HashSet<UIMessagePart> SupersededImages(IReadOnlyList<UIMessagePart> parts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new HashSet<UIMessagePart>();
        for (var index = parts.Count - 1; index >= 0; index--)
            if (parts[index].Type == "file" && Attachments(parts[index]).Any(IsImage)
                && GeneratedImageKey(parts[index]) is { } key && !seen.Add(key)) result.Add(parts[index]);
        return result;
    }
}
