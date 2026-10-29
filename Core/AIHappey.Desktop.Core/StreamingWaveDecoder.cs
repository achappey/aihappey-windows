using System.Buffers.Binary;
using System.Text;

namespace AIHappey.Desktop.Core;

/// <summary>Reads one generation's fragmented WAV header without buffering the audio file.</summary>
public sealed class StreamingWaveDecoder(Action<byte[], int> audio)
{
    private byte[] header = [];
    private bool riff, format, data;
    private long? remaining;
    private long received;
    private int sampleRate;
    public void Push(byte[] bytes)
    {
        if (data) { PushAudio(bytes); return; }
        var joined = new byte[header.Length + bytes.Length]; header.CopyTo(joined, 0); bytes.CopyTo(joined, header.Length); header = joined;
        if (!riff)
        {
            if (header.Length < 12) return;
            if (Name(0) != "RIFF" || Name(8) != "WAVE") throw new InvalidOperationException("Unsupported streaming WAV container.");
            header = header[12..]; riff = true;
        }
        while (header.Length >= 8)
        {
            var kind = Name(0); var length = U32(4);
            if (kind == "data")
            {
                if (!format) throw new InvalidOperationException("Streaming WAV has no PCM format.");
                remaining = length is 0 or uint.MaxValue ? null : length;
                data = true; var pcm = header[8..]; header = []; PushAudio(pcm); return;
            }
            if (length > 65536) throw new InvalidOperationException("Streaming WAV header exceeded its limit.");
            var size = checked(8 + (int)length + (int)(length % 2));
            if (header.Length < size) break;
            if (kind == "fmt ")
            {
                if (format || length < 16 || U16(8) != 1 || U16(10) != 1 || U16(22) != 16 || U16(20) != 2
                    || U32(12) is < 8000 or > 96000 || U32(16) != U32(12) * 2)
                    throw new InvalidOperationException("Streaming WAV requires mono signed 16-bit PCM.");
                sampleRate = (int)U32(12); format = true;
            }
            header = header[size..];
        }
        if (header.Length > 65536) throw new InvalidOperationException("Streaming WAV header exceeded its limit.");
    }
    private string Name(int offset) => Encoding.ASCII.GetString(header, offset, 4);
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset, 4));
    private void PushAudio(byte[] bytes)
    {
        if (remaining is { } count)
        {
            if (bytes.Length > count) throw new InvalidOperationException("Streaming WAV exceeded its declared audio length.");
            remaining -= bytes.Length;
        }
        received += bytes.Length;
        if (bytes.Length > 0) audio(bytes, sampleRate);
    }
    public void Finish()
    {
        if (!data || received % 2 != 0 || remaining is not null and not 0)
            throw new InvalidOperationException("Streaming WAV ended with incomplete audio.");
    }
}
