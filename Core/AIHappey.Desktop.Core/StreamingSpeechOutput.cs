using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHappey.Desktop.Core;

/// <summary>Output only. Shares the bounded PCM implementation, never opens another microphone.</summary>
internal sealed class StreamingSpeechOutput : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiPlayer player;
    private readonly RealtimePcmPlayback buffer;
    private readonly object gate = new();
    private readonly Action started;
    private byte? carry;
    private Exception? failure;
    private bool disposed;
    public StreamingSpeechOutput(int sampleRate, Action started)
    {
        this.started = started;
        using var devices = new MMDeviceEnumerator(); device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        buffer = new(sampleRate);
        player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithCategory(AudioStreamCategory.Speech).WithLatency(60).Build();
        player.PlaybackStopped += (_, args) => { if (!disposed && args.Exception is not null) failure = new InvalidOperationException("Speech audio output failed."); };
        try { player.Init(new TrackedPlayback(this)); player.Play(); }
        catch { player.Dispose(); device.Dispose(); throw; }
    }
    private sealed class TrackedPlayback(StreamingSpeechOutput owner) : IWaveProvider
    {
        public WaveFormat WaveFormat => owner.buffer.WaveFormat;
        public int Read(byte[] bytes, int offset, int count) => Read(bytes.AsSpan(offset, count));
        public int Read(Span<byte> bytes)
        {
            lock (owner.gate)
            {
                if (!owner.disposed && owner.buffer.BufferedBytes > 0) owner.started();
                return owner.buffer.Read(bytes);
            }
        }
    }
    public void Push(byte[] bytes)
    {
        lock (gate)
        {
            if (disposed) return;
            if (failure is not null) throw failure;
            if (carry is { } first) { var joined = new byte[bytes.Length + 1]; joined[0] = first; bytes.CopyTo(joined, 1); bytes = joined; carry = null; }
            if (bytes.Length % 2 != 0) { carry = bytes[^1]; bytes = bytes[..^1]; }
            buffer.Add(bytes);
        }
    }
    public async Task DrainAsync(CancellationToken ct)
    {
        if (carry is not null) throw new InvalidOperationException("Speech returned an incomplete PCM sample.");
        while (buffer.BufferedBytes > 0) { ct.ThrowIfCancellationRequested(); if (failure is not null) throw failure; await Task.Delay(20, ct); }
        await Task.Delay(120, ct); if (failure is not null) throw failure;
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; buffer.Clear(); carry = null; }
        // Dispose joins WASAPI workers; never hold the render lock here.
        player.Dispose(); device.Dispose();
    }
}

internal sealed class PlayedStreamingSpeechSession(IStreamingSpeechSession session, StreamingSpeechOutput output, CancellationToken ct) : IStreamingSpeechSession
{
    private readonly CancellationTokenRegistration registration = ct.Register(output.Dispose);
    private bool disposed;
    public Task AppendAsync(string text, CancellationToken token) => session.AppendAsync(text, token);
    public Task FlushAsync(CancellationToken token) => session.FlushAsync(token);
    public async Task FinishAsync(CancellationToken token) { await session.FinishAsync(token); await output.DrainAsync(token); }
    public async Task CancelAsync() { output.Dispose(); await session.CancelAsync(); }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; registration.Dispose(); output.Dispose(); await session.DisposeAsync(); }
}
