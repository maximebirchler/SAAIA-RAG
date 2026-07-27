using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using Xunit;

public sealed class DoclingClientTests
{
    [Fact]
    public async Task ConvertPdfAsync_SendsBoundedStructuredRequestAndValidatesResponse()
    {
        string? requestBody = null;
        var responseBody = JsonSerializer.Serialize(BuildResponse());
        var factory = new StubHttpClientFactory(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "http://docling.test/v1/convert/file",
                request.RequestUri!.AbsoluteUri);
            requestBody = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });
        var options = new DocumentIntelligenceOptions
        {
            BaseUrl = "http://docling.test",
            DoOcr = true,
            ForceOcr = false,
            TableMode = "accurate",
            TableCellMatching = true
        };
        var client = new DoclingClient(factory, options);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            var response = await client.ConvertPdfAsync(path, CancellationToken.None);

            Assert.Equal("success", response.Status);
            Assert.NotNull(response.Document.JsonContent);
            Assert.Contains("to_formats", requestBody);
            Assert.Contains("do_ocr", requestBody);
            Assert.Contains("table_cell_matching", requestBody);
            Assert.Contains(
                "saaia_heading_hierarchy_enabled",
                requestBody);
            Assert.Contains(
                "saaia_heading_hierarchy_bookmark_threshold",
                requestBody);
            Assert.Contains("accurate", requestBody);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConvertPdfAsync_CanForceOcrForOneAdaptiveConversion()
    {
        string? requestBody = null;
        var responseBody = JsonSerializer.Serialize(BuildResponse());
        var factory = new StubHttpClientFactory(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new DoclingClient(
            factory,
            new()
            {
                BaseUrl = "http://docling.test",
                DoOcr = true,
                ForceOcr = false
            });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            await client.ConvertPdfAsync(
                path,
                CancellationToken.None,
                forceOcrOverride: true);

            Assert.Contains("force_ocr", requestBody);
            Assert.Contains("true", requestBody);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConvertPdfAsync_RejectsResponsesAboveConfiguredLimit()
    {
        var oversized = new byte[(1024 * 1024) + 1];
        var factory = new StubHttpClientFactory(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversized)
            }));
        var client = new DoclingClient(
            factory,
            new() { MaxResponseBytes = 1024 * 1024 });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1]);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.ConvertPdfAsync(path, CancellationToken.None));

            Assert.Contains("exceeds the configured", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConvertPdfAsync_RespectsConfiguredServiceCapacity()
    {
        var responseBody = JsonSerializer.Serialize(BuildResponse());
        var firstRequestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequests = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var activeRequests = 0;
        var maximumActiveRequests = 0;
        var factory = new StubHttpClientFactory(async _ =>
        {
            Interlocked.Increment(ref requestCount);
            var active = Interlocked.Increment(ref activeRequests);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumActiveRequests);
                if (active <= observed)
                    break;
            }
            while (Interlocked.CompareExchange(
                       ref maximumActiveRequests,
                       active,
                       observed)
                   != observed);

            firstRequestEntered.TrySetResult();
            await releaseRequests.Task;
            Interlocked.Decrement(ref activeRequests);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new DoclingClient(
            factory,
            new()
            {
                ServiceWorkerConcurrency = 1,
                TimeoutSeconds = 30
            });
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(firstPath, [1]);
            await File.WriteAllBytesAsync(secondPath, [2]);

            var first = client.ConvertPdfAsync(
                firstPath,
                CancellationToken.None);
            await firstRequestEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            var second = client.ConvertPdfAsync(
                secondPath,
                CancellationToken.None);
            await Task.Delay(100);

            Assert.Equal(1, Volatile.Read(ref requestCount));
            Assert.Equal(1, Volatile.Read(ref maximumActiveRequests));

            releaseRequests.TrySetResult();
            await Task.WhenAll(first, second);

            Assert.Equal(2, Volatile.Read(ref requestCount));
            Assert.Equal(1, Volatile.Read(ref maximumActiveRequests));
        }
        finally
        {
            releaseRequests.TrySetResult();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public void ProviderSwitch_IsExplicitAndRejectsUnknownProviders()
    {
        Assert.False(IngestionWorker.ResolveUseDocling(new() { Enabled = false }));
        Assert.True(IngestionWorker.ResolveUseDocling(new()
        {
            Enabled = true,
            Provider = "DOCLING"
        }));
        Assert.Throws<InvalidOperationException>(
            () => IngestionWorker.ResolveUseDocling(new()
            {
                Enabled = true,
                Provider = "unknown"
            }));
    }

    [Fact]
    public void ResolveDoclingForceOcr_UsesNativeCorruptionSignal()
    {
        var text = "Mesure\u008eindustrielle couche texte corrompue";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var page = new ExtractedPdfPage(
            1,
            text,
            words.Length,
            text.Length,
            [1],
            PdfPageExtractionQuality.FromText(
                text,
                words.Length,
                text.Length));
        var native = new PdfExtractionResult(
            words.Select(word => new WordToken(word, 1)).ToList(),
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.True(IngestionWorker.ResolveDoclingForceOcr(
            new() { DoOcr = true },
            native));
        Assert.False(IngestionWorker.ResolveDoclingForceOcr(
            new() { DoOcr = false },
            native));
    }

    [Fact]
    public void OcrDiagnostics_DoNotInventPerPageAttribution()
    {
        var response = BuildResponse();
        response.Timings["ocr"] = new()
        {
            Count = 2,
            Scope = "page",
            Times = [0.25, 0.5]
        };
        var page = new ExtractedPdfPage(
            1,
            "fixture",
            1,
            7,
            [1]);
        var extraction = new PdfExtractionResult(
            [new("fixture", 1)],
            [page],
            PdfExtractionQualitySummary.FromPages([page]),
            "docling");

        var diagnostics = IngestionWorker.BuildDoclingOcrDiagnostics(
            new() { DoOcr = true },
            response,
            extraction);

        Assert.Equal("docling_auto", diagnostics.Mode);
        Assert.Equal(2, diagnostics.AttemptedPageCount);
        Assert.Empty(diagnostics.AttemptedPages);
        Assert.Empty(diagnostics.PagesWithOcrText);
        Assert.Equal("engine_managed", diagnostics.CoverageStatus);
        Assert.Equal(750, IngestionWorker.ResolveDoclingTimingMs(response, "ocr"));
    }

    [Fact]
    public async Task Readiness_RequiresHealthyProviderOnlyWhenEnabled()
    {
        var factory = new StubHttpClientFactory(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)));
        var details = new Dictionary<string, object?>();

        var ready = await ReadyEndpoints.ProbeDocumentIntelligenceReadinessAsync(
            factory,
            new()
            {
                Enabled = true,
                Provider = "docling",
                Device = "cpu",
                NumThreads = 8,
                ServiceWorkerConcurrency = 2,
                NativeTextCoverageReconciliationEnabled = true,
                NativeTextCoverageMinimumLineCoverage = 0.92
            },
            details,
            CancellationToken.None);

        Assert.True(ready);
        Assert.Equal("ready", details["document_intelligence_status"]);
        Assert.Equal(true, details["document_intelligence_ready"]);
        Assert.Equal(2, details["document_intelligence_client_max_conversions"]);
        Assert.Equal(
            true,
            details["document_intelligence_native_text_reconciliation_enabled"]);
        Assert.Equal(
            0.92,
            details["document_intelligence_native_text_minimum_line_coverage"]);

        details.Clear();
        var disabled = await ReadyEndpoints.ProbeDocumentIntelligenceReadinessAsync(
            new StubHttpClientFactory(_ => throw new InvalidOperationException()),
            new() { Enabled = false },
            details,
            CancellationToken.None);
        Assert.True(disabled);
        Assert.Equal("disabled", details["document_intelligence_status"]);
    }

    [Fact]
    public void Readiness_RejectsMissingCanonicalProvenanceBeforeIngestion()
    {
        var rag = new RagOptions
        {
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EmbeddingsModelRevision = "",
            EmbeddingsRuntimeRevision = "sha256:tei",
            QdrantRuntimeRevision = "sha256:qdrant"
        };
        var details = new Dictionary<string, object?>();

        var ready = ReadyEndpoints.ProbeCanonicalProvenanceReadiness(
            rag,
            new() { CanonicalArtifactsEnabled = true },
            "head-source-hash",
            details);

        Assert.False(ready);
        Assert.Equal("invalid", details["canonical_provenance_status"]);
        Assert.Equal(false, details["canonical_provenance_ready"]);
    }

    [Fact]
    public void Readiness_AcceptsPinnedCanonicalProvenance()
    {
        var rag = new RagOptions
        {
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EmbeddingsModelRevision = "model-commit",
            EmbeddingsRuntimeRevision = "sha256:tei",
            QdrantRuntimeRevision = "sha256:qdrant"
        };
        var details = new Dictionary<string, object?>();

        var ready = ReadyEndpoints.ProbeCanonicalProvenanceReadiness(
            rag,
            new() { CanonicalArtifactsEnabled = true },
            "head-source-hash",
            details);

        Assert.True(ready);
        Assert.Equal("ready", details["canonical_provenance_status"]);
        Assert.Equal("model-commit", details["embeddings_model_revision"]);
    }

    private static DoclingConvertResponse BuildResponse()
        => new()
        {
            Status = "success",
            Document = new()
            {
                FileName = "fixture.pdf",
                JsonContent = new()
                {
                    SchemaName = "DoclingDocument",
                    Version = "1.10.0",
                    Pages =
                    {
                        ["1"] = new()
                        {
                            PageNumber = 1,
                            Size = new() { Width = 100, Height = 200 }
                        }
                    }
                }
            }
        };

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(responder))
            {
                BaseAddress = new("http://docling.test/")
            };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
