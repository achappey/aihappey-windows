using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHappey.Desktop.Core;

/// <summary>A subscription stops only its consumer. A borrowed source remains owned by its caller.</summary>
public interface ITranscriptionAudioInput : IDisposable
{
    int SampleRate { get; }
    IDisposable Subscribe(Action<byte[]> audio, Action<Exception> failed);
}

/// <summary>Input-only Windows communications capture. No render endpoint, loopback or audio files.</summary>
public sealed class TranscriptionAudioInput(int sampleRate) : ITranscriptionAudioInput
{
    public int SampleRate { get; } = sampleRate;
    private readonly object gate = new();
    private WasapiRecorder? recorder;
    private MMDevice? device;
    private bool disposed;
    public IDisposable Subscribe(Action<byte[]> audio, Action<Exception> failed)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recorder is not null) throw new InvalidOperationException("This microphone already has a consumer.");
            try
            {
                using var enumerator = new MMDeviceEnumerator(); device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithCommunicationsMode()
                    .WithFormat(new WaveFormat(SampleRate, 16, 1)).WithBufferLength(20).Build();
                var frames = new RealtimePcmInput(SampleRate);
                recorder.DataAvailable += (bytes, flags, _, _) =>
                {
                    try { frames.Push(bytes, (flags & AudioClientBufferFlags.Silent) != 0, audio); }
                    catch { failed(new RealtimeFailureException("transcription-capture", "Windows microphone capture failed.")); }
                };
                recorder.RecordingStopped += (_, args) => { if (!disposed && args.Exception is not null) failed(new RealtimeFailureException("transcription-device", "The microphone stopped. Check Windows microphone permissions and your input device.")); };
                recorder.StartRecording(); return new Subscription(this);
            }
            catch { Stop(); throw new RealtimeFailureException("transcription-microphone", "Could not open the microphone. Check Windows microphone permissions and your input device."); }
        }
    }
    private void Stop()
    {
        WasapiRecorder? capture; MMDevice? endpoint;
        lock (gate) { capture = recorder; endpoint = device; recorder = null; device = null; }
        try { capture?.Dispose(); } finally { endpoint?.Dispose(); }
    }
    public void Dispose() { disposed = true; Stop(); }
    private sealed class Subscription(TranscriptionAudioInput owner) : IDisposable
    {
        private TranscriptionAudioInput? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Stop();
    }
}
