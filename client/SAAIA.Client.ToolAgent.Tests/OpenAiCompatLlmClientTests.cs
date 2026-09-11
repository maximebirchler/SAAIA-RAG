using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class OpenAiCompatLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_sends_conservative_sampling_and_answer_budget()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "You are SAAIA."),
            ("user", "Hello")
        }, forceJson: false, CancellationToken.None);

        Assert.Equal("ok", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;

        Assert.Equal(1000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.Equal(0.85, root.GetProperty("top_p").GetDouble(), precision: 3);
        Assert.Equal(0.2, root.GetProperty("frequency_penalty").GetDouble(), precision: 3);
        Assert.Equal(0.05, root.GetProperty("presence_penalty").GetDouble(), precision: 3);
        Assert.True(root.TryGetProperty("stop", out var stop));
        Assert.Contains(stop.EnumerateArray(), item => item.GetString() == "\nTOOL_RESULTS");
    }

    [Fact]
    public async Task CompleteAsync_collapses_multiple_system_messages_for_strict_chat_templates()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "Global orchestration rules."),
            ("system", "SAAIA_SOURCE_BACKED_STEP=Planner\nFocused planner rules."),
            ("user", "Plan this request.")
        }, forceJson: true, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(
            "Global orchestration rules.\n\nFocused planner rules.",
            messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.DoesNotContain("SAAIA_SOURCE_BACKED_STEP=", handler.LastBody);
    }

    [Fact]
    public async Task CompleteAsync_keeps_json_response_format_and_larger_budget_for_structured_calls()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"{\"mode\":\"auto\"}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "Return JSON."),
            ("user", "Route this.")
        }, forceJson: true, CancellationToken.None);

        Assert.Equal("{\"mode\":\"auto\"}", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;

        Assert.Equal(1600, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.TryGetProperty("response_format", out var format));
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("stop", out _));
    }

    [Fact]
    public async Task CompleteStructuredAsync_sends_named_strict_json_schema()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"{\"decision\":\"answer\"}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");
        var contract = LlmStructuredOutputContract.Parse(
            "evidence_decision",
            """
            {
              "type": "object",
              "properties": {
                "decision": { "type": "string", "enum": ["answer", "stop"] }
              },
              "required": ["decision"],
              "additionalProperties": false
            }
            """);

        var answer = await client.CompleteStructuredAsync(
            new[] { ("user", "Choose the evidence action.") },
            contract,
            CancellationToken.None);

        Assert.Equal("{\"decision\":\"answer\"}", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var format = payload.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var namedSchema = format.GetProperty("json_schema");
        Assert.Equal("evidence_decision", namedSchema.GetProperty("name").GetString());
        Assert.True(namedSchema.GetProperty("strict").GetBoolean());
        Assert.Equal("object", namedSchema.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(payload.RootElement.TryGetProperty("stop", out _));
        Assert.Equal(0d, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1d, payload.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(0d, payload.RootElement.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(0d, payload.RootElement.GetProperty("presence_penalty").GetDouble());
    }

    [Fact]
    public async Task CompleteStructuredAsync_retries_with_legacy_llama_schema_when_standard_shape_is_rejected()
    {
        var handler = new SequencedCaptureHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"unsupported response format\"}}", Encoding.UTF8, "application/json")
            },
            JsonResponse("{\"decision\":\"answer\"}"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");
        var contract = LlmStructuredOutputContract.Parse(
            "legacy_decision",
            """{"type":"object","properties":{"decision":{"type":"string"}},"required":["decision"],"additionalProperties":false}""");

        var answer = await client.CompleteStructuredAsync(
            new[] { ("user", "Choose.") },
            contract,
            CancellationToken.None);

        Assert.Equal("{\"decision\":\"answer\"}", answer);
        Assert.Equal(2, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("json_schema", first.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var legacy = second.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", legacy.GetProperty("type").GetString());
        Assert.Equal("object", legacy.GetProperty("schema").GetProperty("type").GetString());
        Assert.All(
            new[] { first.RootElement, second.RootElement },
            payload =>
            {
                Assert.Equal(0d, payload.GetProperty("temperature").GetDouble());
                Assert.Equal(1d, payload.GetProperty("top_p").GetDouble());
                Assert.Equal(0d, payload.GetProperty("frequency_penalty").GetDouble());
                Assert.Equal(0d, payload.GetProperty("presence_penalty").GetDouble());
            });
    }

    [Fact]
    public async Task CompleteAsync_uses_source_backed_step_json_budget_when_marked()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"{\"decision\":\"answer\"}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeActionRepair\nReview the evidence decision."),
            ("user", "Return JSON.")
        }, forceJson: true, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;
        Assert.Equal(400, root.GetProperty("max_tokens").GetInt32());
        var sentSystemPrompt = root.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("Review the evidence decision.", sentSystemPrompt);
        Assert.DoesNotContain("SAAIA_SOURCE_BACKED_STEP=", handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_bounds_exact_planner_and_intake_review_transport_budgets()
    {
        var handler = new SequencedCaptureHandler(
            JsonResponse("{\"requests\":[]}"),
            JsonResponse("{\"structureReviewDecision\":\"accept\"}"),
            JsonResponse("{\"decision\":\"accept\",\"labels\":[\"A\",\"B\"],\"quotes\":[\"A\",\"B\"],\"relations\":[\"exact\",\"exact\"]}"),
            JsonResponse("{\"selectedConflictCandidateIds\":[1]}"),
            JsonResponse("{\"rowDimensionRole\":\"Period\"}"),
            JsonResponse("{\"anchors\":[{\"candidateId\":1,\"quote\":\"A\",\"relation\":\"exact\"}]}"),
            JsonResponse("{\"assignments\":[{\"requestId\":1,\"facetId\":1}]}"),
            JsonResponse("{\"decision\":\"accept\",\"queries\":[\"answer terms\"]}"),
            JsonResponse("{\"reviewDecision\":\"accept\",\"requests\":{}}"),
            JsonResponse("{\"reviewDecision\":\"accept\",\"requests\":{}}"),
            JsonResponse("{\"reviewDecision\":\"accept\",\"requests\":{}}"),
            JsonResponse("{\"decision\":\"accept\",\"kind\":\"two_axis_grid\"}"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=Planner"),
            ("user", "Return the initial plan JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeReview"),
            ("user", "Return the focused review JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisAdjudication"),
            ("user", "Return the compact parallel-array column decision JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisSelectionPatch"),
            ("user", "Return the localized conflict selection JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeRowAxisHeaderRolePatch"),
            ("user", "Return the semantic row dimension JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisAnchorPatch"),
            ("user", "Return the immutable-id anchor patch JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerFacetAssignmentPatch"),
            ("user", "Return the immutable-id facet mapping JSON.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerCompactFacetPlan"),
            ("user", "Return the immutable-id compact query arrays.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryAudit"),
            ("user", "Independently audit the provisional query plan.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryAuditContractRepair"),
            ("user", "Repair the complete independent audit contract.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerRowIndependentQueryRepair"),
            ("user", "Derive a clean reusable query set.")
        }, forceJson: true, CancellationToken.None);
        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeShapeAdjudication"),
            ("user", "Return only the semantic output-shape decision.")
        }, forceJson: true, CancellationToken.None);

        Assert.Equal(12, handler.Bodies.Count);
        using var initial = JsonDocument.Parse(handler.Bodies[0]);
        using var focusedReview = JsonDocument.Parse(handler.Bodies[1]);
        using var compactColumnAxis = JsonDocument.Parse(handler.Bodies[2]);
        using var conflictSelection = JsonDocument.Parse(handler.Bodies[3]);
        using var rowHeaderRole = JsonDocument.Parse(handler.Bodies[4]);
        using var columnAnchorPatch = JsonDocument.Parse(handler.Bodies[5]);
        using var facetAssignmentPatch = JsonDocument.Parse(handler.Bodies[6]);
        using var compactFacetPlan = JsonDocument.Parse(handler.Bodies[7]);
        using var acceptedQueryAudit = JsonDocument.Parse(handler.Bodies[8]);
        using var acceptedQueryAuditRepair = JsonDocument.Parse(handler.Bodies[9]);
        using var rowIndependentQueryRepair = JsonDocument.Parse(handler.Bodies[10]);
        using var intakeShape = JsonDocument.Parse(handler.Bodies[11]);
        Assert.Equal(480, initial.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(480, focusedReview.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, compactColumnAxis.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, conflictSelection.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, rowHeaderRole.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(480, columnAnchorPatch.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, facetAssignmentPatch.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(640, compactFacetPlan.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(640, acceptedQueryAudit.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(640, acceptedQueryAuditRepair.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(480, rowIndependentQueryRepair.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, intakeShape.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_keeps_the_evidence_judge_budget_inside_the_local_context_reserve()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"{\"decision\":\"answer\"}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=EvidenceJudge"),
            ("user", "Return the compact decision JSON.")
        }, forceJson: true, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        Assert.Equal(400, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_bounds_document_overview_writer_output_to_the_verified_candidate_shape()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"1. Point [E1]"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(
            http,
            "http://localhost:1234",
            "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "Rédige uniquement les candidats étayés."),
            ("user", "SAAIA_DOCUMENT_OVERVIEW_WRITER\nCANONICAL_EVIDENCE:\n[E1] text=preuve")
        }, forceJson: false, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        var root = payload.RootElement;
        Assert.Equal(320, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1d, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9d, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(0d, root.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(0d, root.GetProperty("presence_penalty").GetDouble());
    }

    [Fact]
    public async Task CompleteAsync_bounds_document_overview_selector_to_ids_only()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"E1,E2,E3"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(
            http,
            "http://localhost:1234",
            "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "Return only evidence IDs."),
            ("user", "SAAIA_DOCUMENT_OVERVIEW_SELECTOR\nEVIDENCE:\nE1 text=one")
        }, forceJson: false, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        Assert.Equal(
            64,
            payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_bounds_document_overview_candidate_repair()
    {
        var handler = new CaptureHandler(
            """{"choices":[{"message":{"content":"Point corrigé [E1]"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(
            http,
            "http://localhost:1234",
            "local-model");

        await client.CompleteAsync(new[]
        {
            ("system", "Repair one candidate."),
            ("user", "SAAIA_DOCUMENT_OVERVIEW_CANDIDATE_REPAIR\nevidence_id=E1")
        }, forceJson: false, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        Assert.Equal(
            96,
            payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_preserves_source_backed_budget_when_json_format_falls_back()
    {
        var handler = new SequencedCaptureHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"json mode unavailable\"}}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"decision\\\":\\\"answer\\\"}\"}}]}", Encoding.UTF8, "application/json")
            });
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeActionRepair"),
            ("user", "Return JSON.")
        }, forceJson: true, CancellationToken.None);

        Assert.Equal("{\"decision\":\"answer\"}", answer);
        Assert.Equal(2, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(400, first.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(400, second.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(first.RootElement.TryGetProperty("response_format", out _));
        Assert.False(second.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task CompleteAsync_uses_larger_budget_for_broad_source_backed_writer_prompts()
    {
        var handler = new CaptureHandler("""{"choices":[{"message":{"content":"ok"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatLlmClient(http, "http://localhost:1234", "local-model");

        var answer = await client.CompleteAsync(new[]
        {
            ("system", "Write a clean source-backed answer. Do not dump raw excerpts."),
            ("user", """
PRIVATE_SOURCE_COVERAGE_NOTE:
The initial evidence is partial.

PRIVATE_SOURCE_WRITING_BRIEF:
Rewrite the useful evidence into a clean answer.

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
Several distinct candidates across several pages.
""")
        }, forceJson: false, CancellationToken.None);

        Assert.Equal("ok", answer);
        using var payload = JsonDocument.Parse(handler.LastBody ?? "{}");
        Assert.Equal(4096, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CaptureHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SequencedCaptureHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, responses.Length - 1);
            return responses[index];
        }
    }

    private static HttpResponseMessage JsonResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }),
                Encoding.UTF8,
                "application/json")
        };
}
