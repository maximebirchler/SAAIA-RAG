using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Backend;

internal sealed record NativeRawPageDescriptor(int PhysicalPage, string Availability, string? Reason,
    int? BlockCount, string Sha256, int Utf8Bytes, int CompressedBytes);
internal sealed record NativeRawPageCatalog(string SchemaVersion, NativeRawPageRootBinding Root,
    NativeRawPageGenerationBinding Generation, string Mode, string? Reason, int PageCount,
    IReadOnlyList<NativeRawPageDescriptor> Pages);
internal sealed record NativeRawPagePrepared(NativeRawPageDescriptor Descriptor, byte[] Payload);
internal sealed record NativeRawPagePreparation(NativeRawPageCatalog Catalog, byte[] CatalogBytes,
    byte[] CatalogHash, IReadOnlyList<NativeRawPagePrepared> Pages);

/// <summary>Private, bounded codec; it never passes a partial document to the canonical validator.</summary>
internal static class NativeRawPageCodec
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] Serialize<T>(T value, int maximumBytes, NativeRawPageWork? work = null)
    {
        using var stream = new BoundedStream(maximumBytes, work);
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, Json);
        work?.Check();
        return stream.ToArray();
    }

    internal static T Deserialize<T>(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length < 2 || bytes.Length > maximumBytes) throw Invalid();
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var parsed = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            UniqueKeys(parsed.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        { throw Invalid(); }
    }

    internal static NativeRawPageSnapshot DeserializePage(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            UniqueKeys(parsed.RootElement);
            return JsonSerializer.Deserialize<NativeRawPageSnapshot>(json, Json) ?? throw Invalid();
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw Invalid(); }
    }

    internal static byte[] Compress(byte[] bytes, NativeRawPageWork work)
    {
        using var output = new BoundedStream(NativeRawPageSchema.MaximumPageBytes, work);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            for (var offset = 0; offset < bytes.Length; offset += 16_384)
            { work.Check(); gzip.Write(bytes, offset, Math.Min(16_384, bytes.Length - offset)); }
        work.Check();
        return output.ToArray();
    }

    internal static byte[] Inflate(byte[] payload, int expectedBytes)
    {
        if (payload.Length is < 1 or > NativeRawPageSchema.MaximumPageBytes
            || expectedBytes is < 2 or > NativeRawPageSchema.MaximumPageBytes) throw Invalid();
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new BoundedStream(expectedBytes, null);
            gzip.CopyTo(output, 16_384);
            if (output.Length != expectedBytes) throw Invalid();
            return output.ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NativeRawPageLimitException or OverflowException)
        { throw Invalid(); }
    }

    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static bool HashEquals(string? a, string? b) => IsHash(a) && IsHash(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static InvalidDataException Invalid() => new("native_observation_page_integrity_invalid");

    private static void UniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); UniqueKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueKeys(item);
    }

    private sealed class BoundedStream(int maximumBytes, NativeRawPageWork? work) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Guard(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Guard(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Guard(1); base.WriteByte(value); }
        private void Guard(int count)
        {
            work?.Check();
            if (count > maximumBytes - Length) throw new NativeRawPageLimitException("native_raw_page_size_limit");
        }
    }
}

internal sealed class NativeRawPageLimitException(string reason) : Exception(reason);
internal sealed class NativeRawPageWork(CancellationToken ct, TimeSpan maximum, Func<TimeSpan>? elapsed = null)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    internal void Check()
    {
        ct.ThrowIfCancellationRequested();
        if ((elapsed?.Invoke() ?? clock.Elapsed) >= maximum)
            throw new NativeRawPageLimitException("native_raw_page_time_limit");
    }
}
