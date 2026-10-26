using System.Buffers.Binary;

namespace AIHappey.Desktop.Core;

/// <summary>Bounded, uncompressed 16 kHz mono PCM16 recording. No disk files or provider state.</summary>
internal sealed class DictationAudio : IDisposable
{
    internal const int SampleRate = 16000, HeaderBytes = 44;
    private readonly object gate = new();
    private readonly MemoryStream audio = new();
    private bool finished;

    public DictationAudio() { audio.SetLength(HeaderBytes); audio.Position = HeaderBytes; }

    public void Append(ReadOnlySpan<byte> bytes, bool silent)
    {
        lock (gate)
        {
            if (finished) return;
            if (bytes.Length % 2 != 0) throw new InvalidOperationException(DesktopResources.Get("TranscriptionRecordingFailed"));
            if (audio.Length + bytes.Length > ComposerAttachments.MaximumFileBytes)
                throw new InvalidOperationException(DesktopResources.Get("TranscriptionRecordingLimit"));
            if (silent)
            {
                var position = (int)audio.Position; audio.SetLength(position + bytes.Length);
                audio.GetBuffer().AsSpan(position, bytes.Length).Clear(); audio.Position = audio.Length;
            }
            else audio.Write(bytes);
        }
    }

    public ComposerAttachment Complete(string filename)
    {
        lock (gate)
        {
            if (finished) throw new OperationCanceledException();
            try
            {
                if (audio.Length == HeaderBytes) throw new InvalidOperationException(DesktopResources.Get("TranscriptionRecordingEmpty"));
                var header = audio.GetBuffer().AsSpan(0, HeaderBytes);
                "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header[4..], (int)audio.Length - 8);
                "WAVEfmt "u8.CopyTo(header[8..]); BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
                BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate); BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * 2);
                BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2); BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
                "data"u8.CopyTo(header[36..]); BinaryPrimitives.WriteInt32LittleEndian(header[40..], (int)audio.Length - HeaderBytes);
                return ComposerAttachment.Local(filename, "audio/wav", audio.GetBuffer().AsMemory(0, (int)audio.Length));
            }
            finally { Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (finished) return;
            finished = true; audio.GetBuffer().AsSpan(0, (int)audio.Length).Clear(); audio.Dispose();
        }
    }
}
