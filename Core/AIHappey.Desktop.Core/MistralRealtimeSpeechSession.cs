using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>A block-owned Mistral output socket. Completion requires generation acks and normal server close.</summary>
public sealed class MistralRealtimeSpeechSession : IStreamingSpeechSession
{
    private readonly WebSocket socket;
    private readonly JsonObject config;
    private readonly Action<byte[], int> audio;
    private readonly Action failed;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration cancellation;
    private readonly SemaphoreSlim sending = new(1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task receiving = Task.CompletedTask;
    private Task? finishing;
    private bool created, configured, disposed;
    private volatile bool ending, dirty;
    private int submitted, completed, sampleRate, faulted;
    private long bytesReceived;
    private StreamingWaveDecoder? decoder;

    public MistralRealtimeSpeechSession(WebSocket socket, JsonObject config, Action<byte[], int> audio, CancellationToken ct, Action? failed = null)
    {
        this.socket = socket; this.config = (JsonObject)config.DeepClone(); this.audio = audio; this.failed = failed ?? (() => { });
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ready.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = done.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        cancellation = ct.Register(() => Fail("Mistral speech was cancelled."));
    }
    public void Start() => receiving = ReceiveAsync();
    public Task WaitReadyAsync(CancellationToken ct) => ready.Task.WaitAsync(ct);
    public static async Task<MistralRealtimeSpeechSession> OpenAsync(JsonObject config, string token, Action<byte[], int> audio, CancellationToken ct, Action? failed = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!token.StartsWith("rt_", StringComparison.Ordinal) || token.Length <= 3
            || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
            throw new InvalidOperationException("Mistral requires a short-lived realtime token.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        MistralRealtimeSpeechSession? session = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new("wss://api.mistral.ai/v1/audio/speech/realtime?model=voxtral-mini-tts-3-0"), startup.Token);
            session = new(socket, config, audio, ct, failed); session.Start();
            await session.WaitReadyAsync(startup.Token); return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync(); else socket.Dispose();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Mistral speech connection or session setup failed.");
        }
    }
    private void Fail(string message = "Mistral returned invalid speech audio or incompatible session settings.")
    {
        if (Interlocked.Exchange(ref faulted, 1) != 0) return;
        var error = new InvalidOperationException(message);
        ready.TrySetException(error); done.TrySetException(error); failed(); socket.Abort();
    }
    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
        if (done.Task.IsFaulted) await done.Task;
        if (disposed || socket.State != WebSocketState.Open) throw new InvalidOperationException("Mistral speech connection is closed.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct);
    }
    public async Task AppendAsync(string text, CancellationToken ct)
    {
        await ready.Task.WaitAsync(ct); await sending.WaitAsync(ct);
        try
        {
            if (ending) throw new InvalidOperationException("Speech input is complete.");
            for (var offset = 0; offset < text.Length;)
            {
                var count = Math.Min(1000, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
                dirty = true;
                await SendAsync(new() { ["type"] = "input_text.append", ["text"] = text.Substring(offset, count) }, ct); offset += count;
            }
        }
        finally { sending.Release(); }
    }
    public async Task FlushAsync(CancellationToken ct)
    {
        await ready.Task.WaitAsync(ct); await sending.WaitAsync(ct);
        try
        {
            if (ending) throw new InvalidOperationException("Speech input is complete.");
            if (!dirty) return;
            Interlocked.Increment(ref submitted); dirty = false;
            await SendAsync(new() { ["type"] = "input_text.flush" }, ct);
        }
        finally { sending.Release(); }
    }
    public Task FinishAsync(CancellationToken ct)
    {
        lock (ready) return finishing ??= FinishCoreAsync(ct);
    }
    private async Task FinishCoreAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await ready.Task.WaitAsync(timeout.Token); await sending.WaitAsync(timeout.Token);
            try
            {
                ending = true; if (dirty) { Interlocked.Increment(ref submitted); dirty = false; }
                await SendAsync(new() { ["type"] = "input_text.end" }, timeout.Token);
            }
            finally { sending.Release(); }
            await done.Task.WaitAsync(timeout.Token);
        }
        catch { Fail("Mistral speech did not complete."); throw; }
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
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        if (!ending || socket.CloseStatus != WebSocketCloseStatus.NormalClosure
                            || Volatile.Read(ref completed) != Volatile.Read(ref submitted) || decoder is not null) throw new InvalidOperationException();
                        await sending.WaitAsync(lifetime.Token);
                        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", lifetime.Token); }
                        finally { sending.Release(); }
                        done.TrySetResult(); return;
                    }
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 1024 * 1024) throw new InvalidOperationException();
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                if (lifetime.IsCancellationRequested) return;
                var value = JsonNode.Parse(message.ToArray())!.AsObject();
                var type = value["type"]?.GetValue<string>();
                if (type == "error") { Fail("Mistral reported a speech error. Check the selected model and settings."); return; }
                if (type == "session.created")
                {
                    if (created) throw new InvalidOperationException();
                    created = true; await sending.WaitAsync(lifetime.Token);
                    try { await SendAsync(new() { ["type"] = "session.update", ["session"] = config.DeepClone() }, lifetime.Token); }
                    finally { sending.Release(); }
                }
                else if (type == "session.updated")
                {
                    var applied = value["session"]?.AsObject();
                    if (!created || configured || applied?["response_format"]?.ToString() != "wav"
                        || applied["streaming_mode"]?.ToString() != config["streaming_mode"]?.ToString()
                        || config["voice"] is { } voice && applied["voice"]?.ToString() != voice.ToString()) throw new InvalidOperationException();
                    configured = true; ready.TrySetResult();
                }
                else if (type == "speech.audio.delta")
                {
                    if (!configured || Volatile.Read(ref completed) >= Volatile.Read(ref submitted) && !dirty) throw new InvalidOperationException();
                    var encoded = value["audio_data"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(encoded)) throw new InvalidOperationException();
                    var bytes = Convert.FromBase64String(encoded); bytesReceived += bytes.Length;
                    if (bytesReceived > 100 * 1024 * 1024) throw new InvalidOperationException();
                    decoder ??= new((pcm, rate) =>
                    {
                        if (sampleRate != 0 && sampleRate != rate) throw new InvalidOperationException();
                        sampleRate = rate; audio(pcm, rate);
                    });
                    decoder.Push(bytes);
                }
                else if (type == "speech.audio.done")
                {
                    if (!configured || Volatile.Read(ref completed) >= Volatile.Read(ref submitted)) throw new InvalidOperationException();
                    decoder?.Finish(); decoder = null; Interlocked.Increment(ref completed);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { Fail("Mistral speech was cancelled."); }
        catch { Fail(); }
    }
    public async Task CancelAsync() => await DisposeAsync(); // Close only: Mistral has no documented cancel event.
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        cancellation.Dispose(); lifetime.Cancel(); socket.Abort();
        ready.TrySetCanceled(); done.TrySetCanceled();
        try { await receiving; } catch { }
        socket.Dispose(); lifetime.Dispose(); sending.Dispose();
    }
}
