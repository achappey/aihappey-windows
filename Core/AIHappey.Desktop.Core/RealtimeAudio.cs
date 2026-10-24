using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Text.Json.Nodes;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Effects;

namespace AIHappey.Desktop.Core;

/// <summary>Session-owned PCM devices. Buffers are bounded; interruption discards queued playback.</summary>
internal sealed class RealtimeAudio : IDisposable
{
    private readonly object inputGate = new();
    private readonly WasapiRecorder capture;
    private readonly WasapiPlayer playback;
    private readonly RealtimePcmPlayback buffer;
    private readonly RealtimePcmInput frames;
    private readonly MMDevice captureDevice, renderDevice;
    private volatile bool disposed, muted;
    private int processingReported;
    public bool Muted
    {
        get => muted;
        set { lock (inputGate) { muted = value; frames.Reset(); } }
    }
    public RealtimeAudio(int inputRate, int outputRate, Action<byte[]> input, Action<Exception> failed, Action<JsonObject>? processing = null)
    {
        // Capture through the communications pipeline, but play through the user's NORMAL output.
        // A default communications render endpoint may be a different, unused headset/HDMI device.
        // The AEC reference must follow this actual playback endpoint, not the communications default.
        // Never record a loopback/render endpoint. Do not disable the Windows APO effects with raw mode.
        using var enumerator = new MMDeviceEnumerator();
        captureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        frames = new RealtimePcmInput(inputRate);
        buffer = new RealtimePcmPlayback(outputRate);
        capture = new WasapiRecorderBuilder().WithDevice(captureDevice).WithSharedMode().WithCommunicationsMode()
            .WithFormat(new WaveFormat(inputRate, 16, 1)).WithBufferLength(20).Build();
        playback = new WasapiPlayerBuilder().WithDevice(renderDevice).WithSharedMode().WithCategory(AudioStreamCategory.Speech).WithLatency(60).Build();
        capture.DataAvailable += (bytes, flags, _, _) =>
        {
            if (disposed) return;
            try
            {
                if (Interlocked.Exchange(ref processingReported, 1) == 0)
                {
                    // The audio client is initialized by the time its first packet is delivered.
                    var reference = false; var aec = false; var suppression = false;
                    try { if (capture.AcousticEchoCancellationControl is { } control) { control.SetReferenceEndpoint(renderDevice); reference = true; } } catch { /* Report lack of reference control; do not silently claim AEC. */ }
                    try
                    {
                        var effects = AudioEffectsManager.CreateAudioCaptureEffectsManager(captureDevice.ID, MediaCategory.Communications, AudioProcessing.Default).GetAudioCaptureEffects();
                        aec = effects.Any(e => e.AudioEffectType == AudioEffectType.AcousticEchoCancellation);
                        suppression = effects.Any(e => e.AudioEffectType == AudioEffectType.NoiseSuppression);
                    }
                    catch { /* Some devices don't expose the effect inventory. */ }
                    processing?.Invoke(new() { ["type"] = "audio.processing", ["echoCancellationAvailable"] = aec,
                        ["noiseSuppressionAvailable"] = suppression, ["echoReferenceConfigured"] = reference,
                        ["inputDevice"] = captureDevice.FriendlyName, ["outputDevice"] = renderDevice.FriendlyName });
                }
                lock (inputGate)
                {
                    if (!disposed && !muted)
                        frames.Push(bytes, (flags & AudioClientBufferFlags.Silent) != 0, input);
                }
            }
            catch (Exception error) { failed(RealtimeFailureException.Safe(error, "capture-processing", "Windows microphone processing failed.")); }
        };
        capture.RecordingStopped += (_, args) => { if (!disposed && args.Exception is not null) failed(new RealtimeFailureException("capture-device", "Windows microphone capture failed. Check the communications input device.")); };
        playback.PlaybackStopped += (_, args) => { if (!disposed && args.Exception is not null) failed(new RealtimeFailureException("render-device", "Windows audio playback failed. Check the default output device.")); };
    }
    public void Start() { playback.Init(buffer); playback.Play(); capture.StartRecording(); }
    public void Play(byte[] pcm)
    {
        if (!disposed) buffer.Add(pcm);
    }
    public void Interrupt() { if (disposed) return; playback.Stop(); buffer.Clear(); if (!disposed) playback.Play(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // Never hold the capture or encoder lock while joining the WASAPI worker threads.
        try { capture.Dispose(); }
        finally { playback.Dispose(); buffer.Clear(); captureDevice.Dispose(); renderDevice.Dispose(); }
    }
}
