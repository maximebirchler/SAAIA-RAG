using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

public static class CompactSemanticEnvelopeNativeTokenHarness
{
    public const int ControllerInputLimit = 3392;
    public const int ReviewerInputLimit = 3648;
    public const int DraftOutputLimit = 512;
    public const int DraftReplayAllowance = 32;
    public const string V1 = "V1_SHARED_SINGLE_ENVELOPE_PREFIX";
    public const string V2 = "V2_STAGE_SPECIFIC_INDEPENDENT_ENVELOPES";
    public const string VerifierResult = "{\"valid\":true,\"errors\":[]}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public sealed record FrozenContracts(
        string SchemaVersion,
        string V1ControllerSystemPrompt,
        string V1ControllerInstruction,
        string V1ReviewerInstruction,
        SourceBackedAgentToolDefinition V1Tool,
        string V2ControllerSystemPrompt,
        string V2ReviewerSystemPrompt,
        string V2ControllerInstruction,
        string V2ReviewerInstruction,
        SourceBackedAgentToolDefinition V2ControllerTool,
        SourceBackedAgentToolDefinition V2ReviewerTool);

    public sealed record FrozenCase(
        int CasePosition,
        string Id,
        string Question,
        string Language,
        string Focus,
        string Plan,
        string ExpectedDecision,
        string DocId,
        string DocName,
        string DocPath,
        string RevisionId,
        IReadOnlyList<string> Excerpts);

    public sealed record SnapshotEnvelope(
        FrozenCase Case,
        DiagnosticSnapshot Snapshot,
        string Json,
        string Sha256);

    public sealed record EnvelopePayload(
        string Variant,
        string Role,
        string Form,
        int CasePosition,
        string CaseId,
        string SnapshotSha256,
        string SnapshotJson,
        IReadOnlyList<SourceBackedAgentMessage> Messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> Tools);

    public sealed record MeasurementScheduleItem(
        int Sequence,
        string Variant,
        string Role,
        string Form,
        int CasePosition,
        string CaseId,
        int Repetition);

    public sealed record NativeMeasurementRecord(
        string Variant,
        string Role,
        string Form,
        int CasePosition,
        int Repetition,
        int? InputTokens,
        string PayloadSha256,
        long ElapsedMilliseconds = 0,
        int? StatusCode = null);

    public sealed record ReviewerProjection(
        int CasePosition,
        int RepresentativeTokens,
        int MinimalTokens,
        int DraftReplayDelta,
        int ProjectedWorstTokens);

    public sealed record VariantGateEvaluation(
        string Variant,
        bool Eligible,
        int? MaximumControllerTokens,
        int? MaximumReviewerRepresentativeTokens,
        int? MaximumReviewerProjectedTokens,
        int? MinimumControllerHeadroom,
        int? MinimumReviewerHeadroom,
        int? MinimumHeadroom,
        IReadOnlyList<string> Errors,
        IReadOnlyList<ReviewerProjection> ReviewerProjections);

    public sealed record EndpointLedgerEntry(
        string Method,
        string Path,
        bool Allowed,
        string RequestSha256,
        int RequestBytes,
        int? StatusCode);

    public sealed record DiagnosticSnapshot(
        int Version,
        string Question,
        string Language,
        string Focus,
        string Plan,
        DiagnosticDocument Document,
        IReadOnlyList<DiagnosticEvidence> Evidence,
        IReadOnlyList<DiagnosticRejected> Rejected,
        IReadOnlyList<DiagnosticAction> Actions,
        IReadOnlyList<DiagnosticCapability> Capabilities,
        DiagnosticBudget Budget,
        int Cycle);

    public sealed record DiagnosticDocument(
        string Id,
        string Name,
        string Path,
        string Revision,
        IReadOnlyList<string> Anchors);

    public sealed record DiagnosticEvidence(
        string Id,
        string DocId,
        string Revision,
        string SourceHash,
        IReadOnlyList<int> Pages,
        string Kind,
        string Excerpt,
        string Locator);

    public sealed record DiagnosticRejected(string Id, string Reason);

    public sealed record DiagnosticAction(
        string Capability,
        string ArgumentsDigest,
        string Outcome,
        IReadOnlyList<string> NewEvidenceIds);

    public sealed record DiagnosticCapability(string Name, string Arguments);

    public sealed record DiagnosticBudget(
        int ModelCallsRemaining,
        int ToolCallsRemaining,
        int CyclesRemaining,
        int MillisecondsRemaining);

    public static FrozenContracts LoadFrozenContracts(
        string path,
        string expectedSha256)
    {
        VerifyHash(path, expectedSha256);
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;
        var variants = root.GetProperty("variants").EnumerateArray().ToArray();
        var v1 = variants.Single(static item =>
            item.GetProperty("id").GetString() == V1);
        var v2 = variants.Single(static item =>
            item.GetProperty("id").GetString() == V2);

        return new FrozenContracts(
            root.GetProperty("schemaVersion").GetString() ?? string.Empty,
            RequiredString(v1, "controllerSystemPrompt"),
            RequiredString(v1, "controllerInstruction"),
            RequiredString(v1, "reviewerInstruction"),
            ReadTool(v1.GetProperty("controllerTools")[0]),
            RequiredString(v2, "controllerSystemPrompt"),
            RequiredString(v2, "reviewerSystemPrompt"),
            RequiredString(v2, "controllerInstruction"),
            RequiredString(v2, "reviewerInstruction"),
            ReadTool(v2.GetProperty("controllerTools")[0]),
            ReadTool(v2.GetProperty("reviewerTools")[0]));
    }

    public static IReadOnlyList<FrozenCase> LoadFrozenCases(
        string path,
        string expectedSha256)
    {
        VerifyHash(path, expectedSha256);
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var cases = document.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Select(static item => new FrozenCase(
                item.GetProperty("casePosition").GetInt32(),
                RequiredString(item, "id"),
                RequiredString(item, "question"),
                RequiredString(item, "language"),
                RequiredString(item, "questionFocus"),
                RequiredString(item, "semanticPlan"),
                RequiredString(item, "expectedDecision"),
                RequiredString(item, "docId"),
                RequiredString(item, "docName"),
                RequiredString(item, "docPath"),
                RequiredString(item, "revisionId"),
                item.GetProperty("excerpts").EnumerateArray()
                    .Select(static excerpt => excerpt.GetString() ?? string.Empty)
                    .ToArray()))
            .OrderBy(static item => item.CasePosition)
            .ToArray();

        if (cases.Length != 25
            || !cases.Select(static item => item.CasePosition)
                .SequenceEqual(Enumerable.Range(1, 25)))
        {
            throw new InvalidOperationException("A647_FROZEN_CASE_SET_INVALID");
        }

        return cases;
    }

    public static IReadOnlyList<SnapshotEnvelope> BuildSnapshots(
        IReadOnlyList<FrozenCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        return cases.Select(BuildSnapshot).ToArray();
    }

    public static string SerializeSnapshot(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static bool ValidateSnapshotProvenance(SnapshotEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Snapshot.Version != 2
            || envelope.Snapshot.Evidence.Count != envelope.Case.Excerpts.Count
            || envelope.Snapshot.Document.Anchors.Count != envelope.Snapshot.Evidence.Count)
        {
            return false;
        }

        for (var index = 0; index < envelope.Snapshot.Evidence.Count; index++)
        {
            var item = envelope.Snapshot.Evidence[index];
            if (item.Id != $"E{index + 1}"
                || item.DocId != envelope.Case.DocId
                || item.Revision != envelope.Case.RevisionId
                || item.SourceHash.Length != 64
                || item.Pages.Count != 1
                || item.Pages[0] != index + 1
                || item.Excerpt != envelope.Case.Excerpts[index]
                || string.IsNullOrWhiteSpace(item.Locator))
            {
                return false;
            }
        }

        return string.Equals(
            envelope.Sha256,
            Sha256(envelope.Json),
            StringComparison.Ordinal);
    }

    public static bool ValidateNoOracleLeak(EnvelopePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = string.Join(
            "\n",
            payload.Messages.Select(static message => message.Content ?? string.Empty));
        foreach (var forbidden in new[]
                 {
                     "expectedDecision",
                     "expectedEvidenceIds",
                     "requiredPatterns",
                     "expectedAdequacy",
                     "expectedNextCapability",
                     "isDangerous"
                 })
        {
            if (text.Contains(forbidden, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<EnvelopePayload> BuildControllerPayloads(
        SnapshotEnvelope snapshot,
        FrozenContracts contracts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(contracts);
        return new[]
        {
            BuildControllerPayload(
                V1,
                snapshot,
                contracts.V1ControllerSystemPrompt,
                contracts.V1ControllerInstruction,
                contracts.V1Tool),
            BuildControllerPayload(
                V2,
                snapshot,
                contracts.V2ControllerSystemPrompt,
                contracts.V2ControllerInstruction,
                contracts.V2ControllerTool)
        };
    }

    public static IReadOnlyList<EnvelopePayload> BuildReviewerPayloads(
        SnapshotEnvelope snapshot,
        FrozenContracts contracts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(contracts);
        var representative = BuildRepresentativeDraft(snapshot.Case);
        var minimal = BuildMinimalDraft();
        return new[]
        {
            BuildV1ReviewerPayload(snapshot, contracts, representative, "representative"),
            BuildV1ReviewerPayload(snapshot, contracts, minimal, "minimal"),
            BuildV2ReviewerPayload(snapshot, contracts, representative, "representative"),
            BuildV2ReviewerPayload(snapshot, contracts, minimal, "minimal")
        };
    }

    public static string BuildRepresentativeDraft(FrozenCase frozenCase)
    {
        ArgumentNullException.ThrowIfNull(frozenCase);
        var value = new
        {
            decision = "answer_draft",
            title = string.Empty,
            presentation = frozenCase.Excerpts.Count > 1 ? "bullets" : "paragraphs",
            claims = frozenCase.Excerpts.Select(static (excerpt, index) => new
            {
                text = excerpt,
                evidenceIds = new[] { $"E{index + 1}" }
            }).ToArray()
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static string BuildMinimalDraft()
    {
        var value = new
        {
            decision = "answer_draft",
            title = string.Empty,
            presentation = "paragraphs",
            claims = new[]
            {
                new { text = ".", evidenceIds = new[] { "E1" } }
            }
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static bool ValidateV1Prefix(
        EnvelopePayload controller,
        EnvelopePayload reviewer)
    {
        if (controller.Variant != V1
            || reviewer.Variant != V1
            || controller.Messages.Count >= reviewer.Messages.Count
            || controller.Tools.Count != reviewer.Tools.Count)
        {
            return false;
        }

        for (var index = 0; index < controller.Messages.Count; index++)
        {
            if (!string.Equals(
                    SerializeNativeMessage(controller.Messages[index]),
                    SerializeNativeMessage(reviewer.Messages[index]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return ToolsEqual(controller.Tools, reviewer.Tools);
    }

    public static bool ValidateV2Independence(
        EnvelopePayload controller,
        EnvelopePayload reviewer)
        => controller.Variant == V2
           && reviewer.Variant == V2
           && reviewer.Messages.Count == 2
           && reviewer.Messages.All(static message =>
               message.Role is "system" or "user")
           && reviewer.Messages[1].Content?.Contains(
               reviewer.SnapshotJson,
               StringComparison.Ordinal) == true
           && !ToolsEqual(controller.Tools, reviewer.Tools);

    public static bool ValidateV2ReviewerCannotWrite(FrozenContracts contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var parameters = contracts.V2ReviewerTool.Parameters;
        var properties = parameters.GetProperty("properties");
        if (properties.TryGetProperty("claims", out _)
            || properties.TryGetProperty("title", out _)
            || properties.TryGetProperty("presentation", out _))
        {
            return false;
        }

        var decisions = properties.GetProperty("decision")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        return !decisions.Contains("answer_draft", StringComparer.Ordinal)
               && decisions.SequenceEqual(
                   new[] { "accept", "search", "context", "clarify", "block" });
    }

    public static int ComputeProjectedWorst(
        int representativeTokens,
        int minimalTokens)
    {
        if (representativeTokens <= 0 || minimalTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeTokens));
        var delta = Math.Max(0, representativeTokens - minimalTokens);
        return checked(
            representativeTokens
            + Math.Max(0, DraftOutputLimit - delta)
            + DraftReplayAllowance);
    }

    public static IReadOnlyList<MeasurementScheduleItem> BuildMeasurementSchedule(
        IReadOnlyList<SnapshotEnvelope> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var result = new List<MeasurementScheduleItem>();
        var sequence = 0;
        foreach (var snapshot in snapshots.OrderBy(static item => item.Case.CasePosition))
        {
            var variants = snapshot.Case.CasePosition % 2 == 1
                ? new[] { V1, V2 }
                : new[] { V2, V1 };
            foreach (var variant in variants)
            {
                for (var repetition = 1; repetition <= 2; repetition++)
                {
                    result.Add(new MeasurementScheduleItem(
                        ++sequence,
                        variant,
                        "controller",
                        "controller",
                        snapshot.Case.CasePosition,
                        snapshot.Case.Id,
                        repetition));
                }
            }
        }

        var answers = snapshots
            .Where(static item => item.Case.ExpectedDecision == "answer")
            .OrderBy(static item => item.Case.CasePosition)
            .ToArray();
        for (var rank = 0; rank < answers.Length; rank++)
        {
            var forms = (rank + 1) % 2 == 1
                ? new[]
                {
                    (V2, "representative"),
                    (V1, "representative"),
                    (V2, "minimal"),
                    (V1, "minimal")
                }
                : new[]
                {
                    (V1, "representative"),
                    (V2, "representative"),
                    (V1, "minimal"),
                    (V2, "minimal")
                };
            foreach (var (variant, form) in forms)
            {
                for (var repetition = 1; repetition <= 2; repetition++)
                {
                    result.Add(new MeasurementScheduleItem(
                        ++sequence,
                        variant,
                        "reviewer",
                        form,
                        answers[rank].Case.CasePosition,
                        answers[rank].Case.Id,
                        repetition));
                }
            }
        }

        return result;
    }

    public static async Task<IReadOnlyList<NativeMeasurementRecord>>
        ExecuteMeasurementScheduleAsync(
            string model,
            IReadOnlyList<SnapshotEnvelope> snapshots,
            FrozenContracts contracts,
            Func<string, CancellationToken, Task<int>> countInputTokens,
            CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(countInputTokens);

        var catalog = new Dictionary<
            (string Variant, string Role, string Form, int CasePosition),
            EnvelopePayload>();
        foreach (var snapshot in snapshots)
        {
            foreach (var payload in BuildControllerPayloads(snapshot, contracts))
            {
                catalog.Add(
                    (payload.Variant, payload.Role, payload.Form, payload.CasePosition),
                    payload);
            }

            if (snapshot.Case.ExpectedDecision != "answer")
                continue;
            foreach (var payload in BuildReviewerPayloads(snapshot, contracts))
            {
                catalog.Add(
                    (payload.Variant, payload.Role, payload.Form, payload.CasePosition),
                    payload);
            }
        }

        var records = new List<NativeMeasurementRecord>();
        foreach (var item in BuildMeasurementSchedule(snapshots))
        {
            ct.ThrowIfCancellationRequested();
            if (!catalog.TryGetValue(
                    (item.Variant, item.Role, item.Form, item.CasePosition),
                    out var payload)
                || payload.CaseId != item.CaseId)
            {
                throw new InvalidOperationException(
                    $"A647_SCHEDULE_PAYLOAD_MISSING:{item.Sequence}");
            }

            var nativePayload = SerializeNativePayload(model, payload);
            var payloadHash = Sha256(nativePayload);
            var stopwatch = Stopwatch.StartNew();
            var inputTokens = await countInputTokens(nativePayload, ct)
                .ConfigureAwait(false);
            stopwatch.Stop();
            records.Add(new NativeMeasurementRecord(
                item.Variant,
                item.Role,
                item.Form,
                item.CasePosition,
                item.Repetition,
                inputTokens,
                payloadHash,
                stopwatch.ElapsedMilliseconds,
                200));
        }

        return records;
    }

    public static bool ValidateDuplicateMeasurements(
        IReadOnlyList<NativeMeasurementRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return false;
        return records
            .GroupBy(static item => new
            {
                item.Variant,
                item.Role,
                item.Form,
                item.CasePosition,
                item.PayloadSha256
            })
            .All(static group =>
            {
                var ordered = group.OrderBy(static item => item.Repetition).ToArray();
                return ordered.Length == 2
                       && ordered[0].Repetition == 1
                       && ordered[1].Repetition == 2
                       && ordered[0].InputTokens is > 0
                       && ordered[0].InputTokens == ordered[1].InputTokens;
            });
    }

    public static bool ValidateEndpointLedger(EndpointGuardHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler.ForbiddenRequestCount == 0
               && handler.Ledger.Count > 0
               && handler.Ledger.All(static entry => entry.Allowed)
               && handler.Ledger.All(static entry =>
                   (entry.Method == HttpMethod.Get.Method
                    && entry.Path is "/health" or "/v1/models")
                   || (entry.Method == HttpMethod.Post.Method
                       && entry.Path == "/v1/chat/completions/input_tokens"));
    }

    public static void WriteCheckpointAtomically(string path, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("A647_CHECKPOINT_PARENT_MISSING");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void EnsureOfficialDirectoryAbsent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path) || File.Exists(path))
            throw new InvalidOperationException("A648_OFFICIAL_DIRECTORY_ALREADY_EXISTS");
    }

    public static VariantGateEvaluation EvaluateVariantGates(
        string variant,
        IReadOnlyList<NativeMeasurementRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentNullException.ThrowIfNull(records);
        var errors = new List<string>();
        var selected = records.Where(item => item.Variant == variant).ToArray();
        if (!ValidateDuplicateMeasurements(selected))
            errors.Add("duplicate_measurement_mismatch");

        var first = selected.Where(static item => item.Repetition == 1).ToArray();
        var controller = first.Where(static item => item.Role == "controller").ToArray();
        var representative = first.Where(static item =>
            item.Role == "reviewer" && item.Form == "representative").ToArray();
        var minimal = first.Where(static item =>
            item.Role == "reviewer" && item.Form == "minimal").ToArray();

        if (controller.Length != 25)
            errors.Add($"controller_count={controller.Length}");
        if (representative.Length != 13)
            errors.Add($"reviewer_representative_count={representative.Length}");
        if (minimal.Length != 13)
            errors.Add($"reviewer_minimal_count={minimal.Length}");
        if (selected.Any(static item => item.InputTokens is null or <= 0))
            errors.Add("input_tokens_missing_or_invalid");

        var projections = new List<ReviewerProjection>();
        foreach (var rep in representative.OrderBy(static item => item.CasePosition))
        {
            var min = minimal.SingleOrDefault(item =>
                item.CasePosition == rep.CasePosition);
            if (min?.InputTokens is not > 0 || rep.InputTokens is not > 0)
                continue;
            var delta = Math.Max(0, rep.InputTokens.Value - min.InputTokens.Value);
            projections.Add(new ReviewerProjection(
                rep.CasePosition,
                rep.InputTokens.Value,
                min.InputTokens.Value,
                delta,
                ComputeProjectedWorst(rep.InputTokens.Value, min.InputTokens.Value)));
        }

        if (projections.Count != 13)
            errors.Add($"reviewer_projection_count={projections.Count}");

        int? maxController = controller.All(static item => item.InputTokens is > 0)
            && controller.Length > 0
                ? controller.Max(static item => item.InputTokens!.Value)
                : null;
        int? maxRepresentative = representative.All(static item => item.InputTokens is > 0)
            && representative.Length > 0
                ? representative.Max(static item => item.InputTokens!.Value)
                : null;
        int? maxProjected = projections.Count > 0
            ? projections.Max(static item => item.ProjectedWorstTokens)
            : null;

        if (maxController > ControllerInputLimit)
            errors.Add($"controller_max={maxController}>{ControllerInputLimit}");
        if (maxRepresentative > ReviewerInputLimit)
            errors.Add($"reviewer_representative_max={maxRepresentative}>{ReviewerInputLimit}");
        if (maxProjected > ReviewerInputLimit)
            errors.Add($"reviewer_projected_max={maxProjected}>{ReviewerInputLimit}");

        int? controllerHeadroom = maxController is { } controllerValue
            ? ControllerInputLimit - controllerValue
            : null;
        int? reviewerHeadroom = maxProjected is { } reviewerValue
            ? ReviewerInputLimit - reviewerValue
            : null;
        int? minimumHeadroom = controllerHeadroom is { } c
                               && reviewerHeadroom is { } r
            ? Math.Min(c, r)
            : null;
        if (minimumHeadroom is <= 0)
            errors.Add($"minimum_headroom={minimumHeadroom}");

        return new VariantGateEvaluation(
            variant,
            errors.Count == 0,
            maxController,
            maxRepresentative,
            maxProjected,
            controllerHeadroom,
            reviewerHeadroom,
            minimumHeadroom,
            errors,
            projections);
    }

    public static async Task RunNativeMeasurementAsync(
        Func<CancellationToken, Task> startRuntime,
        Func<CancellationToken, Task> measure,
        Func<CancellationToken, Task> stopRuntime,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(startRuntime);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(stopRuntime);
        await startRuntime(ct).ConfigureAwait(false);
        try
        {
            await measure(ct).ConfigureAwait(false);
        }
        finally
        {
            await stopRuntime(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static string SerializeNativePayload(
        string model,
        EnvelopePayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(payload);
        var value = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = payload.Messages.Select(ToNativeMessage).ToArray(),
            ["tools"] = payload.Tools.Select(static tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters
                }
            }).ToArray(),
            ["tool_choice"] = "required",
            ["parallel_tool_calls"] = false
        };
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public sealed class EndpointGuardHandler : DelegatingHandler
    {
        private readonly List<EndpointLedgerEntry> _ledger = new();
        private readonly object _sync = new();
        private int _forbiddenRequestCount;

        public EndpointGuardHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        public IReadOnlyList<EndpointLedgerEntry> Ledger
        {
            get
            {
                lock (_sync)
                    return _ledger.ToArray();
            }
        }

        public int ForbiddenRequestCount
            => Volatile.Read(ref _forbiddenRequestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var allowed = request.Method == HttpMethod.Get
                          && path is "/health" or "/v1/models"
                          || request.Method == HttpMethod.Post
                          && path == "/v1/chat/completions/input_tokens";
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var requestHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!allowed)
            {
                Interlocked.Increment(ref _forbiddenRequestCount);
                AddLedger(new EndpointLedgerEntry(
                    request.Method.Method,
                    path,
                    false,
                    requestHash,
                    bytes.Length,
                    null));
                throw new InvalidOperationException(
                    $"A648_FORBIDDEN_ENDPOINT:{request.Method}:{path}");
            }

            var response = await base.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            AddLedger(new EndpointLedgerEntry(
                request.Method.Method,
                path,
                true,
                requestHash,
                bytes.Length,
                (int)response.StatusCode));
            return response;
        }

        private void AddLedger(EndpointLedgerEntry entry)
        {
            lock (_sync)
                _ledger.Add(entry);
        }
    }

    private static SnapshotEnvelope BuildSnapshot(FrozenCase frozenCase)
    {
        var sourceHash = Sha256(frozenCase.DocId + "|" + frozenCase.RevisionId);
        var evidence = frozenCase.Excerpts.Select((excerpt, index) =>
            new DiagnosticEvidence(
                $"E{index + 1}",
                frozenCase.DocId,
                frozenCase.RevisionId,
                sourceHash,
                new[] { index + 1 },
                "chunk",
                excerpt,
                frozenCase.DocName + "#page=" + (index + 1)))
            .ToArray();
        var actionArguments = JsonSerializer.Serialize(new
        {
            question = frozenCase.Question,
            docId = frozenCase.DocId,
            docPath = frozenCase.DocPath,
            topK = 3
        }, JsonOptions);
        var snapshot = new DiagnosticSnapshot(
            2,
            frozenCase.Question,
            frozenCase.Language,
            frozenCase.Focus,
            frozenCase.Plan,
            new DiagnosticDocument(
                frozenCase.DocId,
                frozenCase.DocName,
                frozenCase.DocPath,
                frozenCase.RevisionId,
                evidence.Select(static item => item.Id).ToArray()),
            evidence,
            Array.Empty<DiagnosticRejected>(),
            new[]
            {
                new DiagnosticAction(
                    "rag.search",
                    Sha256(actionArguments),
                    "initial_frozen_evidence",
                    evidence.Select(static item => item.Id).ToArray())
            },
            new[]
            {
                new DiagnosticCapability(
                    "rag.search",
                    "query:string;document:string?;topK:integer[1..8];docId:string?;docPath:string?"),
                new DiagnosticCapability(
                    "documents.context",
                    "anchorEvidenceId:visible-id;contextMode:before|after|around|referenced;maximumPages:integer[1..4]")
            },
            new DiagnosticBudget(4, 4, 4, 240000),
            1);
        var json = SerializeSnapshot(snapshot);
        return new SnapshotEnvelope(frozenCase, snapshot, json, Sha256(json));
    }

    private static EnvelopePayload BuildControllerPayload(
        string variant,
        SnapshotEnvelope snapshot,
        string systemPrompt,
        string instruction,
        SourceBackedAgentToolDefinition tool)
        => new(
            variant,
            "controller",
            "controller",
            snapshot.Case.CasePosition,
            snapshot.Case.Id,
            snapshot.Sha256,
            snapshot.Json,
            new[]
            {
                SourceBackedAgentMessage.System(systemPrompt),
                SourceBackedAgentMessage.User(
                    "SNAPSHOT\n" + snapshot.Json + "\n" + instruction)
            },
            new[] { tool });

    private static EnvelopePayload BuildV1ReviewerPayload(
        SnapshotEnvelope snapshot,
        FrozenContracts contracts,
        string draftJson,
        string form)
    {
        var controller = BuildControllerPayloads(snapshot, contracts)
            .Single(static item => item.Variant == V1);
        using var draftDocument = JsonDocument.Parse(draftJson);
        var call = new SourceBackedAgentToolCall(
            "a647-draft",
            contracts.V1Tool.Name,
            draftDocument.RootElement.Clone());
        var messages = controller.Messages.Concat(new[]
        {
            SourceBackedAgentMessage.Assistant(null, new[] { call }),
            SourceBackedAgentMessage.Tool(
                call.Id,
                contracts.V1Tool.Name,
                VerifierResult),
            SourceBackedAgentMessage.User(contracts.V1ReviewerInstruction)
        }).ToArray();
        return controller with
        {
            Role = "reviewer",
            Form = form,
            Messages = messages
        };
    }

    private static EnvelopePayload BuildV2ReviewerPayload(
        SnapshotEnvelope snapshot,
        FrozenContracts contracts,
        string draftJson,
        string form)
        => new(
            V2,
            "reviewer",
            form,
            snapshot.Case.CasePosition,
            snapshot.Case.Id,
            snapshot.Sha256,
            snapshot.Json,
            new[]
            {
                SourceBackedAgentMessage.System(contracts.V2ReviewerSystemPrompt),
                SourceBackedAgentMessage.User(
                    "SNAPSHOT\n" + snapshot.Json
                    + "\nFROZEN_DRAFT\n" + draftJson
                    + "\nVERIFIER_RESULT\n" + VerifierResult
                    + "\n" + contracts.V2ReviewerInstruction)
            },
            new[] { contracts.V2ReviewerTool });

    private static SourceBackedAgentToolDefinition ReadTool(JsonElement wrapper)
    {
        var function = wrapper.GetProperty("function");
        return new SourceBackedAgentToolDefinition(
            RequiredString(function, "name"),
            RequiredString(function, "description"),
            function.GetProperty("parameters").Clone());
    }

    private static string RequiredString(JsonElement parent, string name)
        => parent.GetProperty(name).GetString()
           ?? throw new InvalidOperationException($"A647_REQUIRED_STRING_MISSING:{name}");

    private static void VerifyHash(string path, string expectedSha256)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("A647_FROZEN_INPUT_MISSING", path);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"A647_FROZEN_INPUT_DRIFT:{Path.GetFileName(path)}");
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool ToolsEqual(
        IReadOnlyList<SourceBackedAgentToolDefinition> left,
        IReadOnlyList<SourceBackedAgentToolDefinition> right)
        => left.Count == right.Count
           && left.Zip(right).All(static pair =>
               pair.First.Name == pair.Second.Name
               && pair.First.Description == pair.Second.Description
               && pair.First.Parameters.GetRawText()
               == pair.Second.Parameters.GetRawText());

    private static string SerializeNativeMessage(SourceBackedAgentMessage message)
        => JsonSerializer.Serialize(ToNativeMessage(message), JsonOptions);

    private static Dictionary<string, object?> ToNativeMessage(
        SourceBackedAgentMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };
        if (message.Role == "assistant")
        {
            payload["content"] = string.IsNullOrEmpty(message.Content)
                ? null
                : message.Content;
            if (message.ToolCalls is { Count: > 0 })
            {
                payload["tool_calls"] = message.ToolCalls.Select(static call =>
                    new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments.GetRawText()
                        }
                    }).ToArray();
            }

            return payload;
        }

        payload["content"] = message.Content ?? string.Empty;
        if (message.Role == "tool")
        {
            payload["tool_call_id"] = message.ToolCallId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message.Name))
                payload["name"] = message.Name;
        }

        return payload;
    }
}
