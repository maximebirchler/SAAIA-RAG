using System.Text.Json;

/// <summary>Bound JSON responses independently of Content-Length, including chunked responses.</summary>
internal static class TeiResponseReader
{
    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maximumBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("tei_response_size_limit_exceeded");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)bytes.Length)), ct);
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
            if (bytes.Length > maximumBytes)
                throw new InvalidDataException("tei_response_size_limit_exceeded");
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
}
