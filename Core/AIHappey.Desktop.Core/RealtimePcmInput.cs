using System.Buffers.Binary;

namespace AIHappey.Desktop.Core;

/// <summary>Exactly 20ms mono PCM16 frames, DC filtering and a conservative noise gate.
/// Constant frame cadence (including digital silence) preserves provider VAD and RTP clocks.
/// This is NOT a speech recognizer and cannot distinguish a radio voice from the user.</summary>
internal sealed class RealtimePcmInput
{
    private sealed class Frame(byte[] pcm, bool audible) { public byte[] Pcm = pcm; public bool Audible = audible; }
    private readonly byte[] pending;
    private readonly byte[] silence;
    private readonly Queue<Frame> preRoll = new();
    private int pendingLength, onset, hangover;
    private double previousInput, previousOutput;
    private double noise = .001;
    public int FrameBytes => pending.Length;
    public RealtimePcmInput(int sampleRate)
    {
        if (sampleRate is not (16000 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        pending = new byte[sampleRate / 50 * 2]; silence = new byte[pending.Length];
    }
    public void Reset()
    {
        pendingLength = onset = hangover = 0; previousInput = previousOutput = 0; noise = .001;
        preRoll.Clear(); Array.Clear(pending);
    }
    public void Push(ReadOnlySpan<byte> pcm, bool silent, Action<byte[]> emit)
    {
        // WASAPI packet boundaries need not coincide with Opus frame boundaries.
        while (pcm.Length > 0)
        {
            var count = Math.Min(pcm.Length, pending.Length - pendingLength);
            if (silent) pending.AsSpan(pendingLength, count).Clear(); else pcm[..count].CopyTo(pending.AsSpan(pendingLength));
            pcm = pcm[count..]; pendingLength += count;
            if (pendingLength != pending.Length) continue;
            Process(emit); pendingLength = 0;
        }
    }
    private void Process(Action<byte[]> emit)
    {
        var filtered = new byte[pending.Length]; double energy = 0;
        for (var i = 0; i < pending.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pending.AsSpan(i)) / 32768d;
            var highPass = sample - previousInput + .995 * previousOutput;
            previousInput = sample; previousOutput = highPass;
            energy += highPass * highPass;
            BinaryPrimitives.WriteInt16LittleEndian(filtered.AsSpan(i), (short)Math.Clamp(highPass * 32768, short.MinValue, short.MaxValue));
        }
        var rms = Math.Sqrt(energy / (pending.Length / 2));
        var threshold = Math.Max(.006, Math.Min(.02, noise * 3));
        var candidate = rms > threshold;
        if (!candidate) noise = noise * .98 + Math.Min(rms, .006) * .02;
        onset = candidate ? onset + 1 : 0;
        if (onset >= 3)
        {
            hangover = 25;
            foreach (var frame in preRoll) frame.Audible = true;
        }
        else if (hangover > 0) hangover--;
        preRoll.Enqueue(new(filtered, hangover > 0));
        // Fixed 120ms pre-roll delay: do not burst or change the audio sample clock when the gate opens.
        if (preRoll.Count > 6)
        {
            var next = preRoll.Dequeue(); emit(next.Audible ? next.Pcm : silence);
        }
    }
}
