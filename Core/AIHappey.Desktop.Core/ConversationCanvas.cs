using System.Globalization;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record CanvasVersion(string MessageId, DateTimeOffset Timestamp, int MessageIndex, int PartIndex, string Text);
public sealed record CanvasDocument(string Uri, IReadOnlyList<CanvasVersion> Versions)
{
    public string Title => ConversationCanvasProjection.Title(Uri);
}

/// <summary>Read-only browser-compatible projection of embedded Markdown tool resources. No resource reads or tool execution.</summary>
public static class ConversationCanvasProjection
{
    public static IReadOnlyList<CanvasDocument> Project(IReadOnlyList<ConversationMessage> messages)
    {
        var groups = new Dictionary<string, Dictionary<string, CanvasVersion>>(StringComparer.Ordinal);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            for (var partIndex = 0; partIndex < message.Message.Parts.Count; partIndex++)
            {
                var part = message.Message.Parts[partIndex];
                if (!PortableConversations.IsTool(part) || part.Type == "tool-call") continue;
                var raw = PortableConversations.Element(part);
                if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("output", out var output)
                    || output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind == JsonValueKind.Array)
                    foreach (var item in content.EnumerateArray()) Add(item);
                else Add(content);

                void Add(JsonElement item)
                {
                    if (item.ValueKind != JsonValueKind.Object || PortableConversations.String(item, "type") != "resource"
                        || !item.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object
                        || PortableConversations.String(resource, "mimeType") != "text/markdown"
                        || PortableConversations.String(resource, "uri") is not { } uri || string.IsNullOrWhiteSpace(uri)
                        || PortableConversations.String(resource, "text") is not { } text) return;
                    if (!groups.TryGetValue(uri, out var versions)) groups[uri] = versions = new(StringComparer.Ordinal);
                    // As in the browser, one version per document per message: last tool part wins.
                    // Multiple matching blocks in that same part retain the first block.
                    if (!versions.TryGetValue(message.Message.Id, out var previous) || partIndex > previous.PartIndex)
                        versions[message.Message.Id] = new(message.Message.Id, message.Timestamp, messageIndex, partIndex, text);
                }
            }
        }
        return groups.Select(group => new CanvasDocument(group.Key, group.Value.Values
            .OrderByDescending(version => version.Timestamp).ThenByDescending(version => version.MessageIndex)
            .ThenByDescending(version => version.PartIndex).ToArray())).ToArray();
    }

    public static string Title(string uri)
    {
        var path = System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri.Replace('\\', '/');
        var name = path[(path.LastIndexOf('/') + 1)..].Trim();
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return string.IsNullOrWhiteSpace(name) ? uri : name;
    }

    public static string FormatTimestamp(DateTimeOffset timestamp, CultureInfo? culture = null)
        => timestamp == default || timestamp == DateTimeOffset.UnixEpoch ? "-"
            : timestamp.ToLocalTime().ToString("g", culture ?? CultureInfo.CurrentCulture);

    // Tool resources can use arbitrary MCP schemes. Never activate those as Windows protocols.
    public static Uri? ExternalUri(string uri) => System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(parsed.Host) && parsed.UserInfo.Length == 0 ? parsed : null;
}

/// <summary>Ephemeral selection only; never persisted or shared across conversations/accounts.</summary>
public sealed class ConversationCanvasSelection
{
    private (string Partition, string Conversation)? scope;
    private readonly Dictionary<string, (CanvasVersion Latest, CanvasVersion Selected)> selections = new(StringComparer.Ordinal);

    public bool SetScope(string partition, string conversation)
    {
        if (scope == (partition, conversation)) return false;
        scope = (partition, conversation); selections.Clear(); return true;
    }

    public void Synchronize(IReadOnlyList<CanvasDocument> documents)
    {
        var uris = documents.Select(document => document.Uri).ToHashSet(StringComparer.Ordinal);
        foreach (var uri in selections.Keys.Where(uri => !uris.Contains(uri)).ToArray()) selections.Remove(uri);
        foreach (var document in documents)
        {
            if (document.Versions.Count == 0) { selections.Remove(document.Uri); continue; }
            var latest = document.Versions[0];
            if (!selections.TryGetValue(document.Uri, out var previous) || previous.Latest != latest
                || !document.Versions.Contains(previous.Selected)) selections[document.Uri] = (latest, latest);
        }
    }

    public CanvasVersion Current(CanvasDocument document) => selections.TryGetValue(document.Uri, out var state)
        && document.Versions.Contains(state.Selected) ? state.Selected : document.Versions[0];

    public void Select(CanvasDocument document, CanvasVersion version)
    {
        if (document.Versions.Contains(version)) selections[document.Uri] = (document.Versions[0], version);
    }
}
