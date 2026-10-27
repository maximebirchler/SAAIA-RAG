using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

internal sealed record ValidatedPngAsset(byte[] Bytes, int Width, int Height);

/// <summary>PNG-only, bounded embedded transport. Never resolves URLs or paths.</summary>
internal static class CanonicalPngAsset
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const long MaximumPixels = 16_000_000;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    // Counts decoded transport bytes without allocating or decoding a PNG.
    // Pixel/header/CRC validation remains mandatory in ReadEmbedded/Validate.
    internal static long MeasureEmbeddedByteLength(DoclingImageReference image)
    {
        const string prefix = "data:image/png;base64,";
        if (image.MediaType != "image/png" || string.IsNullOrEmpty(image.Uri) || image.Size is null
            || !image.Uri.StartsWith(prefix, StringComparison.Ordinal)
            || image.Uri.Length - prefix.Length > ((MaximumBytes + 2) / 3) * 4)
            throw new InvalidDataException("canonical_visual_asset_embedded_png_required");
        var symbols = 0;
        var padding = 0;
        foreach (var character in image.Uri.AsSpan(prefix.Length))
        {
            if (character is ' ' or '\t' or '\r' or '\n') continue;
            symbols++;
            if (character == '=') padding++;
            else if (padding != 0 || !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                     or >= '0' and <= '9' or '+' or '/'))
                throw new InvalidDataException("canonical_visual_asset_base64_invalid");
        }
        if (symbols % 4 != 0 || padding > 2 || (padding > 0 && symbols < 4))
            throw new InvalidDataException("canonical_visual_asset_base64_invalid");
        var bytes = (long)symbols / 4 * 3 - padding;
        if (bytes > MaximumBytes) throw new InvalidDataException("canonical_visual_asset_png_invalid");
        return bytes;
    }

    internal static ValidatedPngAsset ReadEmbedded(DoclingImageReference image)
    {
        const string prefix = "data:image/png;base64,";
        if (image.MediaType != "image/png" || string.IsNullOrEmpty(image.Uri) || image.Size is null || !image.Uri.StartsWith(prefix, StringComparison.Ordinal)
            || image.Uri.Length - prefix.Length > ((MaximumBytes + 2) / 3) * 4)
            throw new InvalidDataException("canonical_visual_asset_embedded_png_required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Uri[prefix.Length..]); }
        catch (FormatException) { throw new InvalidDataException("canonical_visual_asset_base64_invalid"); }
        var asset = Validate(bytes);
        if (asset.Width != image.Size.Width || asset.Height != image.Size.Height)
            throw new InvalidDataException("canonical_visual_asset_dimensions_mismatch");
        return asset;
    }

    internal static ValidatedPngAsset Validate(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes || bytes.Length < 57 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("canonical_visual_asset_png_invalid");
        var position = 8;
        var width = 0;
        var height = 0;
        var channels = 0;
        var sawHeader = false;
        var sawEnd = false;
        var sawPixels = false;
        var pixelsClosed = false;
        using var compressed = new MemoryStream();
        while (position <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (length > MaximumBytes || (long)position + 12 + length > bytes.Length)
                throw new InvalidDataException("canonical_visual_asset_png_chunk_invalid");
            var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            if (!type.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                throw new InvalidDataException("canonical_visual_asset_png_chunk_invalid");
            var data = bytes.AsSpan(position + 8, (int)length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + (int)length, 4));
            if (Crc(bytes.AsSpan(position + 4, 4 + (int)length)) != expectedCrc)
                throw new InvalidDataException("canonical_visual_asset_png_crc_invalid");
            if (!sawHeader && type != "IHDR") throw new InvalidDataException("canonical_visual_asset_png_header_missing");
            switch (type)
            {
                case "IHDR":
                    if (sawHeader || length != 13) throw new InvalidDataException("canonical_visual_asset_png_header_invalid");
                    sawHeader = true;
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    channels = data[9] switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
                    if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > MaximumPixels
                        || data[8] != 8 || channels == 0 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        throw new InvalidDataException("canonical_visual_asset_png_format_or_dimensions_unsupported");
                    break;
                case "IDAT":
                    if (pixelsClosed) throw new InvalidDataException("canonical_visual_asset_png_chunk_order_invalid");
                    sawPixels = true; compressed.Write(data); break;
                case "IEND":
                    if (length != 0) throw new InvalidDataException("canonical_visual_asset_png_end_invalid");
                    sawEnd = true;
                    break;
                default:
                    // This deliberately bounded transport does not accept APNG,
                    // unknown critical chunks or compressed ancillary payloads.
                    if (type is not ("PLTE" or "tRNS" or "pHYs" or "sRGB" or "gAMA" or "cHRM" or "tEXt" or "tIME"))
                        throw new InvalidDataException("canonical_visual_asset_png_chunk_unsupported");
                    if (sawPixels) pixelsClosed = true;
                    break;
            }
            position += (int)length + 12;
            if (sawEnd) break;
        }
        if (!sawHeader || !sawEnd || position != bytes.Length || compressed.Length == 0)
            throw new InvalidDataException("canonical_visual_asset_png_incomplete");
        compressed.Position = 0;
        using var pixels = new ZLibStream(compressed, CompressionMode.Decompress);
        var scanline = new byte[checked(width * channels + 1)];
        try
        {
            for (var row = 0; row < height; row++)
            {
                pixels.ReadExactly(scanline);
                if (scanline[0] > 4) throw new InvalidDataException("canonical_visual_asset_png_filter_invalid");
            }
            if (pixels.ReadByte() != -1) throw new InvalidDataException("canonical_visual_asset_png_pixel_size_invalid");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("canonical_visual_asset_png_pixel_size_invalid"); }
        return new(bytes, width, height);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var octet in bytes)
        {
            crc ^= octet;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return crc ^ 0xffffffffu;
    }
}
