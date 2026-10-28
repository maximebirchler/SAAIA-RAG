/// <summary>Admits exact response bytes without a final full-entity copy; no partial body is returned.</summary>
internal static class DoclingResponseReader
{
    internal static async Task<ReadOnlyMemory<byte>> ReadAsync(HttpContent content, long maxBytes, CancellationToken ct,
        Func<MemoryStream>? createBuffer = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength.Value > maxBytes)
            throw TooLarge(maxBytes);

        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = createBuffer?.Invoke() ?? new MemoryStream();
        if (output.Length != 0 || output.Position != 0 || !output.CanWrite)
            throw new InvalidOperationException("docling_response_buffer_must_be_empty_writable");
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw TooLarge(maxBytes);
            output.Write(buffer, 0, read);
        }
        if (!output.TryGetBuffer(out var owned) || owned.Count != output.Length)
            throw new InvalidOperationException("docling_response_buffer_unavailable");
        // The read-only view owns the array reference, restricted to the written range.
        // MemoryStream.Dispose neither clears nor returns that array to a pool. No writer
        // escapes this method, and no reference to spare capacity reaches JSON consumers.
        return owned.AsMemory();
    }

    private static InvalidDataException TooLarge(long maxBytes)
        => new($"Docling response exceeds the configured {maxBytes} byte limit.");
}
