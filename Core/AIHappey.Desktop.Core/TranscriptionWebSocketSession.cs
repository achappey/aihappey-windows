using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

/// <summary>Transcription-only WebSockets. One send owner; microphone starts after provider readiness.</summary>
public sealed class TranscriptionWebSocketSession(string provider, JsonObject? setup,
    Func<CancellationToken, Task<string>> tokenFactory, ITranscriptionAudioInput input) : RealtimeSession
{
    private readonly Channel<byte[]> audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly TaskCompletionSource finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiveTask, sendTask;
    private IDisposable? subscription;
    private int disposed;
    private volatile bool stopping;
    public static Uri ConnectionUri(string provider, string credential)
    {
        if (provider == "google") return new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContentConstrained?access_token=" + Uri.EscapeDataString(credential));
        if (provider != "gladia" || !Uri.TryCreate(credential, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !(uri.Host == "api.gladia.io" || uri.Host.EndsWith(".gladia.io", StringComparison.OrdinalIgnoreCase)))
            throw new RealtimeFailureException("transcription-url", "The gateway returned an invalid transcription connection URL.");
        return uri;
    }
    public override async Task StartAsync(CancellationToken ct)
    {
        if (input.SampleRate != 16000) throw new ArgumentException("WebSocket transcription requires 16-kHz PCM input.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var credential = await tokenFactory(timeout.Token).ConfigureAwait(false);
            await socket.ConnectAsync(ConnectionUri(provider, credential), timeout.Token).ConfigureAwait(false);
            if (provider == "google")
            {
                await SendAsync(new() { ["setup"] = setup?.DeepClone() }, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var message = await GoogleRealtimeSession.ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
                    if (message is null) throw new RealtimeFailureException("transcription-setup", "The provider closed the connection before transcription was ready.");
                    CheckError(message);
                    if (message["setupComplete"] is not null) break;
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            receiveTask = ReceiveAsync(); sendTask = SendAudioAsync();
            subscription = input.Subscribe(bytes =>
            {
                if (!stopping && !Lifetime.IsCancellationRequested && !audio.Writer.TryWrite(bytes.ToArray()))
                    Fail(new RealtimeFailureException("transcription-backlog", "The connection cannot keep up with microphone audio. Transcription was stopped to avoid losing speech."));
            }, Fail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { throw RealtimeFailureException.Safe(error, "transcription-connect", "Could not connect to the transcription provider. Check the selected model and provider credentials."); }
    }
    private static void CheckError(JsonObject message)
    {
        if (message["error"] is not null || message["type"]?.ToString() == "error"
            || message["type"]?.ToString() == "conversation.item.input_audio_transcription.failed")
            throw new RealtimeFailureException("transcription-provider", "The provider reported a transcription error.");
    }
    private async Task ReceiveAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                var message = await GoogleRealtimeSession.ReceiveAsync(socket, Lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    if (stopping) { finalized.TrySetResult(); return; }
                    throw new RealtimeFailureException("transcription-disconnected", "The transcription provider disconnected unexpectedly.");
                }
                CheckError(message); Emit(message);
                if (stopping && (provider == "google" && message["serverContent"]?["inputTranscription"] is not null
                    || provider == "gladia" && message["type"]?.ToString() is "end_session" or "post_final_transcript")) finalized.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error) { finalized.TrySetResult(); Fail(RealtimeFailureException.Safe(error, "transcription-receive", "The transcription connection failed.")); }
    }
    private async Task SendAudioAsync()
    {
        try
        {
            await foreach (var bytes in audio.Reader.ReadAllAsync(Lifetime.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (provider == "google") await SendAsync(new() { ["realtimeInput"] = new JsonObject { ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = "audio/pcm;rate=16000" } } }, timeout.Token).ConfigureAwait(false);
                else await SendBytesAsync(bytes, WebSocketMessageType.Binary, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch { Fail(new RealtimeFailureException("transcription-upload", "Microphone audio could not be sent to the transcription provider.")); }
    }
    private async Task SendBytesAsync(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, ct).ConfigureAwait(false); }
        finally { sendGate.Release(); }
    }
    public override Task SendAsync(JsonObject message, CancellationToken ct) => SendBytesAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, ct);
    public override Task SetMutedAsync(bool muted) => throw new NotSupportedException();
    public async Task FinishInputAsync()
    {
        stopping = true; Interlocked.Exchange(ref subscription, null)?.Dispose(); audio.Writer.TryComplete();
        if (Lifetime.IsCancellationRequested || socket.State != WebSocketState.Open) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        if (sendTask is not null) await sendTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        await SendAsync(provider == "google" ? new() { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }
            : new() { ["type"] = "stop_recording" }, timeout.Token).ConfigureAwait(false);
        try { await finalized.Task.WaitAsync(TimeSpan.FromMilliseconds(1500), timeout.Token).ConfigureAwait(false); } catch (TimeoutException) { }
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping = true; Lifetime.Cancel(); Interlocked.Exchange(ref subscription, null)?.Dispose(); audio.Writer.TryComplete(); socket.Abort();
        if (receiveTask is not null) await receiveTask.ConfigureAwait(false);
        if (sendTask is not null) await sendTask.ConfigureAwait(false);
        socket.Dispose(); Complete();
    }
}
