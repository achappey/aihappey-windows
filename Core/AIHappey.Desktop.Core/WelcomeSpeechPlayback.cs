namespace AIHappey.Desktop.Core;

/// <summary>Generation-owned optional welcome audio. Refreshing the transcript never replays it.</summary>
public sealed class WelcomeSpeechPlayback : IDisposable
{
    private CancellationTokenSource? generation;
    private bool attempted;
    public CancellationToken Replace()
    {
        Cancel(); generation = new(); attempted = false; return generation.Token;
    }
    public void Cancel()
    {
        var previous = generation; generation = null;
        previous?.Cancel(); previous?.Dispose();
    }
    public async Task PlayAsync(CancellationToken identity, Func<CancellationToken, Task> play)
    {
        if (generation is null || identity != generation.Token || identity.IsCancellationRequested || attempted) return;
        attempted = true;
        try { await play(identity); }
        catch (Exception) { /* Welcome text remains available; this is never a main-conversation error. */ }
    }
    public static bool Supports(ChatTarget? target) => target is not null &&
        // Real-time session tags select the socket; HTTP speech models such as Gemini TTS have none.
        (target.ModelType == "speech" || OpenAiRealtimeSpeech.Supports(target));

    /// <summary>Prefer the output socket; only fall back before any audio has played.</summary>
    public static async Task SpeakAsync(string text, bool useSocket, bool allowHttp,
        Func<Action, CancellationToken, Task<IStreamingSpeechSession>> open,
        Func<CancellationToken, Task> http, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (useSocket)
        {
            var played = 0;
            IStreamingSpeechSession? connection = null;
            try
            {
                connection = await open(() => Interlocked.Exchange(ref played, 1), ct);
                ct.ThrowIfCancellationRequested();
                await connection.AppendAsync(text, ct);
                await connection.FinishAsync(ct);
                return;
            }
            catch
            {
                if (connection is not null) await connection.CancelAsync();
                if (ct.IsCancellationRequested || Volatile.Read(ref played) != 0 || !allowHttp) throw;
            }
            finally { if (connection is not null) await connection.DisposeAsync(); }
        }
        ct.ThrowIfCancellationRequested();
        if (allowHttp) await http(ct);
    }
    public void Dispose() => Cancel();
}
