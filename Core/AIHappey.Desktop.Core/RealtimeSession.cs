using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

public interface IRealtimeSession : IAsyncDisposable
{
    ChannelReader<JsonObject> Events { get; }
    Task StartAsync(CancellationToken ct);
    Task SendAsync(JsonObject message, CancellationToken ct);
    Task SetMutedAsync(bool muted);
    Task SendVideoAsync(byte[] jpeg, CancellationToken ct);
}

public abstract class RealtimeSession : IRealtimeSession
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    protected readonly CancellationTokenSource Lifetime = new();
    private readonly Channel<JsonObject> events = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public ChannelReader<JsonObject> Events => events.Reader;
    protected void Emit(JsonObject value)
    {
        if (!Lifetime.IsCancellationRequested && !events.Writer.TryWrite(value)) Fail(new InvalidOperationException("Realtime event queue exceeded its limit."));
    }
    protected void Fail(Exception error)
    {
        events.Writer.TryComplete(error);
        Lifetime.Cancel();
    }
    protected void Complete() => events.Writer.TryComplete();
    public static JsonObject Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumMessageBytes) throw new JsonException("Realtime message exceeded its limit.");
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new JsonException("Expected a realtime object.");
    }
    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task SendAsync(JsonObject message, CancellationToken ct);
    public abstract Task SetMutedAsync(bool muted);
    public virtual Task SendVideoAsync(byte[] jpeg, CancellationToken ct) => throw new NotSupportedException();
    public abstract ValueTask DisposeAsync();
}
