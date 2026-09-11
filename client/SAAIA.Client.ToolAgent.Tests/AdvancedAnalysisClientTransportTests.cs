using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class AdvancedAnalysisClientTransportTests
{
    private static readonly Guid SessionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid HandoffId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid JobId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task Api_transport_uses_configured_identity_and_typed_routes()
    {
        var seen = new List<RequestSnapshot>();
        var handler = new SequenceHandler(async (request, call, _) =>
        {
            seen.Add(await SnapshotAsync(request));
            return call switch
            {
                1 => Json(HttpStatusCode.Accepted, Job("queued", 1)),
                2 => Json(HttpStatusCode.OK, Job("running", 2)),
                3 => Json(HttpStatusCode.OK, Job("canceled", 3)),
                _ => throw new InvalidOperationException("unexpected request")
            };
        });
        var api = CreateApiClient(handler);

        var created = await api.CreateAdvancedAnalysisJobAsync(
            SessionId,
            Handoff(),
            CancellationToken.None);
        var current = await api.GetAdvancedAnalysisJobAsync(
            JobId,
            CancellationToken.None);
        var canceled = await api.CancelAdvancedAnalysisJobAsync(
            JobId,
            CancellationToken.None);

        Assert.Equal("queued", created.Status);
        Assert.Equal("running", current.Status);
        Assert.Equal("canceled", canceled.Status);
        Assert.Equal(HttpMethod.Post, seen[0].Method);
        Assert.Equal("/advanced-analysis/jobs", seen[0].PathAndQuery);
        Assert.Equal("test-api-key", seen[0].ApiKey);
        using (var body = JsonDocument.Parse(seen[0].Body!))
        {
            Assert.Equal("test-user", body.RootElement.GetProperty("userId").GetString());
            Assert.Equal(SessionId, body.RootElement.GetProperty("sessionId").GetGuid());
            Assert.Equal(HandoffId, body.RootElement.GetProperty("handoff").GetProperty("handoffId").GetGuid());
        }
        Assert.Equal(
            $"/advanced-analysis/jobs/{JobId:D}?userId=test-user",
            seen[1].PathAndQuery);
        Assert.Equal(
            $"/advanced-analysis/jobs/{JobId:D}/cancel?userId=test-user",
            seen[2].PathAndQuery);
    }

    [Fact]
    public async Task Workflow_replays_ambiguous_create_with_the_same_handoff()
    {
        var createBodies = new List<string>();
        var handler = new SequenceHandler(async (request, call, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/advanced-analysis/jobs")
            {
                createBodies.Add(await request.Content!.ReadAsStringAsync());
                if (createBodies.Count == 1)
                    throw new HttpRequestException("response lost after durable insert");
                return Json(HttpStatusCode.OK, Job("succeeded", 4, ValidResult()));
            }

            throw new InvalidOperationException($"unexpected request {call}");
        });

        var result = await ExecuteAsync(
            handler,
            new AdvancedAnalysisClientPollingOptions
            {
                MaximumCreateAttempts = 2,
                MaximumPolls = 2,
                MaximumConsecutiveTransportFailures = 2
            });

        Assert.True(result.Handled);
        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(2, createBodies.Count);
        Assert.Equal(createBodies[0], createBodies[1]);
        Assert.All(createBodies, body => Assert.Contains(HandoffId.ToString(), body, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workflow_resumes_polling_after_disconnect_and_renders_canonical_sources()
    {
        var creates = 0;
        var gets = 0;
        var handler = new SequenceHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/advanced-analysis/jobs")
            {
                creates++;
                return Task.FromResult(Json(HttpStatusCode.Accepted, Job("queued", 1)));
            }

            if (request.Method == HttpMethod.Get)
            {
                gets++;
                if (gets == 1)
                    throw new HttpRequestException("temporary disconnect");
                if (gets == 2)
                    return Task.FromResult(Json(HttpStatusCode.OK, Job("running", 2)));
                return Task.FromResult(Json(HttpStatusCode.OK, Job("succeeded", 3, ValidResult(twoSources: true))));
            }

            throw new InvalidOperationException("unexpected request");
        });

        var result = await ExecuteAsync(handler);

        Assert.True(result.Handled);
        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal("Advanced answer with canonical citations.", result.FinalAnswer);
        Assert.Equal(1, creates);
        Assert.Equal(3, gets);
        var payload = JsonSerializer.Serialize(result.SourcesPayload, ClientJson.CamelCase);
        var cards = SourceCardParser.Parse(payload);
        Assert.Equal(2, cards.Count);
        Assert.Equal("11111111-1111-1111-1111-111111111111", cards[0].DocId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", cards[0].RevisionId);
        Assert.Equal("chunk-1", cards[0].ChunkId);
        Assert.Equal(2, cards[0].PageStart);
        using var json = JsonDocument.Parse(payload);
        var metadata = json.RootElement.GetProperty("advancedAnalysis");
        Assert.Equal(JobId, metadata.GetProperty("jobId").GetGuid());
        Assert.Equal("succeeded", metadata.GetProperty("status").GetString());
        Assert.Equal(3, metadata.GetProperty("revision").GetInt32());
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("citation")]
    [InlineData("document_identity")]
    public async Task Workflow_blocks_untrusted_or_regressive_terminal_data(string mutation)
    {
        var handler = new SequenceHandler((request, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(Json(
                    HttpStatusCode.Accepted,
                    Job("running", mutation == "revision" ? 5 : 1)));
            }

            var result = ValidResult(
                unknownCitation: mutation == "citation",
                invalidDocumentIdentity: mutation == "document_identity");
            var terminal = Job(
                "succeeded",
                mutation == "revision" ? 4 : 2,
                result,
                handoffId: mutation == "identity" ? Guid.NewGuid() : HandoffId);
            return Task.FromResult(Json(HttpStatusCode.OK, terminal));
        });

        var result = await ExecuteAsync(handler);

        Assert.True(result.Handled);
        Assert.Equal("invalid_result", result.Outcome);
        Assert.NotEqual("Advanced answer with canonical citations.", result.FinalAnswer);
        Assert.Empty(SourceCardParser.Parse(JsonSerializer.Serialize(result.SourcesPayload, ClientJson.CamelCase)));
    }

    [Fact]
    public async Task Workflow_preserves_local_terminal_when_license_is_not_entitled()
    {
        var handler = new SequenceHandler((_, _, _) => Task.FromResult(Json(
            HttpStatusCode.Forbidden,
            new { error = "advanced_analysis_not_entitled" })));

        var result = await ExecuteAsync(handler);

        Assert.False(result.Handled);
        Assert.Equal("not_entitled", result.Outcome);
        Assert.Null(result.Job);
    }

    [Theory]
    [InlineData("queued", "pending")]
    [InlineData("failed", "failed")]
    [InlineData("canceled", "canceled")]
    public async Task Workflow_keeps_non_success_states_typed_and_source_free(
        string status,
        string expectedOutcome)
    {
        var handler = new SequenceHandler((_, _, _) => Task.FromResult(Json(
            status == "queued" ? HttpStatusCode.Accepted : HttpStatusCode.OK,
            Job(status, status == "queued" ? 1 : 2, new
            {
                schemaVersion = AdvancedAnalysisResultEnvelope.CurrentSchemaVersion,
                outcome = "answered",
                answerText = "This untrusted terminal payload must stay hidden.",
                providerKey = "fake-internal"
            }))));

        var result = await ExecuteAsync(
            handler,
            new AdvancedAnalysisClientPollingOptions
            {
                MaximumPolls = 0
            });

        Assert.True(result.Handled);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.DoesNotContain("untrusted terminal payload", result.FinalAnswer, StringComparison.OrdinalIgnoreCase);
        var payload = JsonSerializer.Serialize(result.SourcesPayload, ClientJson.CamelCase);
        Assert.Empty(SourceCardParser.Parse(payload));
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(status, json.RootElement.GetProperty("advancedAnalysis").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Non_entitlement_server_rejection_is_safe_and_does_not_publish_body()
    {
        var handler = new SequenceHandler((_, _, _) => Task.FromResult(Json(
            HttpStatusCode.BadRequest,
            new
            {
                error = "handoff_schema_unsupported",
                answerText = "body controlled by a rejected response"
            })));

        var result = await ExecuteAsync(handler);

        Assert.True(result.Handled);
        Assert.Equal("server_rejected", result.Outcome);
        Assert.DoesNotContain("body controlled", result.FinalAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.SourcesPayload);
    }

    [Fact]
    public async Task Local_cancellation_requests_server_cancellation_after_creation()
    {
        var cancellationCalls = 0;
        var handler = new SequenceHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/advanced-analysis/jobs")
            {
                return Task.FromResult(Json(HttpStatusCode.Accepted, Job("queued", 1)));
            }
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                cancellationCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK, Job("canceled", 2)));
            }
            throw new InvalidOperationException("unexpected request");
        });
        using var cts = new CancellationTokenSource();
        var api = CreateApiClient(handler);
        var orchestrator = new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ExecuteAdvancedAnalysisHandoffForTestsAsync(
                SessionId,
                Handoff(),
                new AdvancedAnalysisClientPollingOptions(),
                (_, _) =>
                {
                    cts.Cancel();
                    return Task.FromCanceled(cts.Token);
                },
                cts.Token));

        Assert.Equal(1, cancellationCalls);
    }

    private static Task<AdvancedAnalysisClientExecutionResult> ExecuteAsync(
        HttpMessageHandler handler,
        AdvancedAnalysisClientPollingOptions? options = null)
    {
        var api = CreateApiClient(handler);
        var orchestrator = new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());
        return orchestrator.ExecuteAdvancedAnalysisHandoffForTestsAsync(
            SessionId,
            Handoff(),
            options ?? new AdvancedAnalysisClientPollingOptions
            {
                MaximumCreateAttempts = 3,
                MaximumPolls = 8,
                MaximumConsecutiveTransportFailures = 3
            },
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
    }

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "test-api-key", "test-user");
        var field = typeof(ApiClient).GetField(
            "_http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(api, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });
        return api;
    }

    private static AdvancedAnalysisHandoffEnvelope Handoff()
        => new()
        {
            HandoffId = HandoffId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestText = "Build a source-backed comparison.",
            Language = "en",
            OriginIntent = "rag.answer",
            ReasonCode = "explicit_documentary_comparison_outside_local_envelope",
            TransferStage = "before_retrieval",
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = "comparison",
                AnswerUnitCount = 2,
                AtomicEvidenceCount = 2
            },
            ResearchState = new AdvancedAnalysisResearchState
            {
                MemoryIsEvidence = false,
                EvidenceRevalidationRequired = true
            },
            DataPolicy = new AdvancedAnalysisDataPolicy()
        };

    private static object Job(
        string status,
        int revision,
        object? result = null,
        Guid? handoffId = null)
        => new
        {
            jobId = JobId,
            handoffId = handoffId ?? HandoffId,
            sessionId = SessionId,
            status,
            revision,
            attemptCount = status == "queued" ? 0 : 1,
            cancelRequested = status == "canceled",
            createdAtUtc = "2026-09-11T03:00:00Z",
            updatedAtUtc = "2026-09-11T03:00:01Z",
            expiresAtUtc = "2026-10-11T03:00:00Z",
            startedAtUtc = status == "queued" ? null : "2026-09-11T03:00:00Z",
            finishedAtUtc = status is "succeeded" or "failed" or "canceled"
                ? "2026-09-11T03:00:01Z"
                : null,
            providerKey = status == "succeeded" ? "fake-internal" : null,
            result,
            lastErrorCode = status == "failed" ? "provider_failed" : null
        };

    private static object ValidResult(
        bool twoSources = false,
        bool unknownCitation = false,
        bool invalidDocumentIdentity = false)
    {
        var evidence = new List<object>
        {
            new
            {
                evidenceId = "E1",
                docId = invalidDocumentIdentity ? "not-a-guid" : "11111111-1111-1111-1111-111111111111",
                revisionId = "22222222-2222-2222-2222-222222222222",
                fileName = "procedure-a.pdf",
                docPath = "Quality/procedure-a.pdf",
                sourceHash = "abcdef0123456789",
                pageStart = 2,
                pageEnd = 2,
                chunkId = "chunk-1",
                anchorId = (string?)null,
                contentCardId = (string?)null
            }
        };
        if (twoSources)
        {
            evidence.Add(new
            {
                evidenceId = "E2",
                docId = "66666666-6666-6666-6666-666666666666",
                revisionId = "77777777-7777-7777-7777-777777777777",
                fileName = "procedure-b.pdf",
                docPath = "Quality/procedure-b.pdf",
                sourceHash = "0123456789abcdef",
                pageStart = 5,
                pageEnd = 6,
                chunkId = (string?)null,
                anchorId = "anchor-b",
                contentCardId = (string?)null
            });
        }

        return new
        {
            schemaVersion = AdvancedAnalysisResultEnvelope.CurrentSchemaVersion,
            outcome = "answered",
            answerText = "Advanced answer with canonical citations.",
            providerKey = "fake-internal",
            completedAtUtc = "2026-09-11T03:00:01Z",
            elapsedMilliseconds = 1000,
            evidence,
            claims = new[]
            {
                new
                {
                    claimId = "C1",
                    text = "Claim one.",
                    evidenceIds = new[] { unknownCitation ? "missing" : "E1" }
                },
                new
                {
                    claimId = "C2",
                    text = "Claim two.",
                    evidenceIds = new[] { twoSources ? "E2" : "E1" }
                }
            }
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ClientJson.CamelCase),
                Encoding.UTF8,
                "application/json")
        };

    private static async Task<RequestSnapshot> SnapshotAsync(HttpRequestMessage request)
        => new(
            request.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.TryGetValues("X-Api-Key", out var keys)
                ? keys.Single()
                : null,
            request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync());

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string PathAndQuery,
        string? ApiKey,
        string? Body);

    private sealed class SequenceHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, Interlocked.Increment(ref _calls), cancellationToken);
    }
}
