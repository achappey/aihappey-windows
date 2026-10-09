using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public interface IDesktopVideoClient
{
    Task<JsonObject> StartAsync(JsonObject request, string partition, CancellationToken ct);
    Task<JsonObject> StatusAsync(string operation, string partition, CancellationToken ct);
}

/// <summary>Start is deliberately never retried. Each status request obtains fresh host authentication.</summary>
public sealed class DesktopVideoClient(DesktopChatClient gateway, HttpClient http) : IDesktopVideoClient
{
    public Task<JsonObject> StartAsync(JsonObject request, string partition, CancellationToken ct) => SendAsync(HttpMethod.Post, "api/videos", request, partition, ct);
    public static string StatusPath(string operation)
    {
        var split = operation.IndexOf('/');
        if (split <= 0 || split == operation.Length - 1 || operation.Length > 262144) throw new InvalidOperationException(DesktopResources.Get("VideoResponseInvalid"));
        return "api/videos/" + Uri.EscapeDataString(operation[..split]) + "/" + Uri.EscapeDataString(operation[(split + 1)..]);
    }
    public Task<JsonObject> StatusAsync(string operation, string partition, CancellationToken ct) => SendAsync(HttpMethod.Get, StatusPath(operation), null, partition, ct);
    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, string partition, CancellationToken ct)
    {
        using var request = await gateway.RequestAsync(ServiceKind.Ai, method, path, ct, partition);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DesktopChatClient.CheckResponse(response);
        return await VideoDisk.ReadObjectAsync(await response.Content.ReadAsStreamAsync(ct), method == HttpMethod.Post ? 4 * 1024 * 1024 : VideoDisk.MaximumJsonBytes, ct);
    }
}
