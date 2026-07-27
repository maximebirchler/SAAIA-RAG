using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed class DoclingClient(
    IHttpClientFactory httpClientFactory,
    DocumentIntelligenceOptions options)
{
    private readonly SemaphoreSlim _conversionSlots = new(
        Math.Clamp(options.ServiceWorkerConcurrency, 1, 32),
        Math.Clamp(options.ServiceWorkerConcurrency, 1, 32));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public async Task<DoclingConvertResponse> ConvertPdfAsync(
        string pdfPath,
        CancellationToken ct,
        bool? forceOcrOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("Document-intelligence source file was not found.", pdfPath);

        await _conversionSlots.WaitAsync(ct);
        try
        {
            var client = httpClientFactory.CreateClient("docling");
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/convert/file");
            using var form = new MultipartFormDataContent();
            await using var source = new FileStream(
                pdfPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            using var file = new StreamContent(source);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "files", Path.GetFileName(pdfPath));
            Add(form, "to_formats", "json");
            Add(form, "do_ocr", options.DoOcr);
            Add(form, "force_ocr", forceOcrOverride ?? options.ForceOcr);
            Add(form, "ocr_preset", options.OcrPreset);
            Add(form, "pdf_backend", options.PdfBackend);
            Add(form, "table_mode", options.TableMode);
            Add(form, "table_cell_matching", options.TableCellMatching);
            Add(form, "do_table_structure", options.DoTableStructure);
            Add(form, "include_images", options.IncludeImages);
            Add(form, "include_page_images", options.IncludePageImages);
            Add(
                form,
                "saaia_heading_hierarchy_enabled",
                options.HeadingHierarchyEnabled);
            Add(
                form,
                "saaia_heading_hierarchy_use_bookmarks",
                options.HeadingHierarchyUseBookmarks);
            Add(
                form,
                "saaia_heading_hierarchy_use_numbering",
                options.HeadingHierarchyUseNumbering);
            Add(
                form,
                "saaia_heading_hierarchy_use_style",
                options.HeadingHierarchyUseStyle);
            Add(
                form,
                "saaia_heading_hierarchy_max_level",
                Math.Clamp(options.HeadingHierarchyMaxLevel, 1, 100));
            Add(
                form,
                "saaia_heading_hierarchy_bookmark_threshold",
                Math.Clamp(
                        options.HeadingHierarchyBookmarkMatchThreshold,
                        0d,
                        1d)
                    .ToString(CultureInfo.InvariantCulture));
            Add(form, "abort_on_error", true);
            Add(form, "document_timeout", (int)options.ResolveTimeout().TotalSeconds);
            request.Content = form;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.ResolveTimeout());
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            var bytes = await ReadBoundedAsync(
                response.Content,
                options.ResolveMaxResponseBytes(),
                timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = bytes.Length == 0
                    ? ""
                    : Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 2_000)));
                throw new HttpRequestException(
                    $"Docling conversion failed with HTTP {(int)response.StatusCode}: {detail}",
                    inner: null,
                    response.StatusCode);
            }

            var result = JsonSerializer.Deserialize<DoclingConvertResponse>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Docling returned an empty JSON response.");
            if (!string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Docling conversion status was '{result.Status}'.");
            if (result.Errors.Count > 0)
                throw new InvalidDataException($"Docling reported {result.Errors.Count} conversion error(s).");
            if (result.Document.JsonContent is null)
                throw new InvalidDataException("Docling did not return its canonical JSON document.");
            if (!string.Equals(result.Document.JsonContent.SchemaName, "DoclingDocument", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Unsupported Docling schema '{result.Document.JsonContent.SchemaName}'.");

            return result;
        }
        finally
        {
            _conversionSlots.Release();
        }
    }

    private static void Add(MultipartFormDataContent form, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Docling option '{name}' cannot be empty.");
        form.Add(new StringContent(value), name);
    }

    private static void Add(MultipartFormDataContent form, string name, bool value)
        => Add(form, name, value ? "true" : "false");

    private static void Add(MultipartFormDataContent form, string name, int value)
        => Add(form, name, value.ToString(CultureInfo.InvariantCulture));

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength.Value > maxBytes)
        {
            throw new InvalidDataException(
                $"Docling response exceeds the configured {maxBytes} byte limit.");
        }

        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException(
                    $"Docling response exceeds the configured {maxBytes} byte limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
