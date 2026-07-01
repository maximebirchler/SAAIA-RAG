using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ApiClientDocumentsTransitionTests
{
    [Fact]
    public async Task AdminRagTestRetrievalAsync_posts_to_admin_endpoint_with_full_retrieval_body()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/admin/rag/test-retrieval", req.RequestUri!.AbsolutePath);
            Assert.True(req.Headers.TryGetValues("X-Admin-Key", out var adminValues));
            Assert.Equal("test-admin-key", Assert.Single(adminValues));
            Assert.False(req.Headers.Contains("X-Api-Key"));

            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"metrics":{"tookMs":1,"returned":0}}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler, adminKey: "test-admin-key");
        var result = await sut.AdminRagTestRetrievalAsync(
            "test retrieval",
            "Cuisine",
            topK: 99,
            mode: "broad",
            CancellationToken.None);

        Assert.True(result.TryGetProperty("items", out _));
        Assert.NotNull(capturedBody);
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("test retrieval", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
        Assert.Equal("Cuisine", body.RootElement.GetProperty("categoryPath").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryRef").ValueKind);
        Assert.Equal(50, body.RootElement.GetProperty("topK").GetInt32());
        Assert.Equal("broad", body.RootElement.GetProperty("mode").GetString());
        Assert.True(body.RootElement.GetProperty("includeContextualSnippet").GetBoolean());
        Assert.True(body.RootElement.GetProperty("includeDiagnostics").GetBoolean());
    }

    [Fact]
    public async Task RagSearchToolAsync_does_not_retry_rag_search_busy()
    {
        var calls = 0;
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal("/rag/search", req.RequestUri!.AbsolutePath);
            calls++;

            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.Add("Retry-After", "0");
            busy.Content = new StringContent("""{"error":"rag_search_busy"}""", Encoding.UTF8, "application/json");
            return busy;
        });

        var sut = CreateApiClient(handler);
        var ex = await Assert.ThrowsAsync<ApiClientBackendBusyException>(() =>
            sut.RagSearchToolAsync("needle", 3, null, "balanced", CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(1, ex.RetryAfterSeconds);
        Assert.Contains("rag_search_busy", ex.ResponseBody);
    }

    [Fact]
    public async Task RagSearchToolAsync_retries_one_non_rag_429_response_before_returning_success()
    {
        var calls = 0;
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal("/rag/search", req.RequestUri!.AbsolutePath);
            calls++;

            if (calls == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.Add("Retry-After", "0");
                busy.Content = new StringContent("""{"error":"transient_rate_limit"}""", Encoding.UTF8, "application/json");
                return busy;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        var result = await sut.RagSearchToolAsync("needle", 3, null, "balanced", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(result.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task RagSearchToolAsync_throws_backend_busy_after_retry_budget_is_exhausted()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.Add("Retry-After", "0");
            busy.Content = new StringContent("""{"error":"rag_search_busy"}""", Encoding.UTF8, "application/json");
            return busy;
        });

        var sut = CreateApiClient(handler);
        var ex = await Assert.ThrowsAsync<ApiClientBackendBusyException>(() =>
            sut.RagSearchToolAsync("needle", 3, null, "balanced", CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(1, ex.RetryAfterSeconds);
        Assert.Contains("rag_search_busy", ex.ResponseBody);
    }

    [Fact]
    public async Task RagSearchToolAsync_keeps_non_rag_busy_429_as_http_error()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            rateLimited.Headers.Add("Retry-After", "0");
            rateLimited.Content = new StringContent("""{"error":"generic_rate_limit"}""", Encoding.UTF8, "application/json");
            return rateLimited;
        });

        var sut = CreateApiClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.RagSearchToolAsync("needle", 3, null, "balanced", CancellationToken.None));

        Assert.IsNotType<ApiClientBackendBusyException>(ex);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task ToolAgent_rag_search_busy_returns_structured_payload_instead_of_empty_hits()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.Add("Retry-After", "0");
            busy.Content = new StringContent("""{"error":"rag_search_busy"}""", Encoding.UTF8, "application/json");
            return busy;
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"query":"needle","topK":3,"mode":"balanced"}""");

        var result = await InvokePrivateToolAsync(sut, "ExecRagSearchAsync", args.RootElement);

        Assert.Equal(1, calls);
        Assert.Equal("rag_search_busy", result.GetProperty("error").GetString());
        Assert.True(result.GetProperty("busy").GetBoolean());
        Assert.Equal(1, result.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Empty(result.GetProperty("hits").EnumerateArray());
        Assert.Equal("retry_later", result.GetProperty("guidance").GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_all_busy_preserves_top_level_busy_payload()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            busy.Headers.Add("Retry-After", "0");
            busy.Content = new StringContent("""{"error":"rag_search_busy"}""", Encoding.UTF8, "application/json");
            return busy;
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["first","second"],"topK":3,"mode":"balanced"}""");

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Equal(2, calls);
        Assert.Equal("rag_search_busy", result.GetProperty("error").GetString());
        Assert.True(result.GetProperty("busy").GetBoolean());
        Assert.Equal(1, result.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Empty(result.GetProperty("hits").EnumerateArray());
        var busyQueries = result.GetProperty("meta").GetProperty("busyQueries").EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
        Assert.Equal(["first", "second"], busyQueries);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_partial_busy_keeps_hits_and_marks_busy_queries_only_in_meta()
    {
        var calls = 0;
        var handler = new StubHttpHandler(req =>
        {
            calls++;
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("busy-query", StringComparison.Ordinal))
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.Add("Retry-After", "0");
                busy.Content = new StringContent("""{"error":"rag_search_busy"}""", Encoding.UTF8, "application/json");
                return busy;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Docs/ok.pdf",
                          "docName": "ok.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "ok-1",
                          "text": "usable source"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["busy-query","ok-query"],"topK":3,"mode":"balanced"}""");

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Equal(2, calls);
        Assert.False(result.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True);
        Assert.False(result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String);
        var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal("Docs/ok.pdf", hit.GetProperty("docPath").GetString());
        var busyQueries = result.GetProperty("meta").GetProperty("busyQueries").EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
        Assert.Equal(["busy-query"], busyQueries);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_keeps_raw_query_before_normalized_variant()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["Quel dessert fran\u00E7ais choisir pour un repas chic ?"],"topK":3,"mode":"balanced"}""");

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        var queries = result.GetProperty("meta").GetProperty("queries")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();
        Assert.True(queries.Length >= 2);
        Assert.Equal("Quel dessert fran\u00E7ais choisir pour un repas chic ?", queries[0]);
        Assert.Equal("Quel dessert fran\u00E7ais choisir pour un repas chic", queries[1]);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_limits_client_side_fanout_parallelism()
    {
        var active = 0;
        var maxActive = 0;
        var calls = 0;
        var handler = new AsyncStubHttpHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            try
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxActive);
                    if (current <= observed)
                        break;
                }
                while (Interlocked.CompareExchange(ref maxActive, current, observed) != observed);

                await Task.Delay(50, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["q1","q2","q3","q4","q5","q6","q7","q8"],"topK":3,"mode":"balanced"}""");

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Equal(8, calls);
        Assert.True(maxActive <= 2, $"Expected at most 2 concurrent RAG calls, observed {maxActive}.");
        Assert.Equal(2, result.GetProperty("meta").GetProperty("fanoutParallelism").GetInt32());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_times_out_slow_source_exploration_query_and_continues()
    {
        using var timeoutOverride = ToolAgentOrchestrator.OverrideRagMultiSearchSourceExplorationQueryTimeoutForTests(TimeSpan.FromMilliseconds(50));
        var calls = new List<string>();
        var handler = new AsyncStubHttpHandler(async (req, ct) =>
        {
            using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            var query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            lock (calls)
                calls.Add(query);

            if (string.Equals(query, "slow", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.72,
                          "docPath": "Knowledge/fast.pdf",
                          "docName": "fast.pdf",
                          "pageStart": 4,
                          "pageEnd": 4,
                          "chunkId": "fast-1",
                          "text": "A useful result returned by the fast query."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["slow", "fast"],
              "topK": 4,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Contains("slow", calls);
        Assert.Contains("fast", calls);
        Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal(50, result.GetProperty("meta").GetProperty("perQueryTimeoutMs").GetInt32());

        var queryRuns = result.GetProperty("meta").GetProperty("queryRuns")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(queryRuns, run =>
            string.Equals(run.GetProperty("query").GetString(), "slow", StringComparison.OrdinalIgnoreCase)
            && string.Equals(run.GetProperty("error").GetString(), "rag_search_query_timeout", StringComparison.OrdinalIgnoreCase)
            && run.GetProperty("timeoutMs").GetInt32() == 50);
        Assert.Contains(queryRuns, run =>
            string.Equals(run.GetProperty("query").GetString(), "fast", StringComparison.OrdinalIgnoreCase)
            && run.GetProperty("hitCount").GetInt32() == 1
            && run.GetProperty("timeoutMs").GetInt32() == 50);

        var degraded = result.GetProperty("meta").GetProperty("degradedRetrievers")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("rag_search_query_timeout", degraded);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_ignores_untrusted_source_exploration_category_scope()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Cuisine/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "source-1",
                          "text": "A useful source candidate."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["plan de repas semaine"],
              "topK": 4,
              "categoryPath": "Juridique",
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedBodies);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryPath").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryRef").ValueKind);
        }

        var meta = result.GetProperty("meta");
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("categoryPath").ValueKind);
        Assert.Equal("Juridique", meta.GetProperty("requestedCategory").GetString());
        Assert.Equal("Juridique", meta.GetProperty("rejectedCategoryScope").GetString());
        Assert.Equal("source_exploration_scope_not_supported_by_query", meta.GetProperty("rejectedCategoryScopeReason").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_does_not_special_case_cuisine_scope_for_meal_queries()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Cuisine/PDF/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "source-1",
                          "text": "A useful source candidate."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["plan de repas semaine petit dejeuner diner souper"],
              "topK": 4,
              "categoryPath": "Cuisine/PDF",
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedBodies);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryPath").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryRef").ValueKind);
        }

        var meta = result.GetProperty("meta");
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("categoryPath").ValueKind);
        Assert.Equal("Cuisine/PDF", meta.GetProperty("requestedCategory").GetString());
        Assert.Equal("Cuisine/PDF", meta.GetProperty("rejectedCategoryScope").GetString());
        Assert.Equal("source_exploration_scope_not_supported_by_query", meta.GetProperty("rejectedCategoryScopeReason").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_preserves_trusted_source_exploration_category_scope()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Domain/Selected/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "source-1",
                          "text": "A useful source candidate."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["weekly plan candidates"],
              "topK": 4,
              "categoryPath": "Domain/Selected",
              "trustCategoryScope": true,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedBodies);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal("Domain/Selected", body.RootElement.GetProperty("categoryPath").GetString());
        }

        var meta = result.GetProperty("meta");
        Assert.Equal("Domain/Selected", meta.GetProperty("categoryPath").GetString());
        Assert.Equal("Domain/Selected", meta.GetProperty("requestedCategory").GetString());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("rejectedCategoryScope").ValueKind);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_probes_catalog_categories_before_unscoped_source_exploration()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/documents")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[],"nextLink":null,"totals":{"total":0}}""", Encoding.UTF8, "application/json")
                };
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();
            var hasHits = string.Equals(categoryPath, "Knowledge/Good", StringComparison.OrdinalIgnoreCase);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    hasHits
                        ? """
                          {
                            "items": [
                              {
                                "score": 0.9,
                                "docPath": "Knowledge/Good/source.pdf",
                                "docName": "source.pdf",
                                "pageStart": 2,
                                "pageEnd": 2,
                                "chunkId": "source-1",
                                "text": "A useful source candidate."
                              }
                            ]
                          }
                          """
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Knowledge/Other",
                        DisplayName = "Other",
                        TotalDocuments = 10,
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Knowledge/Good",
                        DisplayName = "Good",
                        TotalDocuments = 8,
                        Ordinal = 2
                    }
                }
            }
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["weekly plan candidates"],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedBodies);
        Assert.DoesNotContain(capturedBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return body.RootElement.GetProperty("categoryPath").ValueKind == JsonValueKind.Null;
        });
        Assert.Contains(capturedBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return string.Equals(body.RootElement.GetProperty("categoryPath").GetString(), "Knowledge/Good", StringComparison.OrdinalIgnoreCase);
        });

        var meta = result.GetProperty("meta");
        Assert.Equal("Knowledge/Good", meta.GetProperty("categoryPath").GetString());
        Assert.True(meta.GetProperty("categoryInferred").GetBoolean());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_uses_catalog_shortlist_before_noisy_category_probe()
    {
        var capturedRagBodies = new List<string>();
        var capturedCatalogQueries = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/categories")
            {
                var path = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("path") ?? string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        string.Equals(path, "Domain", StringComparison.OrdinalIgnoreCase)
                            ? """
                              {
                                "value": [
                                  {
                                    "categoryPath": "Domain/Useful",
                                    "canonicalName": "Useful child",
                                    "displayOrder": 1,
                                    "documentCount": 12,
                                    "aliases": []
                                  }
                                ],
                                "nextLink": null
                              }
                              """
                            : """{"value":[],"nextLink":null}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/documents")
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("q") ?? string.Empty;
                capturedCatalogQueries.Add(q);
                var hasTargetCatalogMatch = q.Contains("target", StringComparison.OrdinalIgnoreCase)
                                            || q.Contains("evidence", StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        hasTargetCatalogMatch
                            ? """
                              {
                                "value": [
                                  {
                                    "docId": "doc-target-1",
                                    "docPath": "Domain/source.pdf",
                                    "canonicalName": "Target evidence source.pdf",
                                    "categoryCanonicalName": "Domain",
                                    "categoryPath": "Domain",
                                    "status": "indexed"
                                  }
                                ],
                                "nextLink": null,
                                "totals": { "total": 1 }
                              }
                              """
                            : """{"value":[],"nextLink":null,"totals":{"total":0}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRagBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var query = parsed.RootElement.GetProperty("query").GetString() ?? string.Empty;
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();

            var isNoisyBroadHit = string.Equals(categoryPath, "Documents", StringComparison.OrdinalIgnoreCase)
                                  && query.Contains("planning", StringComparison.OrdinalIgnoreCase);
            var isTargetHit = string.Equals(categoryPath, "Domain/Useful", StringComparison.OrdinalIgnoreCase)
                              && (query.Contains("target", StringComparison.OrdinalIgnoreCase)
                                  || query.Contains("evidence", StringComparison.OrdinalIgnoreCase));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    isNoisyBroadHit || isTargetHit
                        ? JsonSerializer.Serialize(new
                        {
                            items = new[]
                            {
                                new
                                {
                                    score = isTargetHit ? 0.95 : 0.55,
                                    docPath = isTargetHit ? "Domain/Useful/source.pdf" : "Documents/Versions/noise.pdf",
                                    docName = isTargetHit ? "source.pdf" : "noise.pdf",
                                    pageStart = 2,
                                    pageEnd = 2,
                                    chunkId = isTargetHit ? "target-1" : "noise-1",
                                    text = isTargetHit ? "Target evidence candidate." : "Planning words in an unrelated versioning document."
                                }
                            }
                        })
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Documents",
                        DisplayName = "Documents with versions",
                        TotalDocuments = 100,
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Domain",
                        DisplayName = "Useful domain",
                        TotalDocuments = 12,
                        Ordinal = 2
                    }
                }
            }
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["planning options", "options planning", "target evidence", "evidence target"],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Contains(capturedCatalogQueries, q => q.Contains("target", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(capturedRagBodies);
        Assert.DoesNotContain(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return string.Equals(body.RootElement.GetProperty("categoryPath").GetString(), "Documents", StringComparison.OrdinalIgnoreCase);
        });

        var meta = result.GetProperty("meta");
        Assert.Equal("Domain/Useful", meta.GetProperty("categoryPath").GetString());
        Assert.True(meta.GetProperty("categoryInferred").GetBoolean());
        Assert.Equal("Domain/Useful", mem.Execution.LastRagInferredCategoryScope);
        Assert.Equal("catalog_probe", mem.Execution.LastRagInferredCategoryReason);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_applies_repeated_catalog_signal_for_source_exploration_scope()
    {
        var capturedRagBodies = new List<string>();
        var capturedCatalogQueries = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/categories")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[],"nextLink":null}""", Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/documents")
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("q") ?? string.Empty;
                capturedCatalogQueries.Add(q);
                object[] documents = q.Equals("candidate", StringComparison.OrdinalIgnoreCase)
                    ? Enumerable.Range(1, 6)
                        .Select(index => new
                        {
                            docId = $"doc-strong-{index}",
                            docPath = $"Knowledge/source-{index}.pdf",
                            canonicalName = $"Strong source {index}.pdf",
                            categoryCanonicalName = "Knowledge",
                            categoryPath = "Knowledge",
                            status = "indexed"
                        })
                        .Cast<object>()
                        .ToArray()
                    : q.Equals("noise", StringComparison.OrdinalIgnoreCase)
                        ? new object[]
                        {
                            new
                            {
                                docId = "doc-noise-1",
                                docPath = "Other/noise.pdf",
                                canonicalName = "Noise source.pdf",
                                categoryCanonicalName = "Other",
                                categoryPath = "Other",
                                status = "indexed"
                            }
                        }
                        : Array.Empty<object>();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            value = documents,
                            nextLink = (string?)null,
                            totals = new { total = documents.Length }
                        }),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRagBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var query = parsed.RootElement.GetProperty("query").GetString() ?? string.Empty;
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Equals(categoryPath, "Knowledge", StringComparison.OrdinalIgnoreCase)
                    && query.Contains("candidate", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.Serialize(new
                        {
                            items = new[]
                            {
                                new
                                {
                                    score = 0.94,
                                    docPath = "Knowledge/source-1.pdf",
                                    docName = "source-1.pdf",
                                    pageStart = 3,
                                    pageEnd = 3,
                                    chunkId = "strong-1",
                                    text = "Supported candidate evidence."
                                }
                            }
                        })
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Knowledge",
                        DisplayName = "Knowledge",
                        TotalDocuments = 6,
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Other",
                        DisplayName = "Other",
                        TotalDocuments = 1,
                        Ordinal = 2
                    }
                }
            }
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["morning candidate", "main candidate", "evening candidate", "noise note"],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Contains(capturedCatalogQueries, q => q.Equals("candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedCatalogQueries, q => q.Equals("noise", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(capturedRagBodies);
        Assert.DoesNotContain(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return body.RootElement.GetProperty("categoryPath").ValueKind == JsonValueKind.Null;
        });
        Assert.Contains(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return string.Equals(body.RootElement.GetProperty("categoryPath").GetString(), "Knowledge", StringComparison.OrdinalIgnoreCase);
        });

        var meta = result.GetProperty("meta");
        Assert.Equal("Knowledge", meta.GetProperty("categoryPath").GetString());
        Assert.True(meta.GetProperty("categoryInferred").GetBoolean());
        Assert.Equal("Knowledge", mem.Execution.LastRagInferredCategoryScope);
        Assert.Equal("catalog_probe", mem.Execution.LastRagInferredCategoryReason);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_applies_repeated_catalog_signal_to_specific_child_scope()
    {
        var capturedRagBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/categories")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[],"nextLink":null}""", Encoding.UTF8, "application/json")
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/documents")
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("q") ?? string.Empty;
                var documents = q.Equals("candidate", StringComparison.OrdinalIgnoreCase)
                    ? Enumerable.Range(1, 6)
                        .Select(index => new
                        {
                            docId = $"doc-child-{index}",
                            docPath = $"Knowledge/Specific/source-{index}.pdf",
                            canonicalName = $"Specific source {index}.pdf",
                            categoryCanonicalName = "Knowledge",
                            categoryPath = "Knowledge",
                            status = "indexed"
                        })
                        .Cast<object>()
                        .ToArray()
                    : Array.Empty<object>();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            value = documents,
                            nextLink = (string?)null,
                            totals = new { total = documents.Length }
                        }),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRagBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var query = parsed.RootElement.GetProperty("query").GetString() ?? string.Empty;
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Equals(categoryPath, "Knowledge/Specific", StringComparison.OrdinalIgnoreCase)
                    && query.Contains("candidate", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.Serialize(new
                        {
                            items = new[]
                            {
                                new
                                {
                                    score = 0.94,
                                    docPath = "Knowledge/Specific/source-1.pdf",
                                    docName = "source-1.pdf",
                                    pageStart = 3,
                                    pageEnd = 3,
                                    chunkId = "specific-1",
                                    text = "Supported specific candidate evidence."
                                }
                            }
                        })
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Knowledge",
                        DisplayName = "Knowledge",
                        TotalDocuments = 6,
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Other",
                        DisplayName = "Other",
                        TotalDocuments = 1,
                        Ordinal = 2
                    }
                }
            }
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["morning candidate", "main candidate", "evening candidate", "noise note"],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedRagBodies);
        Assert.DoesNotContain(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return body.RootElement.GetProperty("categoryPath").ValueKind == JsonValueKind.Null;
        });
        Assert.Contains(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return string.Equals(body.RootElement.GetProperty("categoryPath").GetString(), "Knowledge/Specific", StringComparison.OrdinalIgnoreCase);
        });

        var meta = result.GetProperty("meta");
        Assert.Equal("Knowledge/Specific", meta.GetProperty("categoryPath").GetString());
        Assert.True(meta.GetProperty("categoryInferred").GetBoolean());
        Assert.Equal("Knowledge/Specific", mem.Execution.LastRagInferredCategoryScope);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_does_not_scope_source_exploration_from_single_catalog_term()
    {
        var capturedRagBodies = new List<string>();
        var capturedCatalogQueries = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/categories")
            {
                var path = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("path") ?? string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        string.Equals(path, "WeakDomain", StringComparison.OrdinalIgnoreCase)
                            ? """
                              {
                                "value": [
                                  {
                                    "categoryPath": "WeakDomain/Child",
                                    "canonicalName": "Weak child",
                                    "displayOrder": 1,
                                    "documentCount": 3,
                                    "aliases": []
                                  }
                                ],
                                "nextLink": null
                              }
                              """
                            : """{"value":[],"nextLink":null}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/catalog/documents")
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query).Get("q") ?? string.Empty;
                capturedCatalogQueries.Add(q);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        q.Equals("small", StringComparison.OrdinalIgnoreCase)
                            ? """
                              {
                                "value": [
                                  {
                                    "docId": "doc-weak-1",
                                    "docPath": "WeakDomain/Child/source.pdf",
                                    "canonicalName": "Weak source.pdf",
                                    "categoryCanonicalName": "WeakDomain",
                                    "categoryPath": "WeakDomain",
                                    "status": "indexed"
                                  }
                                ],
                                "nextLink": null,
                                "totals": { "total": 1 }
                              }
                              """
                            : """{"value":[],"nextLink":null,"totals":{"total":0}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRagBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.IsNullOrWhiteSpace(categoryPath)
                        ? JsonSerializer.Serialize(new
                        {
                            items = new[]
                            {
                                new
                                {
                                    score = 0.91,
                                    docPath = "Open/source.pdf",
                                    docName = "source.pdf",
                                    pageStart = 4,
                                    pageEnd = 4,
                                    chunkId = "open-1",
                                    text = "Open corpus candidate evidence."
                                }
                            }
                        })
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow,
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "WeakDomain",
                        DisplayName = "Weak domain",
                        TotalDocuments = 3,
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = "Open",
                        DisplayName = "Open domain",
                        TotalDocuments = 12,
                        Ordinal = 2
                    }
                }
            }
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["small plan", "plan options"],
              "topK": 8,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Contains(capturedCatalogQueries, q => q.Equals("small", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(capturedRagBodies);
        Assert.Contains(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return body.RootElement.GetProperty("categoryPath").ValueKind == JsonValueKind.Null;
        });
        Assert.DoesNotContain(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            return string.Equals(body.RootElement.GetProperty("categoryPath").GetString(), "WeakDomain/Child", StringComparison.OrdinalIgnoreCase);
        });

        var meta = result.GetProperty("meta");
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("categoryPath").ValueKind);
        Assert.False(meta.GetProperty("categoryInferred").GetBoolean());
        Assert.Null(mem.Execution.LastRagInferredCategoryScope);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_refines_parent_scope_to_previous_inferred_child_scope()
    {
        var capturedRagBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedRagBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var categoryPath = parsed.RootElement.GetProperty("categoryPath").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Equals(categoryPath, "Domain/Useful", StringComparison.OrdinalIgnoreCase)
                        ? """
                          {
                            "items": [
                              {
                                "score": 0.95,
                                "docPath": "Domain/Useful/source.pdf",
                                "docName": "source.pdf",
                                "pageStart": 2,
                                "pageEnd": 2,
                                "chunkId": "target-1",
                                "text": "Target evidence candidate."
                              }
                            ]
                          }
                          """
                        : """{"items":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var mem = new ToolMemory();
        mem.Execution.LastRagInferredCategoryScope = "Domain/Useful";
        mem.Execution.LastRagInferredCategoryReason = "catalog_probe";
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem);
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["target evidence"],
              "topK": 8,
              "categoryPath": "Domain",
              "trustCategoryScope": true,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedRagBodies);
        Assert.All(capturedRagBodies, capturedBody =>
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal("Domain/Useful", body.RootElement.GetProperty("categoryPath").GetString());
        });

        var meta = result.GetProperty("meta");
        Assert.Equal("Domain", meta.GetProperty("requestedCategory").GetString());
        Assert.Equal("Domain/Useful", meta.GetProperty("categoryPath").GetString());
        Assert.True(meta.GetProperty("categoryInferred").GetBoolean());
    }

    [Fact]
    public void Source_backed_deterministic_exploration_reuses_current_turn_inferred_scope_generically()
    {
        var reused = ToolAgentOrchestrator.ResolveSourceBackedExplorationPassCategoryScopeForTests(
            resolvedPassCategoryScope: null,
            currentCategoryScope: null,
            currentTurnInferredCategoryScope: "Domain/Useful",
            passOrigin: "deterministic_seed",
            passHasDocumentScope: false);

        Assert.Equal("Domain/Useful", reused.CategoryScope);
        Assert.True(reused.ReusedFromCurrentTurnInference);

        var llmBroadPass = ToolAgentOrchestrator.ResolveSourceBackedExplorationPassCategoryScopeForTests(
            resolvedPassCategoryScope: null,
            currentCategoryScope: null,
            currentTurnInferredCategoryScope: "Domain/Useful",
            passOrigin: "llm_planner",
            passHasDocumentScope: false);

        Assert.Null(llmBroadPass.CategoryScope);
        Assert.False(llmBroadPass.ReusedFromCurrentTurnInference);

        var explicitScope = ToolAgentOrchestrator.ResolveSourceBackedExplorationPassCategoryScopeForTests(
            resolvedPassCategoryScope: "Domain/Explicit",
            currentCategoryScope: null,
            currentTurnInferredCategoryScope: "Domain/Useful",
            passOrigin: "deterministic_seed",
            passHasDocumentScope: false);

        Assert.Equal("Domain/Explicit", explicitScope.CategoryScope);
        Assert.False(explicitScope.ReusedFromCurrentTurnInference);

        var documentScopedPass = ToolAgentOrchestrator.ResolveSourceBackedExplorationPassCategoryScopeForTests(
            resolvedPassCategoryScope: null,
            currentCategoryScope: null,
            currentTurnInferredCategoryScope: "Domain/Useful",
            passOrigin: "deterministic_seed",
            passHasDocumentScope: true);

        Assert.Null(documentScopedPass.CategoryScope);
        Assert.False(documentScopedPass.ReusedFromCurrentTurnInference);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_preserves_query_supported_source_exploration_category_scope()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Juridique/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "source-1",
                          "text": "A useful legal source candidate."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["juridique contrats"],
              "topK": 4,
              "categoryPath": "Juridique",
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.NotEmpty(capturedBodies);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal("Juridique", body.RootElement.GetProperty("categoryPath").GetString());
        }

        var meta = result.GetProperty("meta");
        Assert.Equal("Juridique", meta.GetProperty("categoryPath").GetString());
        Assert.Equal("Juridique", meta.GetProperty("requestedCategory").GetString());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("rejectedCategoryScope").ValueKind);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_skips_dominant_category_inference_for_source_exploration()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.8,
                          "docPath": "Juridique/PDF/legal.pdf",
                          "docName": "legal.pdf",
                          "categoryPath": "Juridique/PDF",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "chunkId": "legal-1",
                          "text": "A legal source candidate."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["plan de repas semaine"],
              "topK": 4,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Single(capturedBodies);
        var meta = result.GetProperty("meta");
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("categoryPath").ValueKind);
        Assert.False(meta.GetProperty("categoryInferred").GetBoolean());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_preserves_precise_query_hit_before_score_truncation()
    {
        var genericId = 0;
        var handler = new StubHttpHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            if (query.Contains("alpha beta module details procedure quantities timing source", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "items": [
                            {
                              "score": 0.12,
                              "docPath": "Knowledge/target.pdf",
                              "docName": "target.pdf",
                              "pageStart": 9,
                              "pageEnd": 9,
                              "chunkId": "target-1",
                              "text": "Alpha Beta Module exact procedure with quantities and timing.",
                              "matchedContentCards": [
                                { "title": "Alpha Beta Module", "kind": "section", "pageStart": 9 }
                              ],
                              "selectionHints": {
                                "evidenceRole": "supporting_context",
                                "supportScore": 9
                              }
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var id = System.Threading.Interlocked.Increment(ref genericId);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "items": [
                        {
                          "score": 0.99,
                          "docPath": "Knowledge/generic-{{id}}-a.pdf",
                          "docName": "generic-{{id}}-a.pdf",
                          "pageStart": 1,
                          "pageEnd": 1,
                          "chunkId": "generic-{{id}}-a",
                          "text": "Generic nearby overview {{id}} A."
                        },
                        {
                          "score": 0.98,
                          "docPath": "Knowledge/generic-{{id}}-b.pdf",
                          "docName": "generic-{{id}}-b.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkId": "generic-{{id}}-b",
                          "text": "Generic nearby overview {{id}} B."
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": [
                "alpha beta",
                "alpha module",
                "beta module",
                "alpha beta overview",
                "alpha beta procedure",
                "alpha beta quantities",
                "alpha beta timing",
                "alpha beta module details procedure quantities timing source"
              ],
              "topK": 4,
              "mode": "balanced"
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        var hits = result.GetProperty("hits").EnumerateArray().ToArray();
        Assert.Equal(10, hits.Length);
        Assert.Contains(hits, static hit => string.Equals(
            "Knowledge/target.pdf",
            hit.GetProperty("docPath").GetString(),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_overwrites_empty_backend_retrieval_route_with_fanout_origin()
    {
        var handler = new StubHttpHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "items": [
                        {
                          "score": 0.91,
                          "docPath": "Cuisine/breakfast.pdf",
                          "docName": "breakfast.pdf",
                          "pageStart": 4,
                          "pageEnd": 4,
                          "chunkId": "breakfast-4",
                          "text": "SCONES AUX CANNEBERGES. Ingredients : farine, canneberges, lait et beurre. Preparation : former les scones puis cuire au four.",
                          "retrievalQuery": "",
                          "retrievalQueryIndex": 99,
                          "matchedContentCards": [
                            { "title": "SCONES AUX CANNEBERGES", "kind": "section", "pageStart": 4 }
                          ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": [ "petit-dejeuner recettes" ],
              "topK": 4,
              "mode": "balanced"
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal("petit-dejeuner recettes", hit.GetProperty("retrievalQuery").GetString());
        Assert.Equal(0, hit.GetProperty("retrievalQueryIndex").GetInt32());
        Assert.Equal(0, hit.GetProperty("retrievalHitRank").GetInt32());
    }

    [Fact]
    public async Task DocumentsCatalogAsync_falls_back_to_catalog_alias_when_unified_route_is_missing()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "items": [
                            {
                              "docId": "11111111-1111-1111-1111-111111111111",
                              "docPath": "ATEX/test.pdf",
                              "docName": "test.pdf",
                              "category": "ATEX",
                              "status": "indexed",
                              "pageCount": 2
                            }
                          ],
                          "limit": 10,
                          "offset": 0
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsCatalogAsync("ATEX", "test", 10, 0, CancellationToken.None);

        var item = Assert.Single(response.Items);
        Assert.Equal("ATEX/test.pdf", item.DocPath);
        Assert.Equal("test.pdf", item.DocName);
        Assert.Equal("ATEX", item.Category);
        Assert.Contains("/documents?limit=10&offset=0&category=ATEX&q=test", requestedPaths);
        Assert.Contains("/documents/catalog?limit=10&offset=0&category=ATEX&q=test", requestedPaths);
    }

    [Fact]
    public async Task DocumentsGetAsync_falls_back_to_catalog_alias_when_unified_detail_route_is_missing()
    {
        const string docId = "11111111-1111-1111-1111-111111111111";
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents/" + docId => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog/" + docId => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "docId": "11111111-1111-1111-1111-111111111111",
                          "docPath": "ATEX/test.pdf",
                          "categoryPath": "ATEX"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsGetAsync(docId, CancellationToken.None);

        Assert.Equal("ATEX/test.pdf", response.GetProperty("docPath").GetString());
        Assert.Equal("ATEX", response.GetProperty("categoryPath").GetString());
        Assert.Contains("/documents/" + docId, requestedPaths);
        Assert.Contains("/documents/catalog/" + docId, requestedPaths);
    }

    [Fact]
    public async Task DocumentsCatalogIsIndexedAsync_uses_catalog_alias_when_unified_detail_is_not_available()
    {
        const string docId = "11111111-1111-1111-1111-111111111111";
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents/" + docId => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog/" + docId => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"docId\":\"11111111-1111-1111-1111-111111111111\"}", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var indexed = await sut.DocumentsCatalogIsIndexedAsync(docId, CancellationToken.None);

        Assert.True(indexed);
        Assert.Contains("/documents/" + docId, requestedPaths);
        Assert.Contains("/documents/catalog/" + docId, requestedPaths);
    }

    [Fact]
    public async Task DocumentsListAsync_legacy_fallback_filters_non_indexed_documents()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/catalog/documents")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (req.RequestUri!.AbsolutePath == "/documents")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "items": [
                            {
                              "docId": "11111111-1111-1111-1111-111111111111",
                              "docPath": "Cuisine/indexed.pdf",
                              "docName": "indexed.pdf",
                              "category": "Cuisine",
                              "categoryRef": "cat_cuisine",
                              "status": "indexed"
                            },
                            {
                              "docId": "22222222-2222-2222-2222-222222222222",
                              "docPath": "ATEX/deleted.pdf",
                              "docName": "deleted.pdf",
                              "category": "ATEX",
                              "status": "deleted"
                            },
                            {
                              "docId": "33333333-3333-3333-3333-333333333333",
                              "docPath": "General/missing.pdf",
                              "docName": "missing.pdf",
                              "category": "General",
                              "status": "missing"
                            },
                            {
                              "docId": "44444444-4444-4444-4444-444444444444",
                              "docPath": "Cuisine/pending.pdf",
                              "docName": "pending.pdf",
                              "category": "Cuisine",
                              "status": "pending"
                            },
                            {
                              "docId": "55555555-5555-5555-5555-555555555555",
                              "docPath": "Legacy/statusless.pdf",
                              "docName": "statusless.pdf",
                              "category": "Legacy",
                              "categoryRef": "cat_legacy"
                            }
                          ],
                          "limit": 10,
                          "offset": 0
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsListAsync(null, null, null, 10, 0, CancellationToken.None);
        var items = response.GetProperty("items").EnumerateArray().ToList();

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("Cuisine/indexed.pdf", item.GetProperty("docPath").GetString());
                Assert.Equal("cat_cuisine", item.GetProperty("categoryRef").GetString());
            },
            item =>
            {
                Assert.Equal("Legacy/statusless.pdf", item.GetProperty("docPath").GetString());
                Assert.Equal("cat_legacy", item.GetProperty("categoryRef").GetString());
            });
        Assert.DoesNotContain("ATEX/deleted.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("General/missing.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cuisine/pending.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DocumentsListAsync_catalog_response_filters_non_indexed_documents_defensively()
    {
        var handler = new StubHttpHandler(req =>
        {
            Assert.Equal("/catalog/documents", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "value": [
                        {
                          "docId": "11111111-1111-1111-1111-111111111111",
                          "docPath": "Cuisine/indexed.pdf",
                          "canonicalName": "indexed.pdf",
                          "categoryRef": "cat_cuisine",
                          "categoryPath": "Cuisine",
                          "sourceHash": "hash-catalog",
                          "docLanguage": "fr",
                          "profileLanguage": "fr",
                          "status": "indexed"
                        },
                        {
                          "docId": "22222222-2222-2222-2222-222222222222",
                          "docPath": "ATEX/deleted.pdf",
                          "canonicalName": "deleted.pdf",
                          "categoryPath": "ATEX",
                          "status": "deleted"
                        },
                        {
                          "docId": "33333333-3333-3333-3333-333333333333",
                          "docPath": "General/missing.pdf",
                          "canonicalName": "missing.pdf",
                          "categoryPath": "General",
                          "status": "missing"
                        },
                        {
                          "docId": "44444444-4444-4444-4444-444444444444",
                          "docPath": "Cuisine/pending.pdf",
                          "canonicalName": "pending.pdf",
                          "categoryPath": "Cuisine",
                          "status": "pending"
                        }
                      ],
                      "nextLink": null
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsListAsync(null, null, null, 10, 0, CancellationToken.None);
        var item = Assert.Single(response.GetProperty("items").EnumerateArray());

        Assert.Equal("Cuisine/indexed.pdf", item.GetProperty("docPath").GetString());
        Assert.Equal("cat_cuisine", item.GetProperty("categoryRef").GetString());
        var parsedItem = Assert.Single(sut.ParseDocumentItems(response));
        Assert.Equal("cat_cuisine", parsedItem.CategoryRef);
        Assert.Equal("hash-catalog", parsedItem.SourceHash);
        Assert.Equal("fr", parsedItem.DocLanguage);
        Assert.Equal("fr", parsedItem.ProfileLanguage);
        Assert.DoesNotContain("ATEX/deleted.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("General/missing.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cuisine/pending.pdf", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RagSearchToolAsync_serializes_nested_category_path_without_legacy_category()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync("installation", 8, "Programmation/Mettler", "balanced", CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Programmation/Mettler", body.RootElement.GetProperty("categoryPath").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryRef").ValueKind);
    }

    [Fact]
    public async Task RagSearchToolAsync_serializes_root_category_path_without_legacy_category()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync("properties", 8, "Documentation technique", "balanced", CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Documentation technique", body.RootElement.GetProperty("categoryPath").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryRef").ValueKind);
    }

    [Fact]
    public async Task RagSearchToolAsync_serializes_category_ref_without_legacy_category()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync("inerting", 8, "cat_003", "balanced", CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryPath").ValueKind);
        Assert.Equal("cat_003", body.RootElement.GetProperty("categoryRef").GetString());
    }

    [Theory]
    [InlineData("cat_cuisine")]
    [InlineData("cat-legacy")]
    public async Task RagSearchToolAsync_serializes_non_ordinal_category_ref_without_legacy_category(string categoryRef)
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync("inerting", 8, categoryRef, "balanced", CancellationToken.None);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryPath").ValueKind);
        Assert.Equal(categoryRef, body.RootElement.GetProperty("categoryRef").GetString());
    }

    [Fact]
    public async Task RagSearchToolAsync_serializes_doc_scope_and_per_source_limits()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync(
            "weekly inspection",
            8,
            "Operations",
            "balanced",
            CancellationToken.None,
            docId: "doc-ops-1",
            docPath: "Operations/Weekly guide.pdf",
            maxPerDoc: 8,
            maxPerPage: 2,
            pageStart: 12,
            pageEnd: 14);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Operations", body.RootElement.GetProperty("categoryPath").GetString());
        Assert.Equal("doc-ops-1", body.RootElement.GetProperty("docId").GetString());
        Assert.Equal("Operations/Weekly guide.pdf", body.RootElement.GetProperty("docPath").GetString());
        Assert.Equal(8, body.RootElement.GetProperty("maxPerDoc").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("maxPerPage").GetInt32());
        Assert.Equal(12, body.RootElement.GetProperty("pageStart").GetInt32());
        Assert.Equal(14, body.RootElement.GetProperty("pageEnd").GetInt32());
        Assert.True(body.RootElement.GetProperty("includeContextualSnippet").GetBoolean());
    }

    [Fact]
    public async Task RagSearchToolAsync_serializes_research_surface_flags()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            };
        });

        var sut = CreateApiClient(handler);
        await sut.RagSearchToolAsync(
            "weekly meal planning",
            12,
            "Cuisine",
            "broad",
            CancellationToken.None,
            researchMode: "source_exploration",
            includeResearchSurfaces: true);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("broad", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("source_exploration", body.RootElement.GetProperty("researchMode").GetString());
        Assert.True(body.RootElement.GetProperty("includeResearchSurfaces").GetBoolean());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_passes_doc_scope_to_each_backend_query()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedBodies.Add(body);
            using var parsed = JsonDocument.Parse(body);
            var query = parsed.RootElement.GetProperty("query").GetString() ?? "query";
            var page = capturedBodies.Count;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                score = 0.9,
                                docPath = "Operations/Weekly guide.pdf",
                                docName = "Weekly guide.pdf",
                                pageStart = page,
                                pageEnd = page,
                                chunkId = $"chunk-{page}",
                                text = $"{query} sourced content"
                            }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse(
            """
            {
              "queries": ["Morning control checklist", "Evening exception review"],
              "topK": 4,
              "categoryPath": "Operations",
              "docPath": "Operations/Weekly guide.pdf",
              "pageStart": 12,
              "pageEnd": 14,
              "maxPerDoc": 7,
              "maxPerPage": 2,
              "mode": "broad",
              "researchMode": "source_exploration",
              "includeResearchSurfaces": true
            }
            """);

        var result = await InvokePrivateToolAsync(sut, "ExecRagMultiSearchAsync", args.RootElement);

        Assert.Equal(2, capturedBodies.Count);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal("Operations", body.RootElement.GetProperty("categoryPath").GetString());
            Assert.Equal("Operations/Weekly guide.pdf", body.RootElement.GetProperty("docPath").GetString());
            Assert.Equal(12, body.RootElement.GetProperty("pageStart").GetInt32());
            Assert.Equal(14, body.RootElement.GetProperty("pageEnd").GetInt32());
            Assert.Equal(7, body.RootElement.GetProperty("maxPerDoc").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("maxPerPage").GetInt32());
            Assert.Equal("source_exploration", body.RootElement.GetProperty("researchMode").GetString());
            Assert.True(body.RootElement.GetProperty("includeResearchSurfaces").GetBoolean());
        }

        Assert.Equal("Operations/Weekly guide.pdf", result.GetProperty("meta").GetProperty("docPath").GetString());
        Assert.Equal(12, result.GetProperty("meta").GetProperty("pageStart").GetInt32());
        Assert.Equal(14, result.GetProperty("meta").GetProperty("pageEnd").GetInt32());
        Assert.Equal(7, result.GetProperty("meta").GetProperty("maxPerDoc").GetInt32());
        Assert.Equal(2, result.GetProperty("meta").GetProperty("maxPerPage").GetInt32());
        Assert.Equal("source_exploration", result.GetProperty("meta").GetProperty("researchMode").GetString());
        Assert.True(result.GetProperty("meta").GetProperty("includeResearchSurfaces").GetBoolean());
    }

    [Fact]
    public async Task ToolAgent_rag_search_preserves_nested_category_path_until_backend_call()
    {
        string? capturedBody = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "items": [
                        {
                          "score": 0.9,
                          "docPath": "Programmation/Mettler/file.pdf",
                          "docName": "file.pdf",
                          "pageStart": 1,
                          "pageEnd": 1,
                          "text": "sample",
                          "categoryPath": "Programmation/Mettler"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var api = CreateApiClient(handler);
        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api, llm: null!, mem);
        using var args = JsonDocument.Parse("""{"query":"installation","topK":8,"categoryPath":"Programmation/Mettler","mode":"balanced"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        _ = await task;

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Programmation/Mettler", body.RootElement.GetProperty("categoryPath").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_preserves_category_ref_guidance_and_metrics()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            capturedBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var callNumber = capturedBodies.Count;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    callNumber == 1
                    ? """
                    {
                      "metrics": { "tookMs": 33, "returned": 1, "degradedRetrievers": ["document_profile_v1"] },
                      "guidance": {
                        "behavior": "answer_with_caveat",
                        "reason": "partial_evidence",
                        "qualificationNote": "La source est partielle."
                      },
                      "items": [
                        {
                          "score": 0.99,
                          "docPath": "Guidance/file.pdf",
                          "docName": "file.pdf",
                          "pageStart": 1,
                          "pageEnd": 1,
                          "chunkId": "chunk-1",
                          "text": "sample"
                        }
                      ]
                    }
                    """
                    : """
                    {
                      "metrics": { "tookMs": 33, "returned": 1, "degradedRetrievers": ["sparse_bm25"] },
                      "guidance": {
                        "behavior": "answer_with_caveat",
                        "reason": "partial_evidence",
                        "qualificationNote": "La source est partielle."
                      },
                      "items": [
                        {
                          "score": 0.7,
                          "docPath": "Guidance/file.pdf",
                          "docName": "file.pdf",
                          "pageStart": 1,
                          "pageEnd": 1,
                          "chunkId": "chunk-1",
                          "text": "sample enriched",
                          "sourceHash": "hash-rich",
                          "docLanguage": "en",
                          "profileLanguage": "fr",
                          "categoryRef": "cat_legacy",
                          "categoryPath": "Guidance",
                          "extractionQuality": {
                            "documentQualityStatus": "extraction_ok",
                            "pageQualityStatus": "page_ok",
                            "diagnosticSummary": {
                              "nativeTextStatus": "ok",
                              "pageCount": 2
                            }
                          },
                          "matchedContentCards": [
                            { "title": "Rich source card", "kind": "section", "pageStart": 1 }
                          ],
                          "selectionHints": {
                            "evidenceRole": "supporting_context",
                            "supportScore": 8
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var api = CreateApiClient(handler);
        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api, llm: null!, mem);
        using var args = JsonDocument.Parse("""{"queries":["pressure valve","valve limits"],"topK":4,"categoryRef":"cat_legacy","mode":"balanced"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagMultiSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        Assert.Equal(2, capturedBodies.Count);
        foreach (var capturedBody in capturedBodies)
        {
            using var body = JsonDocument.Parse(capturedBody);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("category").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("categoryPath").ValueKind);
            Assert.Equal("cat_legacy", body.RootElement.GetProperty("categoryRef").GetString());
        }

        Assert.Equal("answer_with_caveat", result.GetProperty("guidance").GetProperty("behavior").GetString());
        var queryRuns = result.GetProperty("meta").GetProperty("queryRuns").EnumerateArray().ToList();
        Assert.Equal(2, queryRuns.Count);
        Assert.Equal(33, queryRuns[0].GetProperty("meta").GetProperty("metrics").GetProperty("tookMs").GetInt32());
        Assert.Equal(1, queryRuns[0].GetProperty("hitCount").GetInt32());
        Assert.True(queryRuns[0].GetProperty("clientElapsedMs").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, queryRuns[0].GetProperty("busy").ValueKind);
        Assert.Equal(["document_profile_v1"], queryRuns[0].GetProperty("degradedRetrievers").EnumerateArray().Select(static item => item.GetString() ?? "").ToArray());
        var degradedRetrievers = result.GetProperty("meta").GetProperty("degradedRetrievers").EnumerateArray().Select(static item => item.GetString() ?? "").ToList();
        Assert.Equal(["document_profile_v1", "sparse_bm25"], degradedRetrievers);
        Assert.Equal(["document_profile_v1", "sparse_bm25"], mem.LastRagDegradedRetrievers);
        var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal("hash-rich", hit.GetProperty("sourceHash").GetString());
        Assert.Equal("en", hit.GetProperty("docLanguage").GetString());
        Assert.Equal("fr", hit.GetProperty("profileLanguage").GetString());
        Assert.Equal("Rich source card", hit.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal("supporting_context", hit.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal("ok", hit.GetProperty("extractionQuality").GetProperty("diagnosticSummary").GetProperty("nativeTextStatus").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_keeps_distinct_content_cards_on_same_page()
    {
        var handler = new StubHttpHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    query.Contains("first", StringComparison.OrdinalIgnoreCase)
                    ? """
                    {
                      "items": [
                        {
                          "score": 0.92,
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "text": "First same-page item.",
                          "matchedContentCards": [
                            { "title": "First card", "contentCardId": "card-first", "kind": "section" }
                          ]
                        }
                      ]
                    }
                    """
                    : """
                    {
                      "items": [
                        {
                          "score": 0.91,
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "text": "Second same-page item.",
                          "matchedContentCards": [
                            { "title": "Second card", "contentCardId": "card-second", "kind": "section" }
                          ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var api = CreateApiClient(handler);
        var sut = new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["first","second"],"topK":4,"mode":"balanced"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagMultiSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        var hits = result.GetProperty("hits").EnumerateArray().ToArray();
        Assert.Equal(2, hits.Length);
        var cardIds = hits
            .Select(hit => hit.GetProperty("matchedContentCards")[0].GetProperty("contentCardId").GetString())
            .ToArray();
        Assert.Contains("card-first", cardIds);
        Assert.Contains("card-second", cardIds);
    }

    [Fact]
    public async Task ToolAgent_rag_multi_search_keeps_distinct_content_cards_without_ids_on_same_page()
    {
        var handler = new StubHttpHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var query = body.RootElement.GetProperty("query").GetString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    query.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    ? """
                    {
                      "items": [
                        {
                          "score": 0.92,
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "text": "Alpha same-page item.",
                          "matchedContentCards": [
                            { "title": "Alpha card", "kind": "section", "pageStart": 3 }
                          ]
                        }
                      ]
                    }
                    """
                    : """
                    {
                      "items": [
                        {
                          "score": 0.91,
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "text": "Beta same-page item.",
                          "matchedContentCards": [
                            { "title": "Beta card", "kind": "section", "pageStart": 3 }
                          ]
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var api = CreateApiClient(handler);
        var sut = new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());
        using var args = JsonDocument.Parse("""{"queries":["alpha","beta"],"topK":4,"mode":"balanced"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagMultiSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        var titles = result.GetProperty("hits")
            .EnumerateArray()
            .Select(hit => hit.GetProperty("matchedContentCards")[0].GetProperty("title").GetString())
            .ToArray();
        Assert.Equal(2, titles.Length);
        Assert.Contains("Alpha card", titles);
        Assert.Contains("Beta card", titles);
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_executes_with_debug_scroll_and_preserves_metadata()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "requestedRef": "doc-1",
                          "source": {
                            "docId": "doc-1",
                            "docPath": "Knowledge/manual.pdf",
                            "docName": "manual.pdf",
                            "pageStart": 1,
                            "pageEnd": 4,
                            "label": "manual.pdf",
                            "sourceHash": "src-1",
                            "docLanguage": "de",
                            "profileLanguage": "de",
                            "categoryRef": "cat_042",
                            "categoryPath": "Knowledge/Procedures",
                            "extractionQuality": {
                              "extractionSource": "pdf_text_plus_image_ocr",
                              "documentQualityStatus": "ocr_applied_ok",
                              "pageQualityStatus": "page_ok_with_images",
                              "textStatus": "ok",
                              "pageExtractionConfidence": 0.86,
                              "ocrAttempted": true,
                              "ocrApplied": true,
                              "signals": [ "page_contains_images" ],
                              "diagnosticSummary": {
                                "nativeTextStatus": "low_text",
                                "nativeOcrRecommended": true,
                                "ocrMode": "image_page",
                                "ocrLanguages": "deu+eng",
                                "ocrDurationMs": 1200,
                                "ocrAppliedReason": "image_ocr_merged_native_text",
                                "ocrAttemptedPageCount": 4,
                                "ocrPagesWithNovelTextCount": 2,
                                "imagePageCount": 3,
                                "pageWarningCount": 1
                              }
                            },
                            "matchedContentCards": [
                              {
                                "title": "Control before validation",
                                "contentCardId": "card-source-resolve",
                                "pageStart": 2,
                                "pageEnd": 2,
                                "kind": "procedure",
                                "evidence": {
                                  "schemaVersion": "source_resolve_v1",
                                  "scaleBasis": { "count": 2, "label": "validation set" },
                                  "quantityFacts": [
                                    { "value": 4, "unit": "checks", "label": "Control checks" }
                                  ],
                                  "confidence": 0.8
                                }
                              }
                            ],
                            "selectionHints": {
                              "evidenceRole": "actionable_item",
                              "actionabilityScore": 91,
                              "supportScore": 42,
                              "fragmentScore": 4,
                              "navigationScore": 0,
                              "qualityPenalty": 2
                            }
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "result": {
                            "points": [
                              {
                                "payload": {
                                  "text": "Dieses Handbuch beschreibt die Validierungsschritte, die Kontrollpruefung und die dokumentierten Grenzen fuer die Bediener.",
                                  "doc_path": "Knowledge/manual.pdf",
                                  "doc_name": "manual.pdf",
                                  "page_start": 2,
                                  "page_end": 2,
                                  "chunk_index": 0,
                                  "source_hash": "src-debug",
                                  "doc_language": "de",
                                  "profile_language": "de",
                                  "category_ref": "cat_debug",
                                  "category_path": "Knowledge/Debug",
                                  "chunk_id": "chunk-debug",
                                  "extraction_quality": {
                                    "extraction_source": "pdf_text",
                                    "document_quality_status": "extraction_ok",
                                    "page_quality_status": "page_ok",
                                    "page_extraction_confidence": 0.97,
                                    "ocr_attempted": true,
                                    "ocr_applied": false,
                                    "signals": [ "debug_signal" ],
                                    "diagnostic_summary": {
                                      "native_text_status": "ok",
                                      "native_ocr_recommended": false,
                                      "ocr_mode": "native_text",
                                      "ocr_languages": "deu",
                                      "ocr_duration_ms": 340,
                                      "ocr_failure_reason": "no_novel_text",
                                      "ocr_timed_out": false,
                                      "ocr_attempted_page_count": 2,
                                      "ocr_skipped_page_count": 1,
                                      "ocr_pages_with_novel_text_count": 0,
                                      "page_count": 4,
                                      "text_page_count": 4,
                                      "image_page_count": 1,
                                      "page_review_recommended_count": 0
                                    }
                                  },
                                  "matched_content_cards": [
                                    {
                                      "title": "Debug card",
                                      "content_card_id": "card-debug",
                                      "page_start": 2,
                                      "page_end": 2,
                                      "kind": "section",
                                      "signals": [ "structured_item" ],
                                      "evidence": {
                                        "schemaVersion": "debug_card_v1",
                                        "scaleBasis": { "count": 3, "label": "debug set" },
                                        "quantityFacts": [
                                          { "value": 9, "unit": "steps", "label": "Debug steps" }
                                        ],
                                        "confidence": 0.92
                                      }
                                    }
                                  ],
                                  "selection_hints": {
                                    "evidence_role": "supporting_context",
                                    "support_score": 97
                                  },
                                  "content_signals": {
                                    "content_role": "mixed_navigation_content",
                                    "navigation_reason": "inline_page_number_list",
                                    "navigation_score": 0.42,
                                    "content_density_score": 0.76
                                  }
                                }
                              }
                            ]
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient(
            """
            {"summaryText":"Ce document resume les etapes de validation, les controles a effectuer et les limites documentees pour les operateurs responsables."}
            """);
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/manual.pdf",
            DocName = "manual.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge/Procedures",
            PdfRef = "PDF01"
        };
        mem.LastLanguage = "fr";

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"short","maxWords":80,"maxChunks":4}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagSummarizeLiveAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        Assert.Contains("/sources/resolve", requestedPaths);
        Assert.Contains(requestedPaths, path => path.StartsWith("/rag/debug/scroll?", StringComparison.Ordinal));
        Assert.Single(llm.Requests);
        var userPrompt = llm.Requests[0].Single(message => message.role == "user").content;
        Assert.Contains("TargetLanguage: fr", userPrompt);
        Assert.Contains("Dieses Handbuch", userPrompt);
        Assert.Contains("card-debug", userPrompt);
        Assert.Contains("debug_card_v1", userPrompt);
        Assert.Equal("fr", result.GetProperty("responseLanguage").GetString());
        Assert.Equal("de", result.GetProperty("docLanguage").GetString());
        Assert.Equal("de", result.GetProperty("profileLanguage").GetString());
        Assert.Equal("src-debug", result.GetProperty("sourceHash").GetString());
        Assert.Equal("cat_debug", result.GetProperty("categoryRef").GetString());
        Assert.Equal("extraction_ok", result.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.True(result.GetProperty("extractionQuality").GetProperty("ocrAttempted").GetBoolean());
        var diagnostics = result.GetProperty("extractionQuality").GetProperty("diagnosticSummary");
        Assert.Equal("ok", diagnostics.GetProperty("nativeTextStatus").GetString());
        Assert.Equal("no_novel_text", diagnostics.GetProperty("ocrFailureReason").GetString());
        Assert.Equal(2, diagnostics.GetProperty("ocrAttemptedPageCount").GetInt32());
        Assert.Equal("supporting_context", result.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal(97, result.GetProperty("selectionHints").GetProperty("supportScore").GetInt32());
        Assert.Equal("mixed_navigation_content", result.GetProperty("contentSignals").GetProperty("contentRole").GetString());
        Assert.Equal("inline_page_number_list", result.GetProperty("contentSignals").GetProperty("navigationReason").GetString());
        Assert.Equal(0.42, result.GetProperty("contentSignals").GetProperty("navigationScore").GetDouble());
        Assert.Equal(0.76, result.GetProperty("contentSignals").GetProperty("contentDensityScore").GetDouble());
        var resultCard = result.GetProperty("matchedContentCards")[0];
        Assert.Equal("card-debug", resultCard.GetProperty("contentCardId").GetString());
        Assert.Equal("debug_card_v1", resultCard.GetProperty("evidence").GetProperty("schemaVersion").GetString());
        var anchor = result.GetProperty("anchors")[0];
        Assert.Equal("src-debug", anchor.GetProperty("sourceHash").GetString());
        Assert.Equal("de", anchor.GetProperty("docLanguage").GetString());
        Assert.True(anchor.GetProperty("extractionQuality").GetProperty("ocrAttempted").GetBoolean());
        Assert.Equal(1, anchor.GetProperty("extractionQuality").GetProperty("diagnosticSummary").GetProperty("imagePageCount").GetInt32());
        var anchorCard = anchor.GetProperty("matchedContentCards")[0];
        Assert.Equal("Debug card", anchorCard.GetProperty("title").GetString());
        Assert.Equal("card-debug", anchorCard.GetProperty("contentCardId").GetString());
        Assert.Equal("debug_card_v1", anchorCard.GetProperty("evidence").GetProperty("schemaVersion").GetString());
        Assert.Equal("supporting_context", anchor.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal("mixed_navigation_content", anchor.GetProperty("contentSignals").GetProperty("contentRole").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_enriches_partial_source_resolve_from_memory()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "requestedRef": "doc-1",
                      "source": {
                        "docId": "doc-1",
                        "docPath": "Knowledge/rich.pdf",
                        "docName": "rich.pdf",
                        "pageStart": 1,
                        "pageEnd": 12,
                        "label": "rich.pdf",
                        "profileSignals": {
                          "language": "de"
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "result": {
                        "points": [
                          {
                            "payload": {
                              "text": "Dieses Dokument beschreibt Pruefschritte, Verantwortlichkeiten und konkrete Kontrollpunkte fuer den Betrieb.",
                              "doc_path": "Knowledge/rich.pdf",
                              "doc_name": "rich.pdf",
                              "page_start": 3,
                              "page_end": 3,
                              "chunk_index": 1
                            }
                          }
                        ]
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient("""{"summaryText":"Ce document presente les controles operationnels et les responsabilites associees."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/rich.pdf",
            DocName = "rich.pdf",
            Category = "Knowledge",
            CategoryRef = "cat_rich",
            CategoryPath = "Knowledge/Procedures",
            PdfRef = "PDF01",
            SourceHash = "hash-memory",
            DocLanguage = "de",
            ProfileLanguage = "de"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef
        {
            DocId = "doc-1",
            DocPath = "Knowledge/rich.pdf",
            DocName = "rich.pdf",
            PageStart = 3,
            PageEnd = 3,
            Label = "rich.pdf (p.3)",
            SourceHash = "hash-memory",
            DocLanguage = "de",
            ProfileLanguage = "de",
            Category = "Knowledge",
            CategoryRef = "cat_rich",
            CategoryPath = "Knowledge/Procedures",
            ChunkId = "chunk-memory",
            DocumentQualityStatus = "extraction_ok",
            MatchedContentCards =
            [
                new ToolMemory.SourceContentCardRef
                {
                    Title = "Operational controls",
                    ContentCardId = "card-memory",
                    PageStart = 3,
                    PageEnd = 3,
                    Kind = "section"
                }
            ],
            SelectionHintEvidenceRole = "supporting_context",
            SelectionHintSupportScore = 88
        });
        mem.LastSourcesUsed[0].ProfileSignals = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = "llm_backoffice_v1",
            Language = "de",
            Keywords = new() { "operational profile keyword" },
            Topics = new() { "memory profile routing" },
            Limits = new() { "Use exact chunks for numeric values." }
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"short","maxWords":80,"maxChunks":2}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.Single(llm.Requests);
        var userPrompt = llm.Requests[0].Single(message => message.role == "user").content;
        Assert.Contains("card-memory", userPrompt);
        Assert.Contains("Knowledge/Procedures", userPrompt);
        Assert.Contains("operational profile keyword", userPrompt);
        Assert.Equal("fr", result.GetProperty("responseLanguage").GetString());
        Assert.Equal("de", result.GetProperty("docLanguage").GetString());
        Assert.Equal("de", result.GetProperty("profileLanguage").GetString());
        Assert.Equal("hash-memory", result.GetProperty("sourceHash").GetString());
        Assert.Equal("Knowledge", result.GetProperty("category").GetString());
        Assert.Equal("cat_rich", result.GetProperty("categoryRef").GetString());
        Assert.Equal("Knowledge/Procedures", result.GetProperty("categoryPath").GetString());
        Assert.Equal("extraction_ok", result.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal("Operational controls", result.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal("card-memory", result.GetProperty("matchedContentCards")[0].GetProperty("contentCardId").GetString());
        Assert.Equal("operational profile keyword", result.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
        Assert.Equal("memory profile routing", result.GetProperty("profileSignals").GetProperty("topics")[0].GetString());
        Assert.Equal("Use exact chunks for numeric values.", result.GetProperty("profileSignals").GetProperty("limits")[0].GetString());
        Assert.Equal("supporting_context", result.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal(88, result.GetProperty("selectionHints").GetProperty("supportScore").GetInt32());
        var anchor = result.GetProperty("anchors")[0];
        Assert.Equal("hash-memory", anchor.GetProperty("sourceHash").GetString());
        Assert.Equal("Knowledge", anchor.GetProperty("category").GetString());
        Assert.Equal("cat_rich", anchor.GetProperty("categoryRef").GetString());
        Assert.Equal("card-memory", anchor.GetProperty("matchedContentCards")[0].GetProperty("contentCardId").GetString());
        Assert.Equal("operational profile keyword", anchor.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_uses_memory_profile_signals_when_source_resolve_fails()
    {
        string? capturedRagBody = null;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":{"points":[]}}""", Encoding.UTF8, "application/json")
            },
            "/rag/search" => CaptureRagSearch(req, body =>
            {
                capturedRagBody = body;
                return """
                {
                  "items": [
                    {
                      "score": 0.91,
                      "docId": "doc-memory",
                      "docPath": "Knowledge/memory.pdf",
                      "docName": "memory.pdf",
                      "pageStart": 4,
                      "pageEnd": 4,
                      "text": "The document describes validated checks and operating limits.",
                      "profileSignals": {
                        "profileVersion": "llm_backoffice_v1",
                        "language": "en",
                        "keywords": ["validated checks"],
                        "topics": ["operating limits"]
                      }
                    }
                  ]
                }
                """;
            }),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient("""{"summaryText":"Résumé source-backed."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-memory",
            DocPath = "Knowledge/memory.pdf",
            DocName = "memory.pdf",
            CategoryPath = "Knowledge/Memory",
            PdfRef = "PDF01"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef
        {
            DocId = "doc-memory",
            DocPath = "Knowledge/memory.pdf",
            DocName = "memory.pdf",
            CategoryPath = "Knowledge/Memory",
            DocLanguage = "en",
            ProfileLanguage = "en",
            ProfileSignals = new ToolMemory.SourceProfileSignalsRef
            {
                ProfileVersion = "llm_backoffice_v1",
                Language = "en",
                Keywords = new() { "fallback keyword" },
                Topics = new() { "fallback topic" },
                Limits = new() { "fallback limit" }
            }
        });

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"short","maxChunks":2}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.NotNull(capturedRagBody);
        using (var ragBody = JsonDocument.Parse(capturedRagBody!))
        {
            var query = ragBody.RootElement.GetProperty("query").GetString();
            Assert.Contains("fallback keyword", query);
            Assert.Contains("fallback topic", query);
            Assert.Contains("fallback limit", query);
        }

        Assert.Contains("fallback keyword", llm.Requests[0].Single(message => message.role == "user").content);
        Assert.Equal("fallback keyword", result.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_falls_back_to_rag_search_when_debug_scroll_is_empty()
    {
        string? capturedRagBody = null;
        var handler = new StubHttpHandler(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "source": {
                            "docId": "doc-1",
                            "docPath": "Knowledge/manual.pdf",
                            "docName": "manual.pdf",
                            "pageStart": 1,
                            "pageEnd": 4,
                            "sourceHash": "src-from-source",
                            "docLanguage": "it",
                            "profileLanguage": "it",
                            "categoryRef": "cat_042",
                            "categoryPath": "Knowledge/Procedures"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":{"points":[]}}""", Encoding.UTF8, "application/json")
                },
                "/rag/search" => CaptureRagSearch(req, body =>
                {
                    capturedRagBody = body;
                    return """
                    {
                      "items": [
                        {
                          "score": 0.91,
                          "docId": "doc-1",
                          "docPath": "Knowledge/manual.pdf",
                          "docName": "manual.pdf",
                          "pageStart": 3,
                          "pageEnd": 3,
                          "chunkIndex": 7,
                          "chunkId": "chunk-7",
                          "text": "Questo manuale descrive i controlli di qualita, le verifiche operative e le condizioni limite documentate.",
                          "sourceHash": "src-from-rag",
                          "docLanguage": "it",
                          "profileLanguage": "it",
                          "categoryRef": "cat_042",
                          "categoryPath": "Knowledge/Procedures",
                          "extractionQuality": {
                            "extractionSource": "pdf_text",
                            "documentQualityStatus": "extraction_ok",
                            "pageQualityStatus": "page_ok",
                            "textStatus": "ok",
                            "documentExtractionConfidence": 0.98,
                            "pageExtractionConfidence": 0.94,
                            "ocrAttempted": true,
                            "ocrApplied": false
                          },
                          "matchedContentCards": [
                            { "title": "Controlli qualita", "pageStart": 3, "pageEnd": 3, "kind": "section" }
                          ]
                        }
                      ]
                    }
                    """;
                }),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient(
            """
            {"summaryText":"Ce document presente les controles de qualite, les verifications operationnelles et les conditions limite documentees dans la procedure."}
            """);
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/manual.pdf",
            DocName = "manual.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge/Procedures",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"medium","maxChunks":4}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.NotNull(capturedRagBody);
        using (var ragBody = JsonDocument.Parse(capturedRagBody!))
        {
            Assert.Equal("doc-1", ragBody.RootElement.GetProperty("docId").GetString());
            Assert.Equal("Knowledge/manual.pdf", ragBody.RootElement.GetProperty("docPath").GetString());
            Assert.Equal("balanced", ragBody.RootElement.GetProperty("mode").GetString());
            Assert.Contains("riassunto", ragBody.RootElement.GetProperty("query").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Single(llm.Requests);
        var prompt = llm.Requests[0].Single(message => message.role == "user").content;
        Assert.Contains("TargetLanguage: fr", prompt);
        Assert.Contains("DocumentLanguage: it", prompt);
        Assert.Contains("IngestionMetadata:", prompt);
        Assert.Contains("- sourceHash: src-from-rag", prompt);
        Assert.Contains("- categoryRef: cat_042", prompt);
        Assert.Contains("- chunkId: chunk-7", prompt);
        Assert.Contains("- extractionSource: pdf_text", prompt);
        Assert.Contains("- documentExtractionConfidence: 0.98", prompt);
        Assert.Contains("- pageExtractionConfidence: 0.94", prompt);
        Assert.Contains("Controlli qualita", prompt);
        Assert.Contains("diagnosticFields: sourceHash/categoryRef/chunkId", prompt);
        Assert.Contains("Questo manuale", prompt);
        Assert.DoesNotContain("procedures/settings", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fr", result.GetProperty("responseLanguage").GetString());
        Assert.Equal("it", result.GetProperty("docLanguage").GetString());
        Assert.Equal("src-from-rag", result.GetProperty("sourceHash").GetString());
        Assert.Equal("extraction_ok", result.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.True(result.GetProperty("extractionQuality").GetProperty("ocrAttempted").GetBoolean());
        Assert.Equal("Controlli qualita", result.GetProperty("anchors")[0].GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_includes_metadata_from_all_selected_chunks_in_prompt()
    {
        var handler = new StubHttpHandler(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "source": {
                            "docId": "doc-multi",
                            "docPath": "Knowledge/source.pdf",
                            "docName": "source.pdf",
                            "pageStart": 1,
                            "pageEnd": 6,
                            "sourceHash": "src-resolved",
                            "docLanguage": "en",
                            "profileLanguage": "en",
                            "categoryRef": "cat_generic",
                            "categoryPath": "Knowledge/Generic"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/search" => CaptureRagSearch(req, body =>
                {
                    return """
                    {
                      "items": [
                        {
                          "score": 0.89,
                          "docId": "doc-multi",
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 1,
                          "pageEnd": 1,
                          "chunkIndex": 1,
                          "chunkId": "chunk-first",
                          "text": "The first selected passage gives the general frame and introductory constraints.",
                          "sourceHash": "src-first",
                          "docLanguage": "en",
                          "profileLanguage": "en",
                          "categoryRef": "cat_generic",
                          "categoryPath": "Knowledge/Generic"
                        },
                        {
                          "score": 0.86,
                          "docId": "doc-multi",
                          "docPath": "Knowledge/source.pdf",
                          "docName": "source.pdf",
                          "pageStart": 4,
                          "pageEnd": 4,
                          "chunkIndex": 4,
                          "chunkId": "chunk-second",
                          "text": "The later selected passage contains the concrete checks, numeric thresholds and review warnings.",
                          "sourceHash": "src-second",
                          "docLanguage": "en",
                          "profileLanguage": "en",
                          "categoryRef": "cat_generic",
                          "categoryPath": "Knowledge/Generic",
                          "extractionQuality": {
                            "extractionSource": "pdf_text_plus_image_ocr",
                            "documentQualityStatus": "extraction_ok",
                            "pageQualityStatus": "page_ok_with_images",
                            "pageExtractionConfidence": 0.83,
                            "pageManualReviewRecommended": true,
                            "ocrAttempted": true,
                            "ocrApplied": true,
                            "signals": [ "image_text_merged" ]
                          },
                          "matchedContentCards": [
                            {
                              "title": "Second metadata card",
                              "contentCardId": "card-second",
                              "pageStart": 4,
                              "pageEnd": 4,
                              "kind": "check",
                              "signals": [ "numeric_fact" ],
                              "evidence": {
                                "schemaVersion": "card_schema_v2",
                                "quantityFacts": [
                                  { "value": 12, "unit": "checks", "label": "Required checks" }
                                ],
                                "confidence": 0.91
                              }
                            }
                          ],
                          "selectionHints": {
                            "evidenceRole": "supporting_context",
                            "actionabilityScore": 72,
                            "supportScore": 88,
                            "fragmentScore": 0,
                            "navigationScore": 0,
                            "qualityPenalty": 1
                          }
                        }
                      ]
                    }
                    """;
                }),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler);
        var llm = new StubLlmClient("""{"summaryText":"This summary uses both selected passages and keeps the concrete checks from the later source."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-multi",
            DocPath = "Knowledge/source.pdf",
            DocName = "source.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge/Generic",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"en","level":"medium","maxChunks":4}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.Single(llm.Requests);
        var prompt = llm.Requests[0].Single(message => message.role == "user").content;
        Assert.Contains("- source[1]:", prompt);
        Assert.Contains("- source[2]:", prompt);
        Assert.Contains("sourceHash=src-second", prompt);
        Assert.Contains("chunkId=chunk-second", prompt);
        Assert.Contains("extractionSource=pdf_text_plus_image_ocr", prompt);
        Assert.Contains("manualReview=recommended", prompt);
        Assert.Contains("contentCards=Second metadata card", prompt);
        Assert.Contains("card_schema_v2", prompt);
        Assert.Contains("selectionScores=actionability:72,support:88", prompt);
        Assert.Equal("src-first", result.GetProperty("sourceHash").GetString());
        Assert.Equal("src-second", result.GetProperty("sourceMetadataSample")[1].GetProperty("sourceHash").GetString());
        Assert.Equal("Second metadata card", result.GetProperty("sourceMetadataSample")[1].GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_keeps_all_selected_source_metadata_without_same_page_dedup_loss()
    {
        var handler = new StubHttpHandler(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"source":{"docId":"doc-many","docPath":"Knowledge/many.pdf","docName":"many.pdf","docLanguage":"en","profileLanguage":"en","sourceHash":"src-resolved"}}""",
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/search" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            items = Enumerable.Range(1, 10).Select(i => new
                            {
                                score = 0.95 - (i * 0.01),
                                docId = "doc-many",
                                docPath = "Knowledge/many.pdf",
                                docName = "many.pdf",
                                pageStart = i <= 2 ? 2 : i,
                                pageEnd = i <= 2 ? 2 : i,
                                chunkIndex = i,
                                text = $"Generic selected chunk {i} with useful factual document content for the summary.",
                                sourceHash = i <= 2 ? "src-same-page" : $"src-{i}",
                                docLanguage = "en",
                                profileLanguage = "en",
                                categoryRef = "cat_generic",
                                categoryPath = "Knowledge/Generic",
                                extractionQuality = new
                                {
                                    extractionSource = "pdf_text",
                                    documentQualityStatus = "extraction_ok",
                                    pageQualityStatus = "page_ok",
                                    pageExtractionConfidence = 0.9
                                },
                                matchedContentCards = new[]
                                {
                                    new
                                    {
                                        title = i == 10 ? "Tenth metadata card" : $"Same-page metadata card {i}",
                                        pageStart = i <= 2 ? 2 : i,
                                        pageEnd = i <= 2 ? 2 : i,
                                        kind = "section"
                                    }
                                },
                                selectionHints = new
                                {
                                    evidenceRole = "supporting_context",
                                    actionabilityScore = i,
                                    supportScore = 100 - i,
                                    fragmentScore = i % 3,
                                    navigationScore = i % 2,
                                    qualityPenalty = i % 4
                                }
                            }).ToArray()
                        }),
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler);
        var llm = new StubLlmClient("""{"summaryText":"The summary keeps the selected source metadata available."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-many",
            DocPath = "Knowledge/many.pdf",
            DocName = "many.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge/Generic",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"en","level":"medium","maxChunks":10,"maxCharsPerBatch":20000}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        var prompt = Assert.Single(llm.Requests).Single(message => message.role == "user").content;
        Assert.Contains("- source[1]: page=p.2", prompt);
        Assert.Contains("- source[2]: page=p.2", prompt);
        Assert.Contains("Same-page metadata card 1", prompt);
        Assert.Contains("Same-page metadata card 2", prompt);
        Assert.Contains("selectionScores=actionability:10,support:90,fragment:1,navigation:0,qualityPenalty:2", prompt);
        Assert.Equal(10, result.GetProperty("sourceMetadataTotal").GetInt32());
        Assert.False(result.GetProperty("sourceMetadataTruncated").GetBoolean());
        Assert.Equal(10, result.GetProperty("sourceMetadata").GetArrayLength());
        Assert.Equal("src-10", result.GetProperty("sourceMetadata")[9].GetProperty("sourceHash").GetString());
        Assert.Equal("Tenth metadata card", result.GetProperty("sourceMetadata")[9].GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_falls_back_to_language_aware_rag_search_when_debug_scroll_is_forbidden()
    {
        string? capturedRagBody = null;
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "source": {
                            "docId": "doc-nl",
                            "docPath": "Knowledge/handleiding.pdf",
                            "docName": "handleiding.pdf",
                            "pageStart": 1,
                            "pageEnd": 6,
                            "sourceHash": "src-nl",
                            "docLanguage": "nl-BE",
                            "profileLanguage": "nl-BE",
                            "categoryRef": "cat_nl",
                            "categoryPath": "Knowledge/Onderhoud"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":"admin_required"}""", Encoding.UTF8, "application/json")
                },
                "/rag/search" => CaptureRagSearch(req, body =>
                {
                    capturedRagBody = body;
                    return """
                    {
                      "items": [
                        {
                          "score": 0.88,
                          "docId": "doc-nl",
                          "docPath": "Knowledge/handleiding.pdf",
                          "docName": "handleiding.pdf",
                          "pageStart": 2,
                          "pageEnd": 2,
                          "chunkIndex": 3,
                          "chunkId": "chunk-nl",
                          "text": "Deze handleiding beschrijft onderhoudscontroles, veiligheidslimieten en acties voor operators.",
                          "sourceHash": "src-nl",
                          "docLanguage": "nl-BE",
                          "profileLanguage": "nl-BE",
                          "categoryRef": "cat_nl",
                          "categoryPath": "Knowledge/Onderhoud"
                        }
                      ]
                    }
                    """;
                }),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient(
            """
            {"summaryText":"Ce document décrit des contrôles de maintenance, des limites de sécurité et des actions opérateur."}
            """);
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-nl",
            DocPath = "Knowledge/handleiding.pdf",
            DocName = "handleiding.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge/Onderhoud",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"medium","maxChunks":4}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());
        var secondResult = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.NotNull(capturedRagBody);
        using (var ragBody = JsonDocument.Parse(capturedRagBody!))
        {
            Assert.Equal("doc-nl", ragBody.RootElement.GetProperty("docId").GetString());
            Assert.Equal("Knowledge/handleiding.pdf", ragBody.RootElement.GetProperty("docPath").GetString());
            Assert.Equal("balanced", ragBody.RootElement.GetProperty("mode").GetString());
            var query = ragBody.RootElement.GetProperty("query").GetString() ?? string.Empty;
            Assert.Contains("samenvatting doel hoofdsecties", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("summary purpose main sections", query, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(2, llm.Requests.Count);
        var prompt = llm.Requests[0].Single(message => message.role == "user").content;
        Assert.Contains("DocumentLanguage: nl-be", prompt);
        Assert.Contains("Deze handleiding", prompt);
        Assert.Equal("nl-be", result.GetProperty("docLanguage").GetString());
        Assert.Equal("src-nl", result.GetProperty("sourceHash").GetString());
        Assert.Equal("nl-be", secondResult.GetProperty("docLanguage").GetString());
        Assert.Single(requestedPaths, path => path.StartsWith("/rag/debug/scroll?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_prefers_backend_doc_language_over_tool_argument_hint()
    {
        string? capturedRagBody = null;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "source": {
                        "docId": "doc-de",
                        "docPath": "Knowledge/handbuch.pdf",
                        "docName": "handbuch.pdf",
                        "pageStart": 1,
                        "pageEnd": 4,
                        "sourceHash": "src-de",
                        "docLanguage": "de",
                        "profileLanguage": "de"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            "/rag/search" => CaptureRagSearch(req, body =>
            {
                capturedRagBody = body;
                return """
                {
                  "items": [
                    {
                      "docId": "doc-de",
                      "docPath": "Knowledge/handbuch.pdf",
                      "docName": "handbuch.pdf",
                      "pageStart": 1,
                      "pageEnd": 1,
                      "chunkIndex": 1,
                      "text": "Dieses Handbuch beschreibt Pruefschritte, Grenzwerte und Betreiberhinweise.",
                      "sourceHash": "src-de",
                      "docLanguage": "de",
                      "profileLanguage": "de"
                    }
                  ]
                }
                """;
            }),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler);
        var llm = new StubLlmClient("""{"summaryText":"Ce document decrit des controles, des limites et des consignes operateur."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-de",
            DocPath = "Knowledge/handbuch.pdf",
            DocName = "handbuch.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","docLanguage":"fr","level":"medium","maxChunks":3}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.NotNull(capturedRagBody);
        using (var ragBody = JsonDocument.Parse(capturedRagBody!))
        {
            var query = ragBody.RootElement.GetProperty("query").GetString() ?? string.Empty;
            Assert.Contains("zusammenfassung zweck hauptabschnitte", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resume objectif sections principales", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("medium useful", query, StringComparison.OrdinalIgnoreCase);
        }

        var prompt = Assert.Single(llm.Requests).Single(message => message.role == "user").content;
        Assert.Contains("TargetLanguage: fr", prompt);
        Assert.Contains("DocumentLanguage: de", prompt);
        Assert.Equal("de", result.GetProperty("docLanguage").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_recomputes_doc_language_from_rag_hits_when_resolve_has_no_language()
    {
        string? capturedRagBody = null;
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"source":{"docId":"doc-it","docPath":"Knowledge/manuale.pdf","docName":"manuale.pdf","pageStart":1,"pageEnd":3}}""",
                    Encoding.UTF8,
                    "application/json")
            },
            "/rag/search" => CaptureRagSearch(req, body =>
            {
                capturedRagBody = body;
                return """
                {
                  "items": [
                    {
                      "docId": "doc-it",
                      "docPath": "Knowledge/manuale.pdf",
                      "docName": "manuale.pdf",
                      "pageStart": 2,
                      "pageEnd": 2,
                      "chunkIndex": 2,
                      "text": "Il manuale descrive controlli, limiti e indicazioni operative.",
                      "sourceHash": "src-it",
                      "docLanguage": "it",
                      "profileLanguage": "it"
                    }
                  ]
                }
                """;
            }),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler);
        var llm = new StubLlmClient("""{"summaryText":"Ce document decrit des controles, des limites et des consignes operationnelles."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-it",
            DocPath = "Knowledge/manuale.pdf",
            DocName = "manuale.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"medium","maxChunks":3}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.NotNull(capturedRagBody);
        using (var ragBody = JsonDocument.Parse(capturedRagBody!))
        {
            var query = ragBody.RootElement.GetProperty("query").GetString() ?? string.Empty;
            Assert.Contains("manuale.pdf", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("medium useful", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("summary purpose", query, StringComparison.OrdinalIgnoreCase);
        }

        var prompt = Assert.Single(llm.Requests).Single(message => message.role == "user").content;
        Assert.Contains("DocumentLanguage: it", prompt);
        Assert.Equal("it", result.GetProperty("docLanguage").GetString());
        Assert.Equal("it", result.GetProperty("profileLanguage").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_skips_debug_scroll_without_admin_key()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"source":{"docId":"doc-plain","docPath":"Knowledge/plain.pdf","docName":"plain.pdf","docLanguage":"en","profileLanguage":"en","sourceHash":"src-plain"}}""",
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/search" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "items": [
                            {
                              "docId": "doc-plain",
                              "docPath": "Knowledge/plain.pdf",
                              "docName": "plain.pdf",
                              "pageStart": 1,
                              "pageEnd": 1,
                              "chunkIndex": 1,
                              "text": "This document explains operator checks, acceptance limits and escalation notes.",
                              "sourceHash": "src-plain",
                              "docLanguage": "en",
                              "profileLanguage": "en"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var api = CreateApiClient(handler);
        var llm = new StubLlmClient("""{"summaryText":"Ce document explique les controles operateur, les limites d'acceptation et les notes d'escalade."}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-plain",
            DocPath = "Knowledge/plain.pdf",
            DocName = "plain.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","level":"short","maxChunks":2}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.DoesNotContain(requestedPaths, path => path.StartsWith("/rag/debug/scroll?", StringComparison.Ordinal));
        Assert.Contains(requestedPaths, path => path.Equals("/rag/search", StringComparison.Ordinal));
        Assert.Single(llm.Requests);
        Assert.Equal("src-plain", result.GetProperty("sourceHash").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_returns_no_chunks_without_calling_llm_when_retrieval_is_empty()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"source":{"docId":"doc-1","docPath":"Knowledge/empty.pdf","docName":"empty.pdf"}}""", Encoding.UTF8, "application/json")
            },
            "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":{"points":[]}}""", Encoding.UTF8, "application/json")
            },
            "/rag/search" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient("""{"summaryText":"should not be used"}""");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/empty.pdf",
            DocName = "empty.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr"}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.Equal("no_chunks_found", result.GetProperty("error").GetString());
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Admin_summary_submit_uses_backend_resolved_document_metadata_over_llm_args()
    {
        string? capturedSubmitBody = null;
        var handler = new StubHttpHandler(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "source": {
                            "docId": "doc-1",
                            "docPath": "Knowledge/manual.pdf",
                            "docName": "manual.pdf",
                            "sourceHash": "resolved-source-hash",
                            "docLanguage": "nl-BE",
                            "profileLanguage": "nl-BE"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                "/admin/summaries/submit" => CaptureSubmit(req, body =>
                {
                    capturedSubmitBody = body;
                    return """{"stored":true}""";
                }),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/manual.pdf",
            DocName = "manual.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };
        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: mem);
        using var args = JsonDocument.Parse(
            """
            {
              "docRef": "PDF01",
              "docLanguage": "fr",
              "sourceHash": "llm-invented-hash",
              "summaryText": "Resume stocke.",
              "executionLeaseToken": "lease-123"
            }
            """);

        await InvokePrivateToolAsync(sut, "ExecAdminSummarySubmitAsync", args.RootElement.Clone());

        Assert.NotNull(capturedSubmitBody);
        using var body = JsonDocument.Parse(capturedSubmitBody!);
        Assert.Equal("nl-be", body.RootElement.GetProperty("docLanguage").GetString());
        Assert.Equal("resolved-source-hash", body.RootElement.GetProperty("sourceHash").GetString());
        Assert.Equal("lease-123", body.RootElement.GetProperty("executionLeaseToken").GetString());
    }

    [Fact]
    public async Task ToolAgent_rag_summarize_live_falls_back_to_chunk_text_when_llm_json_is_invalid()
    {
        const string chunkText = "This document lists maintenance checks, inspection intervals, acceptance limits and escalation notes for operators.";
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"source":{"docId":"doc-1","docPath":"Knowledge/fallback.pdf","docName":"fallback.pdf","docLanguage":"en","profileLanguage":"en","sourceHash":"src-fallback"}}""", Encoding.UTF8, "application/json")
            },
            "/rag/debug/scroll" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "result": {
                        "points": [
                          {
                            "payload": {
                              "text": "{{chunkText}}",
                              "doc_path": "Knowledge/fallback.pdf",
                              "doc_name": "fallback.pdf",
                              "page_start": 5,
                              "page_end": 5,
                              "chunk_index": 2
                            }
                          }
                        ]
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var llm = new StubLlmClient("not valid json");
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/fallback.pdf",
            DocName = "fallback.pdf",
            Category = "Knowledge",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01"
        };

        var sut = new ToolAgentOrchestrator(api, llm, mem);
        using var args = JsonDocument.Parse("""{"docRef":"PDF01","responseLanguage":"fr","strategy":"summary","maxChunks":3}""");
        var result = await InvokePrivateToolAsync(sut, "ExecRagSummarizeLiveAsync", args.RootElement.Clone());

        Assert.Single(llm.Requests);
        Assert.Equal(chunkText, result.GetProperty("summaryText").GetString());
        Assert.Equal("src-fallback", result.GetProperty("sourceHash").GetString());
        Assert.Equal(5, result.GetProperty("anchors")[0].GetProperty("pageStart").GetInt32());
    }

    [Fact]
    public async Task ToolAgent_sources_resolve_enriches_partial_backend_source_from_memory()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "requestedRef": "PDF01",
                      "source": {
                        "docId": "doc-1",
                        "docPath": "Knowledge/rich.pdf",
                        "pageStart": 2,
                        "pageEnd": 2,
                        "label": "rich.pdf"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Knowledge/rich.pdf",
            DocName = "rich.pdf",
            Category = "Knowledge",
            CategoryRef = "cat_rich",
            CategoryPath = "Knowledge/Procedures",
            PdfRef = "PDF01",
            SourceHash = "hash-memory",
            DocLanguage = "en",
            ProfileLanguage = "fr"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef
        {
            DocId = "doc-1",
            DocPath = "Knowledge/rich.pdf",
            PageStart = 2,
            PageEnd = 2,
            SourceHash = "hash-memory",
            DocLanguage = "en",
            ProfileLanguage = "fr",
            CategoryRef = "cat_rich",
            CategoryPath = "Knowledge/Procedures",
            DocumentQualityStatus = "extraction_ok",
            OcrAttempted = true,
            ExtractionDiagnosticSummary = new ToolMemory.SourceExtractionDiagnosticRef
            {
                NativeTextStatus = "ok",
                OcrAttemptedPageCount = 1
            },
            MatchedContentCards =
            [
                new ToolMemory.SourceContentCardRef
                {
                    Title = "Relevant section",
                    PageStart = 2,
                    Kind = "section"
                }
            ],
            ProfileSignals = new ToolMemory.SourceProfileSignalsRef
            {
                ProfileVersion = "llm_backoffice_v1",
                Language = "fr",
                Keywords = new() { "rich metadata" },
                Topics = new() { "source resolve memory merge" },
                Limits = new() { "Use exact chunks for numeric values." }
            },
            SelectionHintEvidenceRole = "supporting_context",
            SelectionHintSupportScore = 91
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: mem);
        using var args = JsonDocument.Parse("""{"ref":"PDF01"}""");
        var result = await InvokePrivateToolAsync(sut, "ExecSourcesResolveV2Async", args.RootElement.Clone());
        var source = result.GetProperty("source");

        Assert.Equal("doc-1", source.GetProperty("docId").GetString());
        Assert.Equal("Knowledge/rich.pdf", source.GetProperty("docPath").GetString());
        Assert.Equal("rich.pdf", source.GetProperty("docName").GetString());
        Assert.Equal("hash-memory", source.GetProperty("sourceHash").GetString());
        Assert.Equal("en", source.GetProperty("docLanguage").GetString());
        Assert.Equal("fr", source.GetProperty("profileLanguage").GetString());
        Assert.Equal("Knowledge", source.GetProperty("category").GetString());
        Assert.Equal("cat_rich", source.GetProperty("categoryRef").GetString());
        Assert.Equal("Knowledge/Procedures", source.GetProperty("categoryPath").GetString());
        Assert.Equal("extraction_ok", source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.True(source.GetProperty("extractionQuality").GetProperty("ocrAttempted").GetBoolean());
        Assert.Equal("ok", source.GetProperty("extractionQuality").GetProperty("diagnosticSummary").GetProperty("nativeTextStatus").GetString());
        Assert.Equal("Relevant section", source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal("llm_backoffice_v1", source.GetProperty("profileSignals").GetProperty("profileVersion").GetString());
        Assert.Equal("fr", source.GetProperty("profileSignals").GetProperty("language").GetString());
        Assert.Equal("rich metadata", source.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
        Assert.Equal("source resolve memory merge", source.GetProperty("profileSignals").GetProperty("topics")[0].GetString());
        Assert.Equal("Use exact chunks for numeric values.", source.GetProperty("profileSignals").GetProperty("limits")[0].GetString());
        Assert.Equal("supporting_context", source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal(91, source.GetProperty("selectionHints").GetProperty("supportScore").GetInt32());
    }

    [Fact]
    public async Task ToolAgent_sources_resolve_prefers_precise_rag_memory_when_backend_returns_document_wide_source()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "requestedRef": "PDF01",
                      "source": {
                        "docId": "doc-precise",
                        "docPath": "Knowledge/precise.pdf",
                        "docName": "precise.pdf",
                        "pageStart": 1,
                        "pageEnd": 12,
                        "sourceHash": "hash-current",
                        "docLanguage": "en",
                        "matchedContentCards": [
                          { "title": "Document overview", "pageStart": 1, "pageEnd": 12, "kind": "document" }
                        ],
                        "selectionHints": {
                          "evidenceRole": "supporting_context",
                          "supportScore": 20
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "doc-precise",
            DocPath = "Knowledge/precise.pdf",
            DocName = "precise.pdf",
            CategoryPath = "Knowledge",
            PdfRef = "PDF01",
            SourceHash = "hash-current"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef
        {
            DocId = "doc-precise",
            DocPath = "Knowledge/precise.pdf",
            PageStart = 4,
            PageEnd = 4,
            ChunkId = "chunk-004",
            SourceHash = "hash-current",
            PageQualityStatus = "page_ok",
            PageExtractionConfidence = 0.97,
            MatchedContentCards =
            [
                new ToolMemory.SourceContentCardRef
                {
                    Title = "Matched evidence card",
                    ContentCardId = "card-004",
                    PageStart = 4,
                    PageEnd = 4,
                    Kind = "unit_lead"
                }
            ],
            SelectionHintEvidenceRole = "direct_evidence",
            SelectionHintSupportScore = 97
        });

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: mem);
        using var args = JsonDocument.Parse("""{"ref":"PDF01"}""");
        var result = await InvokePrivateToolAsync(sut, "ExecSourcesResolveV2Async", args.RootElement.Clone());
        var source = result.GetProperty("source");

        Assert.Equal("doc-precise", source.GetProperty("docId").GetString());
        Assert.Equal("Knowledge/precise.pdf", source.GetProperty("docPath").GetString());
        Assert.Equal("hash-current", source.GetProperty("sourceHash").GetString());
        Assert.Equal(4, source.GetProperty("pageStart").GetInt32());
        Assert.Equal(4, source.GetProperty("pageEnd").GetInt32());
        Assert.Equal("chunk-004", source.GetProperty("chunkId").GetString());
        Assert.Equal("page_ok", source.GetProperty("extractionQuality").GetProperty("pageQualityStatus").GetString());
        Assert.Equal(0.97, source.GetProperty("extractionQuality").GetProperty("pageExtractionConfidence").GetDouble());
        var cards = source.GetProperty("matchedContentCards").EnumerateArray().ToList();
        Assert.Contains(cards, card => string.Equals("Matched evidence card", card.GetProperty("title").GetString(), StringComparison.Ordinal));
        Assert.Contains(cards, card => string.Equals("Document overview", card.GetProperty("title").GetString(), StringComparison.Ordinal));
        var preciseCard = cards.Single(card => string.Equals("Matched evidence card", card.GetProperty("title").GetString(), StringComparison.Ordinal));
        Assert.Equal("card-004", preciseCard.GetProperty("contentCardId").GetString());
        Assert.Equal("direct_evidence", source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal(97, source.GetProperty("selectionHints").GetProperty("supportScore").GetInt32());
    }

    [Fact]
    public async Task ToolAgent_sources_resolve_does_not_resurrect_deleted_doc_from_memory_after_backend_not_found()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"source":null,"error":"source_not_found","requestedRef":"PDF01"}""",
                    Encoding.UTF8,
                    "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "deleted-doc",
            DocPath = "ATEX/deleted.pdf",
            DocName = "deleted.pdf",
            Category = "ATEX",
            CategoryPath = "ATEX",
            PdfRef = "PDF01"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);

        var sut = new ToolAgentOrchestrator(CreateApiClient(handler), llm: null!, mem: mem);
        using var args = JsonDocument.Parse("""{"ref":"PDF01"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecSourcesResolveV2Async", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        Assert.Equal("source_not_found", result.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("source").ValueKind);
        Assert.DoesNotContain("ATEX/deleted.pdf", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolAgent_sources_resolve_does_not_resurrect_memory_source_after_backend_error()
    {
        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/sources/resolve" => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":"boom"}""", Encoding.UTF8, "application/json")
            },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });

        var api = CreateApiClient(handler);
        var mem = new ToolMemory();
        mem.PdfMap["PDF01"] = new ToolMemory.DocumentItem
        {
            DocId = "stale-doc",
            DocPath = "ATEX/deleted.pdf",
            DocName = "deleted.pdf",
            Category = "ATEX",
            CategoryPath = "ATEX",
            PdfRef = "PDF01"
        };
        mem.LastListedDocuments.Add(mem.PdfMap["PDF01"]);

        var sut = new ToolAgentOrchestrator(api, llm: null!, mem: mem);
        using var args = JsonDocument.Parse("""{"ref":"PDF01"}""");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecSourcesResolveV2Async", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args.RootElement.Clone(), CancellationToken.None })!;
        var result = await task;

        Assert.Equal("sources_resolve_failed", result.GetProperty("error").GetString());
        Assert.Equal(500, result.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("source").ValueKind);
        Assert.DoesNotContain("ATEX/deleted.pdf", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminSummaryMissingAsync_preserves_capability_b_governance_metadata_from_catalog_summaries()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            if (string.Equals(req.RequestUri!.AbsolutePath, "/catalog/summaries", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "value": [
                            {
                              "docId": "doc-1",
                              "docPath": "Generic/governance.pdf",
                              "canonicalName": "governance.pdf",
                              "categoryCanonicalName": "Generic",
                              "summaryState": "stale",
                              "hasActiveSummaryJob": true,
                              "activeSummaryJobId": "job-1",
                              "activeSummaryJobStatus": "running",
                              "activeSummaryJobExecutionMode": "server_backoffice",
                              "activeSummaryJobRuntimeCapabilityKey": "capability_b",
                              "activeSummaryJobRuntimeCapabilityStatus": "qualified",
                              "activeSummaryJobEnqueueSource": "capability_b",
                              "activeSummaryJobCampaignId": "campaign-1",
                              "capabilityBReadyToEnqueue": true,
                              "capabilityBRecommendedAction": "enqueue_profile_refresh",
                              "capabilityBPolicyBlocked": false,
                              "capabilityBPriorityScore": 42.5,
                              "capabilityBLastJobStatus": "failed",
                              "capabilityBLastJobFinishedAt": "2026-05-07T01:02:03Z",
                              "capabilityBLastJobError": "timeout",
                              "capabilityBProfileState": "stale",
                              "capabilityBHasBackofficeProfile": true,
                              "capabilityBReasons": ["summary_stale"]
                            }
                          ],
                          "totals": { "total": 1, "staleStored": 1, "profileMissing": 0 },
                          "level": "medium"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var result = await api.AdminSummaryMissingAsync(50, 0, "Generic", null, CancellationToken.None);

        Assert.Contains("/catalog/summaries?pageSize=50&categoryPath=Generic", requestedPaths);
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("hasActiveSummaryJob").GetBoolean());
        Assert.Equal("job-1", item.GetProperty("activeSummaryJobId").GetString());
        Assert.Equal("running", item.GetProperty("activeSummaryJobStatus").GetString());
        Assert.Equal("server_backoffice", item.GetProperty("activeSummaryJobExecutionMode").GetString());
        Assert.Equal("capability_b", item.GetProperty("activeSummaryJobRuntimeCapabilityKey").GetString());
        Assert.True(item.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
        Assert.Equal("enqueue_profile_refresh", item.GetProperty("capabilityBRecommendedAction").GetString());
        Assert.False(item.GetProperty("capabilityBPolicyBlocked").GetBoolean());
        Assert.Equal(42.5, item.GetProperty("capabilityBPriorityScore").GetDouble());
        Assert.Equal("failed", item.GetProperty("capabilityBLastJobStatus").GetString());
        Assert.Equal("timeout", item.GetProperty("capabilityBLastJobError").GetString());
        Assert.Equal("summary_stale", item.GetProperty("capabilityBReasons")[0].GetString());
    }

    [Fact]
    public async Task DocumentsContextAsync_reads_user_context_endpoint_with_anchor_scope()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "found": true,
                      "contextKind": "around_chunk",
                      "items": [
                        {
                          "chunkId": "chunk-1",
                          "chunkIndex": 12,
                          "pageStart": 7,
                          "pageEnd": 8,
                          "text": "Indexed context"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var api = CreateApiClient(handler, adminKey: "test-admin-key");
        var result = await api.DocumentsContextAsync(
            " doc-123 ",
            " Knowledge\\Generic\\source.pdf ",
            " chunk-456 ",
            pageStart: 7,
            pageEnd: 8,
            before: 99,
            after: 99,
            limit: 99,
            offset: -4,
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("/documents/context", capturedRequest.RequestUri!.AbsolutePath);
        Assert.True(capturedRequest.Headers.TryGetValues("X-Api-Key", out var userKeys));
        Assert.Equal("test-api-key", Assert.Single(userKeys));
        Assert.False(capturedRequest.Headers.Contains("X-Admin-Key"));

        var qs = System.Web.HttpUtility.ParseQueryString(capturedRequest.RequestUri.Query);
        Assert.Equal("doc-123", qs.Get("docId"));
        Assert.Equal("Knowledge/Generic/source.pdf", qs.Get("docPath"));
        Assert.Equal("chunk-456", qs.Get("chunkId"));
        Assert.Equal("7", qs.Get("pageStart"));
        Assert.Equal("8", qs.Get("pageEnd"));
        Assert.Equal("20", qs.Get("before"));
        Assert.Equal("30", qs.Get("after"));
        Assert.Equal("50", qs.Get("limit"));
        Assert.Equal("0", qs.Get("offset"));
        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.Equal("Indexed context", result.GetProperty("items")[0].GetProperty("text").GetString());
    }

    private static HttpResponseMessage CaptureRagSearch(HttpRequestMessage request, Func<string, string> responseFactory)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseFactory(body), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CaptureSubmit(HttpRequestMessage request, Func<string, string> responseFactory)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseFactory(body), Encoding.UTF8, "application/json")
        };
    }

    private static async Task<JsonElement> InvokePrivateToolAsync(ToolAgentOrchestrator sut, string methodName, JsonElement args)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<JsonElement>)method!.Invoke(sut, new object[] { args, CancellationToken.None })!;
        return await task;
    }

    private static ApiClient CreateApiClient(HttpMessageHandler handler, string? adminKey = null)
    {
        var sut = new ApiClient();
        sut.Configure("http://localhost:5122", "test-api-key", "test-user", adminKey);

        var field = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(sut, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });

        return sut;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class AsyncStubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class StubLlmClient(string completion) : ILlmClient
    {
        public List<IReadOnlyList<(string role, string content)>> Requests { get; } = new();

        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        {
            Assert.True(forceJson);
            Requests.Add(messages);
            return Task.FromResult(completion);
        }

        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
