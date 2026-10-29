using System.Text.Json.Nodes;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly WelcomeSpeechPlayback welcomeSpeech = new();
    private bool IsRealtimeStart => !closing && activePage == DesktopPage.Realtime
        && current.Messages.Count == 0 && realtime is null && !realtimeSending;

    private async Task PlayWelcomeSpeechAsync(JsonObject speech, IReadOnlyList<ChatTarget> models,
        string text, Func<bool> currentGeneration, CancellationToken ct)
    {
        var id = DesktopAgent.Text(speech["model"]?["id"]);
        var model = models.FirstOrDefault(target => target.Id == id);
        if (!IsRealtimeStart || !currentGeneration() || !WelcomeSpeechPlayback.Supports(model) || string.IsNullOrWhiteSpace(text)) return;
        await WelcomeSpeechPlayback.SpeakAsync(text,
            StreamingSpeechProtocol.Supports(model) || OpenAiRealtimeSpeech.Supports(model), !OpenAiRealtimeSpeech.IsModel(id),
            (started, token) => OpenAgentStreamingSpeechAsync(speech, started, token),
            async token =>
            {
                var result = await new DesktopSpeechClient(client, http).GenerateAsync(AgentSpeechConfiguration.Request(speech, text), token,
                    AgentAudioHeaders(speech["model"]!.AsObject()));
                token.ThrowIfCancellationRequested();
                if (!IsRealtimeStart || !currentGeneration()) return;
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(result.Audio); await writer.StoreAsync(); }
                token.ThrowIfCancellationRequested();
                if (!IsRealtimeStart || !currentGeneration()) return;
                stream.Seek(0);
                using var source = MediaSource.CreateFromStream(stream, result.MediaType);
                using var player = new MediaPlayer { AutoPlay = false };
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaEnded += (_, _) => finished.TrySetResult();
                player.MediaFailed += (_, _) => finished.TrySetException(new InvalidOperationException("Welcome audio playback failed."));
                try { player.Source = source; player.Play(); await finished.Task.WaitAsync(token); }
                finally { player.Pause(); player.Source = null; }
            }, ct);
    }
}
