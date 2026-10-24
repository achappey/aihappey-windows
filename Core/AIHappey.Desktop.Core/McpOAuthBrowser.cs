using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Authentication;

namespace AIHappey.Desktop.Core;

/// <summary>A native-app callback bound only to IPv4 loopback. No HTTP.sys registration or app protocol required.</summary>
internal sealed class McpOAuthBrowser : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Action<Uri> launch;
    public Uri RedirectUri { get; }
    internal McpOAuthBrowser(Action<Uri>? launch = null)
    {
        this.launch = launch ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        listener.Start(4);
        RedirectUri = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/oauth-callback/{Guid.NewGuid():N}");
    }

    internal async Task<AuthorizationResult> AuthorizeAsync(Uri authorizationUri, CancellationToken ct)
    {
        var expected = Query(authorizationUri.Query).GetValueOrDefault("state");
        if (string.IsNullOrEmpty(expected)) throw new InvalidOperationException("Missing OAuth state.");
        _ = McpValidation.Endpoint(authorizationUri.GetLeftPart(UriPartial.Path));
        launch(authorizationUri);
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestBudget.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await using var stream = client.GetStream();
                // Bound all request bytes; never echo query strings, codes or server-supplied errors.
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 16384)
                {
                    if (await stream.ReadAsync(one, requestBudget.Token) == 0) break;
                    bytes.Add(one[0]);
                    if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                var parts = lines[0].Split(' ');
                var valid = parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith('/')
                    && Uri.TryCreate(RedirectUri, parts[1], out var callback)
                    && callback.GetLeftPart(UriPartial.Path) == RedirectUri.AbsoluteUri
                    && lines.Skip(1).Any(l => l.Equals("Host: " + RedirectUri.Authority, StringComparison.OrdinalIgnoreCase));
                var query = valid ? Query(new Uri(RedirectUri, parts[1]).Query) : [];
                valid &= query.GetValueOrDefault("state") == expected;
                var denied = valid && query.ContainsKey("error");
                valid &= denied || !string.IsNullOrEmpty(query.GetValueOrDefault("code"));
                var text = valid ? DesktopResources.Get(denied ? "McpBrowserDenied" : "McpBrowserComplete") : "Invalid authorization callback.";
                var body = Encoding.UTF8.GetBytes(text);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(header, requestBudget.Token); await stream.WriteAsync(body, requestBudget.Token);
                if (!valid) continue;
                if (denied) throw new McpSignInRequiredException();
                return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query.GetValueOrDefault("iss") };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static Dictionary<string, string> Query(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (!result.TryAdd(key, Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' ')))) return [];
        }
        return result;
    }
    public void Dispose() => listener.Stop();
}
