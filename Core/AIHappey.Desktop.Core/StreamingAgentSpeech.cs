using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AIHappey.Desktop.Core;

/// <summary>Turn-owned text observer. Neither the LLM nor the normal tool/approval pipeline waits for audio.</summary>
public sealed class StreamingAgentSpeech : IAsyncDisposable
{
    private sealed class Block
    {
        public readonly StringBuilder Text = new();
        public readonly Channel<string> Input = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        public bool Ended;
    }
    private readonly Dictionary<string, Block> blocks = new(StringComparer.Ordinal);
    private readonly Func<Action, CancellationToken, Task<IStreamingSpeechSession>> open;
    private readonly Func<string, CancellationToken, Task> legacy;
    private readonly Action<Exception> error;
    private readonly CancellationTokenSource lifetime;
    private Task tail = Task.CompletedTask;
    private int played, retained;
    private bool failed, reported;
    public StreamingAgentSpeech(Func<Action, CancellationToken, Task<IStreamingSpeechSession>> open,
        Func<string, CancellationToken, Task> legacy, Action<Exception> error, CancellationToken ct)
    { this.open = open; this.legacy = legacy; this.error = error; lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); }
    public void Observe(int response, StreamEvent item)
    {
        if (lifetime.IsCancellationRequested || item.Type is not ("text-start" or "text-delta" or "text-end")) return;
        var id = item.Raw.TryGetProperty("id", out var key) ? key.GetString() ?? "" : "";
        var identity = $"{response}:{id}";
        if (item.Type == "text-delta" && item.Raw.TryGetProperty("delta", out var delta)) Append(identity, delta.GetString() ?? "");
        if (item.Type == "text-end") End(identity);
    }
    public void CompleteResponse(int response)
    {
        foreach (var pair in blocks.Where(pair => pair.Key.StartsWith(response + ":", StringComparison.Ordinal))) End(pair.Key);
    }
    private void Append(string key, string text)
    {
        if (text.Length == 0 || lifetime.IsCancellationRequested) return;
        if (blocks.TryGetValue(key, out var block) && block.Ended) return;
        if (Volatile.Read(ref retained) + text.Length > 128_000 || blocks.Count >= 1024)
        {
            lifetime.Cancel(); if (!reported) { reported = true; error(new InvalidOperationException("Speech text backlog exceeded.")); } return;
        }
        Interlocked.Add(ref retained, text.Length);
        if (block is null)
        {
            block = new(); blocks[key] = block;
            var previous = tail;
            tail = PlayAsync(previous, block);
        }
        // Only the observer writes Text; the worker reads it after the channel has completed.
        block.Text.Append(text); block.Input.Writer.TryWrite(text);
    }
    private void End(string key)
    {
        if (blocks.TryGetValue(key, out var block)) { block.Ended = true; block.Input.Writer.TryComplete(); }
    }
    private async Task PlayAsync(Task previous, Block block)
    {
        var ct = lifetime.Token; IStreamingSpeechSession? session = null;
        try
        {
            await previous; ct.ThrowIfCancellationRequested();
            if (failed) throw new InvalidOperationException("Streaming speech is unavailable for this turn.");
            session = await open(() => Interlocked.Exchange(ref played, 1), ct);
            await foreach (var text in block.Input.Reader.ReadAllAsync(ct)) await session.AppendAsync(text, ct);
            await session.FinishAsync(ct);
        }
        catch (Exception failure)
        {
            if (session is not null) await session.CancelAsync(); failed = true;
            try
            {
                // Failed sockets may precede the rest of the LLM answer. Wait for authoritative text, never synthesize a preview twice.
                await foreach (var _ in block.Input.Reader.ReadAllAsync(ct)) { }
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref played) == 0) await legacy(block.Text.ToString(), ct);
                else if (!reported) { reported = true; error(new InvalidOperationException("Streaming speech failed after playback started.", failure)); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception legacyFailure) { if (!ct.IsCancellationRequested) error(legacyFailure); }
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            Interlocked.Add(ref retained, -block.Text.Length);
            // Keep the identity tombstone but not the response text/audio.
            if (block.Ended) block.Text.Clear();
        }
    }
    public Task DrainAsync() => tail;
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); foreach (var block in blocks.Values) block.Input.Writer.TryComplete();
        await tail; lifetime.Dispose(); blocks.Clear();
    }
}
