using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHappey.Desktop.Core;

/// <summary>The small hardware boundary also lets UI tests exercise dictation without opening a microphone.</summary>
public interface IDictationRecording : IAsyncDisposable
{
    void Start();
    Task<ComposerAttachment> StopAsync();
}

/// <summary>A single Windows capture endpoint, never loopback or a realtime/provider session.</summary>
internal sealed class DictationRecorder : IDictationRecording
{
    private readonly object gate = new();
    private readonly DictationAudio audio = new();
    private readonly MMDevice device;
    private readonly WasapiRecorder capture;
    private Task? release;
    private Exception? failure;
    private bool stopping, discarded;
    private int failureReported;
    private readonly Action<Exception> failed;

    public DictationRecorder(Action<Exception> failed)
    {
        this.failed = failed;
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        try
        {
            capture = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithCommunicationsMode()
                .WithFormat(new WaveFormat(DictationAudio.SampleRate, 16, 1)).WithBufferLength(20).Build();
            capture.DataAvailable += (bytes, flags, _, _) =>
            {
                try { audio.Append(bytes, (flags & AudioClientBufferFlags.Silent) != 0); }
                catch (Exception error) { Fail(error); }
            };
            capture.RecordingStopped += (_, args) =>
            {
                lock (gate)
                {
                    if (args.Exception is not null) failure = args.Exception;
                    if (stopping || discarded) return;
                }
                Fail(new InvalidOperationException(DesktopResources.Get("TranscriptionRecordingFailed")));
            };
        }
        catch { device.Dispose(); audio.Dispose(); throw; }
    }

    private void Fail(Exception error)
    {
        lock (gate) { if (discarded) return; failure ??= error; }
        if (Interlocked.Exchange(ref failureReported, 1) == 0) failed(error);
    }

    public void Start() => capture.StartRecording();

    // Dispose joins the WASAPI worker and drains its final packets. Never join while holding the audio lock.
    private Task ReleaseAsync()
    {
        lock (gate) return release ??= Task.Run(() => { try { capture.Dispose(); } finally { device.Dispose(); } });
    }

    public async Task<ComposerAttachment> StopAsync()
    {
        lock (gate)
        {
            if (stopping || discarded) throw new OperationCanceledException();
            stopping = true;
        }
        await ReleaseAsync();
        lock (gate)
        {
            if (discarded) throw new OperationCanceledException();
            if (failure is not null) throw new InvalidOperationException(DesktopResources.Get("TranscriptionRecordingFailed"), failure);
            return audio.Complete($"recording-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { discarded = true; audio.Dispose(); }
        await ReleaseAsync();
    }
}
