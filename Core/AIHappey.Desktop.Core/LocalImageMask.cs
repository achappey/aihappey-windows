using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Portable RGBA PNG encoder: opaque black, transparent rectangular edit regions. No UI-thread canvas needed.</summary>
public static class LocalImageMask
{
    public static byte[] Create(JsonElement input, CancellationToken ct)
    {
        int Dimension(string field)
        {
            if (!input.TryGetProperty(field, out var value) || !value.TryGetDouble(out var n) || !double.IsFinite(n) || n < 1 || n > 8192)
                throw new LocalToolException(field + " must be between 1 and 8192 pixels.");
            return (int)n;
        }
        var width = Dimension("width"); var height = Dimension("height");
        if ((long)width * height > 16 * 1024 * 1024) throw new LocalToolException("Mask exceeds the 16 megapixel limit.");
        if (!input.TryGetProperty("boxes", out var boxes) || boxes.ValueKind != JsonValueKind.Array || boxes.GetArrayLength() > 10000)
            throw new LocalToolException("boxes must be an array of at most 10000 rectangles.");
        var rectangles = new List<(int X, int Y, int Right, int Bottom)>();
        foreach (var box in boxes.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            double Number(string field) => box.ValueKind == JsonValueKind.Object && box.TryGetProperty(field, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Floor(n + .5) : double.NaN;
            var x = Number("x"); var y = Number("y"); var w = Number("w"); var h = Number("h");
            if (!double.IsFinite(x + y + w + h)) continue;
            rectangles.Add(((int)Math.Clamp(Math.Min(x, x + w), 0, width), (int)Math.Clamp(Math.Min(y, y + h), 0, height),
                (int)Math.Clamp(Math.Max(x, x + w), 0, width), (int)Math.Clamp(Math.Max(y, y + h), 0, height)));
        }
        using var compressed = new MemoryStream();
        rectangles.Sort((a, b) => a.X.CompareTo(b.X));
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[checked(1 + width * 4)];
            for (var y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested(); Array.Clear(row);
                for (var x = 0; x < width; x++) row[1 + x * 4 + 3] = 255;
                var clearedThrough = 0;
                foreach (var r in rectangles) if (y >= r.Y && y < r.Bottom)
                {
                    // Sorted union: overlapping boxes never clear the same pixel thousands of times.
                    for (var x = Math.Max(clearedThrough, r.X); x < r.Right; x++) row[1 + x * 4 + 3] = 0;
                    clearedThrough = Math.Max(clearedThrough, r.Right);
                }
                zip.Write(row);
            }
        }
        using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []); return png.ToArray();
    }
    private static void Chunk(Stream output, string type, byte[] bytes)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number);
        var tag = Encoding.ASCII.GetBytes(type); output.Write(tag); output.Write(bytes); uint crc = uint.MaxValue;
        void Add(byte value) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        foreach (var b in tag) Add(b); foreach (var b in bytes) Add(b);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
}
