using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

/// <summary>One block owns one output-only GA socket. Ordered out-of-band responses never accumulate conversation history.</summary>
public sealed class OpenAiRealtimeSpeechSession : IStreamingSpeechSession
{
    private readonly WebSocket socket;
    private readonly JsonObject config;
    private readonly Action<byte[]> audio;
    private readonly Action? failed;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration cancellation;
    private readonly SemaphoreSlim gate = new(1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task receiving = Task.CompletedTask;
    private string pending = "";
    private string? requestId, responseId;
    private int responseBytes, disposed, faulted;
    private bool finishing, forceFlush;
    private Timer? responseTimeout;

    public OpenAiRealtimeSpeechSession(WebSocket socket, JsonObject config, Action<byte[]> audio, CancellationToken ct, Action? failed = null)
    {
        this.socket = socket; this.config = (JsonObject)config.DeepClone(); this.audio = audio; this.failed = failed;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ready.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = done.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        cancellation = lifetime.Token.Register(() =>
        {
            ready.TrySetCanceled(lifetime.Token); done.TrySetCanceled(lifetime.Token); socket.Abort();
            if (!done.Task.IsCompletedSuccessfully) failed?.Invoke();
        });
    }
    public static async Task<OpenAiRealtimeSpeechSession> OpenAsync(JsonObject config, string token, Action<byte[]> audio, CancellationToken ct, Action? failed = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!Regex.IsMatch(token, @"^ek_[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("OpenAI speech requires a short-lived Realtime client secret.");
        var socket = new ClientWebSocket(); OpenAiRealtimeSpeechSession? session = null;
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(config["model"]!.GetValue<string>())), startup.Token);
            session = new(socket, config, audio, ct, failed); await session.StartAsync(startup.Token); return session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (session is not null) await session.DisposeAsync(); else socket.Dispose(); throw; }
        catch { if (session is not null) await session.DisposeAsync(); else socket.Dispose(); throw new InvalidOperationException("OpenAI speech session setup failed."); }
    }
    public async Task StartAsync(CancellationToken ct)
    {
        receiving = ReceiveAsync();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); startup.CancelAfter(TimeSpan.FromSeconds(15));
        var update = (JsonObject)config.DeepClone(); update.Remove("model");
        try
        {
            await gate.WaitAsync(startup.Token);
            try { await SendAsync(new() { ["type"] = "session.update", ["session"] = update }, startup.Token); }
            finally { gate.Release(); }
            await ready.Task.WaitAsync(startup.Token);
        }
        catch { Fail("OpenAI speech session setup failed."); throw; }
    }
    private void Fail(string message)
    {
        if (Volatile.Read(ref disposed) != 0 || done.Task.IsCompletedSuccessfully || Interlocked.Exchange(ref faulted, 1) != 0) return;
        var error = new InvalidOperationException(message); ready.TrySetException(error); done.TrySetException(error);
        lifetime.Cancel();
    }
    private async Task CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (done.Task.IsFaulted) await done.Task;
        lifetime.Token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }
    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await CheckAsync(ct);
        if (socket.State != WebSocketState.Open) throw new InvalidOperationException("OpenAI speech connection is unavailable.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct);
    }
    public async Task AppendAsync(string text, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await CheckAsync(ct);
            if (finishing) throw new InvalidOperationException("OpenAI speech input is closed.");
            if (pending.Length + text.Length > 128_000) throw new InvalidOperationException("OpenAI speech text backlog exceeded.");
            pending += text; await PumpAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Fail("OpenAI speech send failed."); throw; }
        finally { gate.Release(); }
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await CheckAsync(ct); forceFlush = true; await PumpAsync(ct); }
        catch { Fail("OpenAI speech send failed."); throw; }
        finally { gate.Release(); }
    }
    public async Task FinishAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await CheckAsync(ct); finishing = true; await PumpAsync(ct); }
        catch { Fail("OpenAI speech send failed."); throw; }
        finally { gate.Release(); }
        await done.Task.WaitAsync(ct);
        // Stop receiving without clearing the output device; the caller still drains queued PCM.
        lifetime.Cancel();
    }
    private async Task PumpAsync(CancellationToken ct)
    {
        if (!ready.Task.IsCompletedSuccessfully || requestId is not null) return;
        string text;
        do
        {
            var count = OpenAiRealtimeSpeech.BatchLength(pending, finishing || forceFlush);
            if (count == 0) { if (finishing && pending.Length == 0) done.TrySetResult(); return; }
            text = pending[..count]; pending = pending[count..];
        } while (string.IsNullOrWhiteSpace(text));
        requestId = Guid.NewGuid().ToString("N"); responseId = null; responseBytes = 0;
        responseTimeout?.Dispose();
        var currentRequest = requestId;
        responseTimeout = new Timer(_ => { if (Volatile.Read(ref requestId) == currentRequest) Fail("OpenAI speech response timed out."); }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        await SendAsync(new()
        {
            ["type"] = "response.create", ["response"] = new JsonObject
            {
                ["conversation"] = "none", ["output_modalities"] = new JsonArray("audio"), ["instructions"] = config["instructions"]?.DeepClone(),
                ["tools"] = new JsonArray(), ["tool_choice"] = "none", ["metadata"] = new JsonObject { ["speech_request"] = requestId },
                ["input"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }) })
            }
        }, ct);
    }
    private async Task ReceiveAsync()
    {
        var bytes = new byte[16384]; var ct = lifetime.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var buffer = new MemoryStream(); ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(bytes.AsMemory(), ct);
                    if (part.MessageType != WebSocketMessageType.Text || buffer.Length + part.Count > 1024 * 1024) throw new InvalidOperationException();
                    buffer.Write(bytes, 0, part.Count);
                } while (!part.EndOfMessage);
                var message = JsonNode.Parse(buffer.ToArray())!.AsObject();
                await gate.WaitAsync(ct);
                try { ct.ThrowIfCancellationRequested(); await HandleAsync(message, ct); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { Fail("OpenAI Realtime speech failed."); }
    }
    private async Task HandleAsync(JsonObject message, CancellationToken ct)
    {
        var type = message["type"]?.ToString();
        if (type == "error") throw new InvalidOperationException(); // Never report upstream bodies/secrets.
        if (type == "session.updated")
        {
            if (ready.Task.IsCompletedSuccessfully) return;
            var session = message["session"];
            if (session?["type"]?.ToString() != "realtime" || session["audio"]?["output"]?["format"]?["type"]?.ToString() != "audio/pcm"
                || session["audio"]?["output"]?["format"]?["rate"]?.GetValue<int>() != OpenAiRealtimeSpeech.SampleRate
                || session["audio"]?["input"]?["turn_detection"] is not null
                || session["output_modalities"] is not JsonArray modalities || modalities.Count != 1 || modalities[0]?.ToString() != "audio"
                || session["tools"] is JsonArray tools && tools.Count != 0
                || session["audio"]?["output"]?["voice"]?.ToString() != config["audio"]!["output"]!["voice"]!.ToString()) throw new InvalidOperationException();
            ready.TrySetResult(); return;
        }
        if (!ready.Task.IsCompletedSuccessfully || requestId is null || done.Task.IsCompleted) return;
        if (type == "response.created")
        {
            if (message["response"]?["metadata"]?["speech_request"]?.ToString() != requestId) return;
            var id = message["response"]?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || responseId is not null && responseId != id) throw new InvalidOperationException();
            responseId = id; return;
        }
        if (responseId is null) return;
        if (type == "response.output_audio.delta" && message["response_id"]?.ToString() == responseId)
        {
            var data = Convert.FromBase64String(message["delta"]!.GetValue<string>());
            responseBytes += data.Length;
            if (responseBytes > 4 * 1024 * 1024) throw new InvalidOperationException();
            if (data.Length > 0) audio(data);
        }
        if (type == "response.done" && message["response"]?["id"]?.ToString() == responseId)
        {
            if (message["response"]?["status"]?.ToString() != "completed" || responseBytes == 0 || responseBytes % 2 != 0) throw new InvalidOperationException();
            responseTimeout?.Dispose(); responseTimeout = null; requestId = responseId = null;
            await PumpAsync(ct);
        }
    }
    public async Task CancelAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await gate.WaitAsync(timeout.Token);
            try { if (responseId is not null && socket.State == WebSocketState.Open) await SendAsync(new() { ["type"] = "response.cancel", ["response_id"] = responseId }, timeout.Token); }
            finally { gate.Release(); }
        }
        catch { /* Closing the block-owned socket cancels unacknowledged requests too. */ }
        await DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); responseTimeout?.Dispose(); socket.Abort();
        try { await receiving; } catch { }
        pending = ""; requestId = responseId = null;
        cancellation.Dispose(); socket.Dispose(); lifetime.Dispose(); gate.Dispose();
    }
}
