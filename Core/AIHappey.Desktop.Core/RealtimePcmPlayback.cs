using NAudio.Wave;

namespace AIHappey.Desktop.Core;

/// <summary>A bounded render queue, sized for generated-audio bursts rather than device latency.
/// Reading is clocked by WASAPI. Enqueueing 10 seconds of generated audio is not a device error.</summary>
internal sealed class RealtimePcmPlayback(int sampleRate) : IWaveProvider
{
    private readonly object gate = new();
    private readonly Queue<byte[]> chunks = new();
    private int offset, queued;
    public WaveFormat WaveFormat { get; } = new(sampleRate, 16, 1);
    public int MaximumBytes => WaveFormat.AverageBytesPerSecond * 90;
    public int BufferedBytes { get { lock (gate) return queued; } }
    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new InvalidDataException("Invalid PCM frame length.");
        lock (gate)
        {
            if (pcm.Length > MaximumBytes - queued) throw new RealtimeFailureException("playback-backlog", "Realtime playback exceeded the 90-second safety limit.");
            if (pcm.Length == 0) return;
            chunks.Enqueue(pcm.ToArray()); queued += pcm.Length;
        }
    }
    public int Read(byte[] buffer, int bufferOffset, int count) => Read(buffer.AsSpan(bufferOffset, count));
    public int Read(Span<byte> output)
    {
        lock (gate)
        {
            var count = output.Length; var written = 0;
            while (written < count && chunks.TryPeek(out var head))
            {
                var length = Math.Min(count - written, head.Length - offset);
                head.AsSpan(offset, length).CopyTo(output[written..]);
                written += length; offset += length; queued -= length;
                if (offset == head.Length) { chunks.Dequeue(); offset = 0; }
            }
            output[written..].Clear(); return count;
        }
    }
    public void Clear() { lock (gate) { chunks.Clear(); offset = queued = 0; } }
}
