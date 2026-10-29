using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public interface IStreamingSpeechSession : IAsyncDisposable
{
    Task AppendAsync(string text, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
    Task FinishAsync(CancellationToken ct);
    Task CancelAsync();
}

/// <summary>One block owns one socket. All sends are ordered; close/cancel can never affect another block.</summary>
public sealed class StreamingSpeechSession : IStreamingSpeechSession
{
    private readonly WebSocket socket;
    private readonly StreamingSpeechProtocol protocol;
    private readonly Action<byte[]> audio;
    private readonly CancellationTokenSource lifetime;
    private readonly SemaphoreSlim sending = new(1);
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<DateTimeOffset> flushes = new();
    private Task receiving = Task.CompletedTask, flushing = Task.CompletedTask;
    private volatile bool dirty, finishing, finalInputSent;
    private bool disposed;
    private int sent, acknowledged;
    public StreamingSpeechSession(WebSocket socket, StreamingSpeechProtocol protocol, Action<byte[]> audio, CancellationToken ct)
    {
        this.socket = socket; this.protocol = protocol; this.audio = audio; lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = done.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
    public void Start() { receiving = ReceiveAsync(); flushing = FlushLoopAsync(); }
    public static async Task<StreamingSpeechSession> OpenAsync(StreamingSpeechProtocol protocol, string token, Action<byte[]> audio, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        var endpoint = protocol.Endpoint;
        if (protocol.Provider == "deepgram") { socket.Options.AddSubProtocol("bearer"); socket.Options.AddSubProtocol(token); }
        else endpoint = new(endpoint + "&access_token=" + Uri.EscapeDataString(token));
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(endpoint, startup.Token);
            var result = new StreamingSpeechSession(socket, protocol, audio, ct); result.Start(); return result;
        }
        catch { socket.Dispose(); throw new InvalidOperationException("Streaming speech connection failed."); }
    }
    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (done.Task.IsFaulted) await done.Task;
        if (socket.State != WebSocketState.Open) throw new InvalidOperationException("Streaming speech connection closed.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct);
    }
    public async Task AppendAsync(string text, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try
        {
            if (finishing) throw new InvalidOperationException("Speech input is complete.");
            for (var offset = 0; offset < text.Length;)
            {
                var count = Math.Min(1000, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
                await SendAsync(protocol.Append(text.Substring(offset, count)), ct); offset += count; dirty = true;
            }
        }
        finally { sending.Release(); }
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        if (!protocol.CanFlush) return;
        await sending.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            while (flushes.TryPeek(out var first) && now - first >= TimeSpan.FromSeconds(60)) flushes.Dequeue();
            if (!dirty || flushes.Count >= 20 || flushes.Count > 0 && now - flushes.Last() < TimeSpan.FromMilliseconds(3200)) return;
            // Count before sending: the receive worker may acknowledge immediately.
            Interlocked.Increment(ref sent);
            await SendAsync(protocol.Finish(), ct); flushes.Enqueue(now); dirty = false;
        }
        finally { sending.Release(); }
    }
    private async Task FlushLoopAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { await Task.Delay(200, lifetime.Token); if (!finishing) await FlushAsync(lifetime.Token); } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { done.TrySetException(new InvalidOperationException("Streaming speech send failed.")); }
    }
    public async Task FinishAsync(CancellationToken ct)
    {
        finishing = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        if (protocol.CanFlush)
        {
            while (dirty) { await FlushAsync(timeout.Token); if (dirty) await Task.Delay(100, timeout.Token); }
            finalInputSent = true;
            if (Volatile.Read(ref acknowledged) >= Volatile.Read(ref sent)) done.TrySetResult();
        }
        else { await sending.WaitAsync(timeout.Token); try { finalInputSent = true; await SendAsync(protocol.Finish(), timeout.Token); } finally { sending.Release(); } }
        await done.Task.WaitAsync(timeout.Token);
    }
    private async Task ReceiveAsync()
    {
        var buffer = new byte[16384];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream(); ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (part.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException();
                    if (message.Length + part.Count > 512 * 1024) throw new InvalidOperationException();
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                if (lifetime.IsCancellationRequested) return;
                if (part.MessageType == WebSocketMessageType.Binary) { audio(message.ToArray()); continue; }
                var value = JsonNode.Parse(message.ToArray())!.AsObject();
                if (value["type"]?.ToString() is "error" or "Error" or "Warning") throw new InvalidOperationException();
                if (protocol.Audio(value) is { } encoded) audio(Convert.FromBase64String(encoded));
                if (protocol.Done(value))
                {
                    if (protocol.CanFlush)
                    {
                        var sequence = value["sequence_id"]?.GetValue<int>() ?? throw new InvalidOperationException();
                        Interlocked.Exchange(ref acknowledged, Math.Max(Volatile.Read(ref acknowledged), sequence + 1));
                    }
                    if (finalInputSent && (!protocol.CanFlush || Volatile.Read(ref acknowledged) >= Volatile.Read(ref sent))) done.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { done.TrySetCanceled(lifetime.Token); }
        catch { done.TrySetException(new InvalidOperationException("Streaming speech receive failed.")); }
    }
    public async Task CancelAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await sending.WaitAsync(timeout.Token); try { if (socket.State == WebSocketState.Open) await SendAsync(protocol.Cancel(), timeout.Token); } finally { sending.Release(); } } catch { }
        await DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); socket.Abort();
        try { await Task.WhenAll(receiving, flushing); } catch { }
        socket.Dispose(); lifetime.Dispose(); sending.Dispose();
    }
}
