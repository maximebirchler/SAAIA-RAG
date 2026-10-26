using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

internal static class VisualAssetTestPng
{
    internal static readonly byte[] Bytes = Build();
    internal static DoclingImageReference Image() => new()
    {
        MediaType = "image/png", Uri = "data:image/png;base64," + Convert.ToBase64String(Bytes),
        Dpi = 72, Size = new() { Width = 2, Height = 2 }
    };

    private static byte[] Build()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.SmallestSize, leaveOpen: true))
            compressed.Write(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255 });
        Chunk(output, "IDAT", pixels.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(type);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, data.Length); stream.Write(value);
        stream.Write(name); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var octet in name.Concat(data))
        {
            crc ^= octet;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(value, crc ^ 0xffffffff); stream.Write(value);
    }
}
