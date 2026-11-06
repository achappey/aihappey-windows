using System.Collections.Concurrent;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Disposable V1 model documents, not serialized UI projections or user metadata.</summary>
public sealed class DesktopModelCatalogStore(string directory)
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private string PathFor(string scope) => Path.Combine(directory, McpValidation.Hash(scope) + ".json");

    public static bool ValidResponse(JsonElement response) => response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
        && data.EnumerateArray().All(model => model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()));

    public async Task<JsonElement?> LoadAsync(string scope, CancellationToken ct)
    {
        try
        {
            var path = PathFor(scope);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            await using var stream = File.OpenRead(path);
            using var content = new StreamContent(stream);
            using var document = JsonDocument.Parse(await DesktopCatalogClient.ReadBoundedAsync(content, MaxBytes, ct));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object
                || !sources.TryGetProperty("gateway", out var source) || source.ValueKind != JsonValueKind.Object
                || !source.TryGetProperty("updatedAt", out var updated) || !updated.TryGetInt64(out var time) || time < 0
                || !source.TryGetProperty("response", out var response) || !ValidResponse(response)) return null;
            return response.Clone();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or GatewayException or InvalidOperationException)
        { return null; }
    }

    public async Task SaveAsync(string scope, JsonElement response, CancellationToken ct)
    {
        if (!ValidResponse(response)) return;
        var path = PathFor(scope); var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1,
                sources = new { gateway = new { updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), response } } });
            if (bytes.Length > MaxBytes) return;
            Directory.CreateDirectory(directory); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Cache failure is not discovery failure. */ }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            gate.Release();
        }
    }

    public async Task ClearAsync(string scope, CancellationToken ct)
    {
        var path = PathFor(scope); var gate = Gates.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(ct);
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { gate.Release(); }
    }
}
