using AIHappey.Desktop.Core;

internal static class WelcomeSpeechRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var owner = new WelcomeSpeechPlayback();
        var calls = 0;
        Task Play(CancellationToken _) { calls++; return Task.CompletedTask; }
        var first = owner.Replace();
        await owner.PlayAsync(first, Play); await owner.PlayAsync(first, Play);
        check(calls == 1, "welcome speech: transcript/rerender refresh never repeats a generation");
        var nextLanguage = owner.Replace(); await owner.PlayAsync(nextLanguage, Play);
        check(first.IsCancellationRequested && calls == 2, "welcome speech: new language generation can speak even if its text is identical");
        await owner.PlayAsync(first, Play);
        check(calls == 2, "welcome speech: late completion of a replaced generation is ignored");
        var failing = owner.Replace();
        await owner.PlayAsync(failing, _ => { calls++; throw new IOException("optional playback failure"); });
        await owner.PlayAsync(failing, Play);
        check(calls == 3, "welcome speech: optional failures preserve text and do not retry a generation");
        var active = owner.Replace(); var cancelled = false;
        var running = owner.PlayAsync(active, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; }
        });
        owner.Cancel(); await running; await owner.PlayAsync(active, Play);
        check(cancelled && calls == 3, "welcome speech: conversation start/page exit cancels audio and rejects stale dispatch");

        check(!WelcomeSpeechPlayback.Supports(null)
            && !WelcomeSpeechPlayback.Supports(new("other/llm", "LLM") { ModelType = "language", Tags = ["real-time"] })
            && WelcomeSpeechPlayback.Supports(new("deepgram/aura-2-asteria-en", "Speech") { ModelType = "speech", Tags = ["real-time", "realtime-token"] })
            && WelcomeSpeechPlayback.Supports(new("google/gemini-3.8-flash-tts", "Gemini 3.8 Flash TTS") { ModelType = "speech" }),
            "welcome speech: current catalog speech models are eligible, including untagged Google Gemini HTTP streaming models");
        var openai = new ChatTarget("openai/gpt-realtime-2.1-mini", "Audio") { ModelType = "audio", Tags = ["real-time", "realtime-token", "realtime-speech"] };
        check(WelcomeSpeechPlayback.Supports(openai) && !WelcomeSpeechPlayback.Supports(openai with { Tags = ["real-time"] }),
            "welcome speech: OpenAI agent-only audio requires the explicit current speech capabilities");
        foreach (var played in new[] { false, true })
        {
            var fallback = 0; var failed = false;
            try
            {
                await WelcomeSpeechPlayback.SpeakAsync("Welcome", true, true,
                    (started, _) => Task.FromResult<IStreamingSpeechSession>(new Socket(text =>
                    { check(text == "Welcome", "welcome speech: completed text is sent verbatim"); if (played) started(); throw new IOException("socket lost"); })),
                    _ => { fallback++; return Task.CompletedTask; }, default);
            }
            catch (IOException) { failed = true; }
            check(played ? fallback == 0 && failed : fallback == 1 && !failed,
                "welcome speech: fallback is allowed only before audio playback begins");
        }
        var httpCalls = 0;
        try
        {
            await WelcomeSpeechPlayback.SpeakAsync("Welcome", true, false, (_, _) => throw new IOException("Realtime-only failure"),
                _ => { httpCalls++; return Task.CompletedTask; }, default);
        }
        catch (IOException) { }
        check(httpCalls == 0, "welcome speech: Realtime-only models never fall back to unsupported HTTP synthesis");
        using var abort = new CancellationTokenSource();
        try
        {
            await WelcomeSpeechPlayback.SpeakAsync("Welcome", true, true, (_, _) =>
            { abort.Cancel(); throw new OperationCanceledException(abort.Token); }, _ => { httpCalls++; return Task.CompletedTask; }, abort.Token);
        }
        catch (OperationCanceledException) { }
        check(httpCalls == 0, "welcome speech: cancelled setup never falls back");
        await WelcomeSpeechPlayback.SpeakAsync("Welcome", false, true, (_, _) => throw new Exception("Unexpected socket"),
            _ => { httpCalls++; return Task.CompletedTask; }, default);
        check(httpCalls == 1, "welcome speech: catalog-supported HTTP-only speech uses native fallback without opening a socket");
    }
    private sealed class Socket(Action<string> append) : IStreamingSpeechSession
    {
        public Task AppendAsync(string text, CancellationToken ct) { append(text); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public Task FinishAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
