using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

/// <summary>Atoms hosts its own pipeline. No client tools, keys, or OpenAI session configuration are sent.</summary>
public sealed class SmallestRealtimeSession(Func<CancellationToken, Task<string>> tokenFactory) : RealtimeSession
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RealtimeAudio? devices;
    private Task? receiving, sending;
    private volatile bool stopping;
    private int disposed;
    private byte? carry;
    public override async Task StartAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var token = await tokenFactory(timeout.Token).ConfigureAwait(false);
            await socket.ConnectAsync(SmallestRealtimeProtocol.Connection(token), timeout.Token).ConfigureAwait(false);
            var created = await GoogleRealtimeSession.ReceiveAsync(socket, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException();
            var rate = SmallestRealtimeProtocol.OutputRate(created);
            devices = new(SmallestRealtimeProtocol.InputRate, rate, bytes =>
            {
                if (!stopping && !input.Writer.TryWrite(bytes)) Fail(new RealtimeFailureException("smallest-backlog", "The connection cannot keep up with microphone audio."));
            }, Fail, Emit);
            Emit(created); receiving = ReceiveAsync(); sending = SendAudioAsync();
            devices.Start(); timeout.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new RealtimeFailureException("smallest-connect", "Could not start the Smallest AI agent. Check the model, token expiry, and provider permissions."); }
    }
    private async Task SendFrameAsync(JsonObject message, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { sendGate.Release(); }
    }
    public override Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (stopping) throw new InvalidOperationException("The Smallest AI session is closing.");
        return message["type"]?.ToString() switch
        {
            "response.create" => Task.CompletedTask, // Text input itself starts the provider response.
            "input_audio_buffer.commit" => SendFrameAsync(new() { ["type"] = "input_audio_buffer.commit" }, ct),
            _ => SendFrameAsync(SmallestRealtimeProtocol.TextInput(message), ct)
        };
    }
    private async Task SendAudioAsync()
    {
        try
        {
            await foreach (var bytes in input.Reader.ReadAllAsync(Lifetime.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await SendFrameAsync(new() { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(bytes) }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch { Fail(new RealtimeFailureException("smallest-upload", "Microphone audio could not be sent to the Smallest AI agent.")); }
    }
    private async Task ReceiveAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                var message = await GoogleRealtimeSession.ReceiveAsync(socket, Lifetime.Token).ConfigureAwait(false);
                if (message is null) throw new InvalidOperationException();
                var type = message["type"]?.ToString();
                if (type == "error") throw new RealtimeFailureException("smallest-provider", "The Smallest AI agent reported an error.");
                if (type == "output_audio.delta")
                {
                    if (stopping) continue;
                    var bytes = Convert.FromBase64String(message["audio"]!.GetValue<string>());
                    if (carry is { } first) { var joined = new byte[bytes.Length + 1]; joined[0] = first; bytes.CopyTo(joined, 1); bytes = joined; carry = null; }
                    if (bytes.Length % 2 != 0) { carry = bytes[^1]; bytes = bytes[..^1]; }
                    devices?.Play(bytes); continue;
                }
                if (type == "interruption") { carry = null; devices?.Interrupt(); }
                if (type == "session.closed")
                {
                    if (message["reason"]?.ToString() == "setup_failed") throw new InvalidOperationException();
                    Emit(message); closed.TrySetResult(); Complete(); return;
                }
                if (type is "transcript" or "transcript.delta" or "agent_start_talking" or "agent_stop_talking" or "interruption") Emit(message);
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch { Fail(new RealtimeFailureException("smallest-receive", "The Smallest AI agent connection failed.")); }
        finally { closed.TrySetResult(); }
    }
    public override Task SetMutedAsync(bool muted) { if (devices is not null) devices.Muted = muted; return Task.CompletedTask; }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping = true; devices?.Dispose(); input.Writer.TryComplete();
        try
        {
            if (!Lifetime.IsCancellationRequested && socket.State == WebSocketState.Open && !closed.Task.IsCompleted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                if (sending is not null) await sending.WaitAsync(timeout.Token).ConfigureAwait(false);
                await SendFrameAsync(new() { ["type"] = "session.close" }, timeout.Token).ConfigureAwait(false);
                await closed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch { /* Shutdown is bounded; confirmed transcript events have already been emitted. */ }
        Lifetime.Cancel(); socket.Abort();
        if (receiving is not null) await receiving.ConfigureAwait(false);
        if (sending is not null) await sending.ConfigureAwait(false);
        socket.Dispose(); sendGate.Dispose(); Complete();
    }
}
