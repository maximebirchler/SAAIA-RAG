using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAAIA.Backend;

internal sealed class DoclingClient(
    IHttpClientFactory httpClientFactory,
    DocumentIntelligenceOptions options,
    ILogger<DoclingClient>? logger = null)
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
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("Document-intelligence source file was not found.", pdfPath);

        using var operation = BackendDiagnostics.Start("docling.conversion", logger);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ResolveTimeout());
        var slotAcquired = false;
        var attemptCount = 0;
        try
        {
            await _conversionSlots.WaitAsync(deadline.Token);
            slotAcquired = true;
            // Keep one source handle for every attempt. Reopening the pathname after an atomic
            // source replacement could otherwise mix two documents in one logical conversion.
            await using var source = new FileStream(pdfPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 128 * 1024, useAsync: true);
            for (var attempt = 1; attempt <= DoclingRetryPolicy.MaximumAttempts; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                attemptCount = attempt;
                operation.Set("attempt_count", attempt);
                using var attemptProbe = BackendDiagnostics.Start("docling.conversion_attempt", logger);
                attemptProbe.Set("attempt", attempt);
                string retryReason;
                try
                {
                    var result = await ConvertAttemptAsync(source, Path.GetFileName(pdfPath),
                        deadline.Token, forceOcrOverride);
                    deadline.Token.ThrowIfCancellationRequested();
                    DoclingRegionalTextRepairPolicy.RecordDiagnostics(operation, result);
                    DoclingRegionalTableRepairPolicy.RecordDiagnostics(operation, options, result);
                    attemptProbe.Complete();
                    operation.Set("retry_count", attempt - 1);
                    operation.Complete();
                    return result;
                }
                catch (HttpRequestException exception) when (DoclingRetryPolicy.IsTransient(exception))
                {
                    retryReason = exception.StatusCode is null ? "network_transport_error" : "upstream_temporarily_unavailable";
                    attemptProbe.Set("http_status", exception.StatusCode is null ? null : (int)exception.StatusCode);
                    if (attempt == DoclingRetryPolicy.MaximumAttempts)
                    {
                        operation.Set("retry_exhausted", true);
                        attemptProbe.Complete("failed", "retry_limit_reached");
                        // Do not expose an inner transport exception: it can contain a URI or body.
                        throw DoclingRetryPolicy.SafeFailure(exception.StatusCode);
                    }
                    attemptProbe.Complete("degraded", retryReason);
                }
                catch (Exception exception)
                {
                    attemptProbe.Fail(exception);
                    throw;
                }

                var delay = DoclingRetryPolicy.DelayAfterAttempt(attempt);
                operation.Set("retry_reason", retryReason);
                operation.Set("retry_count", attempt);
                attemptProbe.Set("retry_delay_ms", (int)delay.TotalMilliseconds);
                await Task.Delay(delay, deadline.Token);
            }
            throw new InvalidOperationException("docling_retry_policy_exhausted");
        }
        catch (OperationCanceledException)
        {
            operation.Set("attempt_count", attemptCount);
            operation.Complete("cancelled", ct.IsCancellationRequested ? "caller_cancelled" : "global_deadline_exceeded");
            throw;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
        finally
        {
            if (slotAcquired) _conversionSlots.Release();
        }
    }

    private async Task<DoclingConvertResponse> ConvertAttemptAsync(FileStream source, string fileName,
        CancellationToken ct, bool? forceOcrOverride)
    {
        var client = httpClientFactory.CreateClient("docling");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/convert/file");
        using var form = new MultipartFormDataContent();
        using var file = new ReplayablePdfContent(source);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", fileName);
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
        Add(form, "image_export_mode", options.IncludeImages || options.IncludePageImages
            ? "embedded" : "placeholder");
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

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        var bytes = await ReadBoundedAsync(
            response.Content,
            options.ResolveMaxResponseBytes(),
            ct);
        if (!response.IsSuccessStatusCode)
        {
            throw DoclingRetryPolicy.FailureForResponse(response.StatusCode, bytes);
        }

        DoclingConvertResponse result;
        try
        {
            result = JsonSerializer.Deserialize<DoclingConvertResponse>(bytes, JsonOptions)
                ?? throw new InvalidDataException("docling_response_json_empty");
        }
        catch (JsonException)
        {
            // An inner parser exception can contain an upstream key/path. Keep ordinary logs content-free.
            throw new InvalidDataException("docling_response_json_invalid");
        }
        if (!string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("docling_conversion_status_invalid");
        if (result.Errors.Count > 0)
            throw new InvalidDataException($"Docling reported {result.Errors.Count} conversion error(s).");
        if (result.Document.JsonContent is null)
            throw new InvalidDataException("Docling did not return its canonical JSON document.");
        if (!string.Equals(result.Document.JsonContent.SchemaName, "DoclingDocument", StringComparison.Ordinal))
            throw new InvalidDataException(
                "docling_schema_unsupported");

        result.RequestedForceOcr = forceOcrOverride ?? options.ForceOcr;
        DoclingExecutionMetadataPolicy.Validate(options, result, result.RequestedForceOcr.Value);
        return result;
    }

    // A multipart attempt owns its HttpContent, while the logical conversion owns the source.
    // Disposing an attempt must not dispose the handle needed by the following attempt.
    private sealed class ReplayablePdfContent(FileStream source) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = source.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            source.Position = 0;
            return source.CopyToAsync(stream, 128 * 1024, cancellationToken);
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
