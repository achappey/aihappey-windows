using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

public sealed class GoogleRealtimeSession(JsonObject setup, Func<string?, CancellationToken, Task<string>> tokenFactory) : RealtimeSession
{
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContentConstrained";
    private readonly SemaphoreSlim sendGate = new(1);
    // Never accumulate stale microphone audio or turn a short network stall into a terminal failure.
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private ClientWebSocket? socket;
    private RealtimeAudio? audio;
    private Task? receiveTask, sendTask;
    private string? handle;
    private string responseId = Guid.NewGuid().ToString("N");
    private string userText = "", assistantText = "";
    private int disposed;
    private volatile bool reconnecting;
    private readonly object audioGate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async Task StartAsync(CancellationToken ct)
    {
        receiveTask = RunAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); Lifetime.Token.ThrowIfCancellationRequested();
        audio = new RealtimeAudio(16000, 24000, bytes =>
        {
            if (!reconnecting && !Lifetime.IsCancellationRequested) input.Writer.TryWrite(bytes.ToArray());
        }, Fail, Emit);
        sendTask = SendAudioAsync(); audio.Start();
    }
    private async Task RunAsync()
    {
        var attempts = 0;
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var credential = await tokenFactory(handle, connectTimeout.Token).ConfigureAwait(false);
                await ws.ConnectAsync(new Uri(Endpoint + "?access_token=" + Uri.EscapeDataString(credential)), connectTimeout.Token).ConfigureAwait(false);
                var nextSetup = (JsonObject)setup.DeepClone();
                if (handle is not null) nextSetup["sessionResumption"] = new JsonObject { ["handle"] = handle };
                await SendToAsync(ws, new JsonObject { ["setup"] = nextSetup }, connectTimeout.Token).ConfigureAwait(false);
                // Do not expose the socket or enable audio until setupComplete has arrived.
                while (true)
                {
                    var message = await ReceiveAsync(ws, connectTimeout.Token).ConfigureAwait(false);
                    if (message is null) throw Closed(ws.CloseStatus);
                    if (message["error"] is JsonObject error) throw ProviderError(error);
                    if (message["setupComplete"] is not null) break;
                    Handle(message);
                }
                await sendGate.WaitAsync(Lifetime.Token).ConfigureAwait(false);
                try { socket = ws; reconnecting = false; } finally { sendGate.Release(); }
                ready.TrySetResult();
                if (handle is not null) Emit(new() { ["type"] = "session.resumed" });
                var connectedAt = DateTimeOffset.UtcNow;
                var goAway = false;
                try
                {
                    while (!Lifetime.IsCancellationRequested)
                    {
                        var message = await ReceiveAsync(ws, Lifetime.Token).ConfigureAwait(false);
                        if (message is null)
                        {
                            if (ws.CloseStatus is WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.InvalidPayloadData or WebSocketCloseStatus.ProtocolError)
                                throw Closed(ws.CloseStatus);
                            break;
                        }
                        Handle(message);
                        if (message["goAway"] is not null) { goAway = true; break; }
                    }
                }
                catch (WebSocketException) when (handle is not null && attempts < 3) { }
                finally
                {
                    reconnecting = true;
                    await sendGate.WaitAsync().ConfigureAwait(false);
                    try { if (ReferenceEquals(socket, ws)) socket = null; } finally { sendGate.Release(); }
                    ws.Abort();
                }
                if (Lifetime.IsCancellationRequested) break;
                if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) attempts = 0;
                if (handle is null || ++attempts > 3) throw Closed(ws.CloseStatus);
                Emit(new() { ["type"] = "session.reconnecting" });
                audio?.Interrupt();
                if (!goAway) await Task.Delay(TimeSpan.FromMilliseconds(500 * attempts), Lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { ready.TrySetCanceled(Lifetime.Token); }
        catch (Exception cause)
        {
            // Socket exceptions may contain access_token in a URI. Never forward their text.
            var error = RealtimeFailureException.Safe(cause, "google-connect", "Google Live connection failed or could not be resumed.");
            ready.TrySetException(error); Fail(error);
        }
    }
    public static async Task<JsonObject?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult frame;
        do
        {
            frame = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (stream.Length + frame.Count > MaximumMessageBytes) throw new InvalidDataException("Google event exceeded its limit.");
            stream.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return Parse(stream.ToArray());
    }
    private void Handle(JsonObject message)
    {
        if (message["error"] is JsonObject error) throw ProviderError(error);
        if (message["sessionResumptionUpdate"] is JsonObject resume)
            handle = resume["resumable"]?.GetValue<bool>() == true ? resume["newHandle"]?.ToString() : null;
        if (message["toolCallCancellation"]?["ids"] is JsonArray cancellations)
            foreach (var id in cancellations) Emit(new() { ["type"] = "tool.cancelled", ["call_id"] = id?.DeepClone() });
        var content = message["serverContent"] as JsonObject;
        if (content?["interrupted"]?.GetValue<bool>() == true)
        {
            audio?.Interrupt(); Emit(new() { ["type"] = "response.cancelled", ["response_id"] = responseId });
        }
        var userDelta = content?["inputTranscription"]?["text"]?.ToString() ?? content?["interimInputTranscription"]?["text"]?.ToString();
        if (!string.IsNullOrEmpty(userDelta))
        {
            userText += userDelta;
            Emit(new() { ["type"] = "conversation.item.input_audio_transcription.delta", ["item_id"] = "google-input-" + responseId, ["delta"] = userDelta });
        }
        var outputDelta = content?["outputTranscription"]?["text"]?.ToString();
        if (content?["modelTurn"]?["parts"] is JsonArray parts)
            foreach (var part in parts)
            {
                if (part?["inlineData"] is JsonObject inline && inline["mimeType"]?.ToString().StartsWith("audio/pcm", StringComparison.Ordinal) == true)
                {
                    var mime = inline["mimeType"]!.ToString();
                    if (mime.Contains("rate=", StringComparison.Ordinal) && !mime.Contains("rate=24000", StringComparison.Ordinal))
                        throw new RealtimeFailureException("google-audio-format", "Google returned an unsupported PCM sample rate.");
                    audio?.Play(Convert.FromBase64String(inline["data"]!.ToString()));
                }
                if (outputDelta is null && part?["text"] is { } text) outputDelta = (outputDelta ?? "") + text.ToString();
            }
        if (!string.IsNullOrEmpty(outputDelta))
        {
            assistantText += outputDelta;
            Emit(new() { ["type"] = "response.output_audio_transcript.delta", ["response_id"] = responseId, ["delta"] = outputDelta });
        }
        if (message["toolCall"]?["functionCalls"] is JsonArray calls)
            foreach (var call in calls) Emit(new() { ["type"] = "response.output_item.done", ["response_id"] = responseId,
                ["item"] = new JsonObject { ["type"] = "function_call", ["call_id"] = call?["id"]?.DeepClone(), ["name"] = call?["name"]?.DeepClone(), ["arguments"] = call?["args"]?.ToJsonString() ?? "{}" } });
        if (message["usageMetadata"] is { } usage) Emit(new() { ["type"] = "response.usage", ["usage"] = usage.DeepClone(), ["response_id"] = responseId });
        // generationComplete precedes the end of playback/turn: do not split a turn there.
        if (content?["turnComplete"]?.GetValue<bool>() == true)
        {
            if (userText.Length > 0) Emit(new() { ["type"] = "conversation.item.input_audio_transcription.completed", ["item_id"] = "google-input-" + responseId, ["transcript"] = userText });
            Emit(new() { ["type"] = "response.done", ["response"] = new JsonObject { ["id"] = responseId, ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["transcript"] = assistantText }) }) } });
            userText = assistantText = ""; responseId = Guid.NewGuid().ToString("N");
        }
    }
    private async Task SendAudioAsync()
    {
        try
        {
            await foreach (var bytes in input.Reader.ReadAllAsync(Lifetime.Token).ConfigureAwait(false))
            {
                if (reconnecting) continue;
                try
                {
                    await sendGate.WaitAsync(Lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (reconnecting || socket is null) continue;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        await SendToAsync(socket, new() { ["realtimeInput"] = new JsonObject { ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = "audio/pcm;rate=16000" } } }, timeout.Token).ConfigureAwait(false);
                    }
                    finally { sendGate.Release(); }
                }
                catch (Exception error) when (!Lifetime.IsCancellationRequested && error is WebSocketException or OperationCanceledException)
                {
                    // The receive owner performs bounded resumption. Do not race it with a terminal Fail().
                    reconnecting = true; socket?.Abort();
                }
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch { if (!Lifetime.IsCancellationRequested) Fail(new RealtimeFailureException("google-upload", "Google audio upload failed.")); }
    }
    private static Task SendToAsync(ClientWebSocket ws, JsonObject value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Google input exceeded its limit.");
        return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
    public override async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket is null || reconnecting) throw new RealtimeFailureException("google-reconnecting", "Google Live is reconnecting. Wait for the connected status before sending.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SendToAsync(socket, message, timeout.Token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }
    public override async Task SetMutedAsync(bool muted)
    {
        if (audio is not null) audio.Muted = muted;
        if (muted && !reconnecting && socket is not null)
            await SendAsync(new() { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }, Lifetime.Token).ConfigureAwait(false);
    }
    public override Task SendVideoAsync(byte[] jpeg, CancellationToken ct) => SendAsync(new() { ["realtimeInput"] = new JsonObject { ["video"] = new JsonObject { ["data"] = Convert.ToBase64String(jpeg), ["mimeType"] = "image/jpeg" } } }, ct);
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Lifetime.Cancel(); audio?.Dispose(); input.Writer.TryComplete(); socket?.Abort();
        if (receiveTask is not null) await receiveTask.ConfigureAwait(false);
        if (sendTask is not null) await sendTask.ConfigureAwait(false);
        Complete();
    }
    private static RealtimeFailureException Closed(WebSocketCloseStatus? status) => new("google-close-" + ((int?)status ?? 0),
        status is null ? "Google Live disconnected without a close code." : $"Google Live closed the connection (code {(int)status}).");
    private static RealtimeFailureException ProviderError(JsonObject error)
    {
        var code = error["code"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
        return new("google-provider-" + code, $"Google Live reported a provider error (code {code}).");
    }
}
