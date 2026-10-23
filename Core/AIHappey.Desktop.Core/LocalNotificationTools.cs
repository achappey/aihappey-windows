using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AIHappey.Desktop.Core;

public sealed record LocalNotificationRequest(string Title, string Body);

/// <summary>Immediate local notifications only; no scheduling, actions or retained notification records.</summary>
public sealed class LocalNotificationTools(Func<LocalNotificationRequest, CancellationToken, Task<uint>> show,
    Func<bool>? isCurrent = null)
{
    public const string ShowTool = "local_notifications_show";
    public const int MaximumTitleCharacters = 200;
    public const int MaximumBodyCharacters = 2000;

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name != ShowTool) throw new LocalToolException("Unsupported notification tool.");
        string Text(string field, int maximum)
        {
            if (!input.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > maximum)
                throw new LocalToolException(DesktopResources.Get("LocalNotificationTextInvalid"));
            try { XmlConvert.VerifyXmlChars(text); }
            catch (XmlException) { throw new LocalToolException(DesktopResources.Get("LocalNotificationTextInvalid")); }
            return text;
        }
        var request = new LocalNotificationRequest(Text("title", MaximumTitleCharacters), Text("body", MaximumBodyCharacters));
        Check(ct); var id = await show(request, ct); Check(ct);
        if (id == 0) throw new LocalToolException(DesktopResources.Get("LocalNotificationUnavailable"));
        // Do not echo the notification text into metadata or unfilterable structuredContent.
        return DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(new { submitted = true, notificationId = id }, JsonSerializerOptions.Web)));
    }, ct);

    internal static string Payload(LocalNotificationRequest request) => new XElement("toast",
        new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", request.Title), new XElement("text", request.Body)))).ToString(SaveOptions.DisableFormatting);
}
