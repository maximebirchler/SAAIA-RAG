using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CompactSemanticEnvelopeNativeTokenHarnessTests
{
    private const string ManifestSha256 =
        "D1CCCA29AC3704B3058D934202379BADBAFEA80CB7865D084520E45CBDC3B7F6";
    private const string ContractsSha256 =
        "BD6ECE287F3AF3F4448F187604725D461097122DA0BE1AD762041DEE01104DA0";

    [Fact]
    public void A647_GREEN01_loads_frozen_inputs_only_when_hashes_match()
    {
        var contracts = LoadContracts();
        var cases = LoadCases();

        Assert.Equal("a645_compact_semantic_envelope_candidates_v1", contracts.SchemaVersion);
        Assert.Equal(25, cases.Count);
        Assert.Equal(Enumerable.Range(1, 25), cases.Select(item => item.CasePosition));
        Assert.Equal("survey_drone_shortest_recharge", cases[0].Id);
        Assert.Equal(3, cases[0].Excerpts.Count);
    }

    [Fact]
    public void A647_GREEN02_rejects_any_frozen_input_hash_drift()
    {
        var temporary = Path.Combine(
            Path.GetTempPath(),
            "saaia-a647-hash-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, "{}", new UTF8Encoding(false));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                CompactSemanticEnvelopeNativeTokenHarness.LoadFrozenCases(
                    temporary,
                    ManifestSha256));

            Assert.Contains("A647_FROZEN_INPUT_DRIFT", exception.Message);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    [Fact]
    public void A647_GREEN03_reconstructs_all_snapshots_deterministically()
    {
        var cases = LoadCases();
        var first = CompactSemanticEnvelopeNativeTokenHarness.BuildSnapshots(cases);
        var second = CompactSemanticEnvelopeNativeTokenHarness.BuildSnapshots(cases);

        Assert.Equal(25, first.Count);
        Assert.Equal(
            first.Select(item => (item.Json, item.Sha256)),
            second.Select(item => (item.Json, item.Sha256)));
        Assert.All(first, item => Assert.Equal(
            item.Json,
            CompactSemanticEnvelopeNativeTokenHarness.SerializeSnapshot(item.Snapshot)));
    }

    [Fact]
    public void A647_GREEN04_preserves_every_snapshot_provenance_field()
    {
        var snapshots = LoadSnapshots();

        Assert.All(snapshots, envelope =>
        {
            Assert.True(
                CompactSemanticEnvelopeNativeTokenHarness.ValidateSnapshotProvenance(envelope));
            Assert.Equal(envelope.Case.DocId, envelope.Snapshot.Document.Id);
            Assert.Equal(envelope.Case.DocName, envelope.Snapshot.Document.Name);
            Assert.Equal(envelope.Case.DocPath, envelope.Snapshot.Document.Path);
            Assert.Equal(envelope.Case.RevisionId, envelope.Snapshot.Document.Revision);
            Assert.Equal(
                envelope.Snapshot.Evidence.Select(item => item.Id),
                envelope.Snapshot.Document.Anchors);
        });
    }

    [Fact]
    public void A647_GREEN05_excludes_oracles_from_snapshots_and_counted_messages()
    {
        var contracts = LoadContracts();
        foreach (var snapshot in LoadSnapshots())
        {
            Assert.DoesNotContain("expectedDecision", snapshot.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("isDangerous", snapshot.Json, StringComparison.Ordinal);
            var payloads = CompactSemanticEnvelopeNativeTokenHarness
                .BuildControllerPayloads(snapshot, contracts)
                .Concat(CompactSemanticEnvelopeNativeTokenHarness
                    .BuildReviewerPayloads(snapshot, contracts));
            Assert.All(payloads, payload => Assert.True(
                CompactSemanticEnvelopeNativeTokenHarness.ValidateNoOracleLeak(payload)));
        }
    }

    [Fact]
    public void A647_GREEN06_builds_exact_v1_and_v2_controller_envelopes()
    {
        var contracts = LoadContracts();
        var snapshot = LoadSnapshots()[0];
        var payloads = CompactSemanticEnvelopeNativeTokenHarness
            .BuildControllerPayloads(snapshot, contracts);

        Assert.Equal(
            new[]
            {
                CompactSemanticEnvelopeNativeTokenHarness.V1,
                CompactSemanticEnvelopeNativeTokenHarness.V2
            },
            payloads.Select(item => item.Variant));
        Assert.All(payloads, payload =>
        {
            Assert.Equal("controller", payload.Role);
            Assert.Equal(2, payload.Messages.Count);
            Assert.Equal(new[] { "system", "user" }, payload.Messages.Select(item => item.Role));
            Assert.Single(payload.Tools);
            Assert.Contains("SNAPSHOT\n" + snapshot.Json, payload.Messages[1].Content);
        });
        Assert.Equal(contracts.V1ControllerSystemPrompt, payloads[0].Messages[0].Content);
        Assert.Equal(contracts.V2ControllerSystemPrompt, payloads[1].Messages[0].Content);
    }

    [Fact]
    public void A647_GREEN07_builds_all_representative_and_minimal_reviewer_envelopes()
    {
        var contracts = LoadContracts();
        var snapshot = LoadSnapshots()[0];
        var payloads = CompactSemanticEnvelopeNativeTokenHarness
            .BuildReviewerPayloads(snapshot, contracts);

        Assert.Equal(4, payloads.Count);
        Assert.Equal(2, payloads.Count(item => item.Form == "representative"));
        Assert.Equal(2, payloads.Count(item => item.Form == "minimal"));
        Assert.Equal(2, payloads.Count(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V1));
        Assert.Equal(2, payloads.Count(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V2));
        Assert.All(payloads, payload => Assert.Equal("reviewer", payload.Role));
    }

    [Fact]
    public void A647_GREEN08_representative_draft_reuses_each_excerpt_without_semantic_selection()
    {
        var frozenCase = LoadCases()[0];
        using var draft = JsonDocument.Parse(
            CompactSemanticEnvelopeNativeTokenHarness.BuildRepresentativeDraft(frozenCase));
        var claims = draft.RootElement.GetProperty("claims").EnumerateArray().ToArray();

        Assert.Equal(frozenCase.Excerpts.Count, claims.Length);
        for (var index = 0; index < claims.Length; index++)
        {
            Assert.Equal(frozenCase.Excerpts[index], claims[index].GetProperty("text").GetString());
            Assert.Equal(
                $"E{index + 1}",
                claims[index].GetProperty("evidenceIds")[0].GetString());
        }
    }

    [Fact]
    public void A647_GREEN09_proves_v1_reviewer_is_a_byte_stable_controller_prefix()
    {
        var contracts = LoadContracts();
        var snapshot = LoadSnapshots()[0];
        var controller = CompactSemanticEnvelopeNativeTokenHarness
            .BuildControllerPayloads(snapshot, contracts)
            .Single(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V1);
        var reviewers = CompactSemanticEnvelopeNativeTokenHarness
            .BuildReviewerPayloads(snapshot, contracts)
            .Where(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V1);

        Assert.All(reviewers, reviewer => Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateV1Prefix(controller, reviewer)));
    }

    [Fact]
    public void A647_GREEN10_proves_v2_reviewers_are_independent_and_cannot_write()
    {
        var contracts = LoadContracts();
        var snapshot = LoadSnapshots()[0];
        var controller = CompactSemanticEnvelopeNativeTokenHarness
            .BuildControllerPayloads(snapshot, contracts)
            .Single(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V2);
        var reviewers = CompactSemanticEnvelopeNativeTokenHarness
            .BuildReviewerPayloads(snapshot, contracts)
            .Where(item => item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V2);

        Assert.All(reviewers, reviewer => Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateV2Independence(
                controller,
                reviewer)));
        Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateV2ReviewerCannotWrite(contracts));
    }

    [Theory]
    [InlineData(3000, 2700, 3244)]
    [InlineData(3000, 2400, 3032)]
    [InlineData(2700, 3000, 3244)]
    public void A647_GREEN11_applies_the_conservative_reviewer_projection(
        int representative,
        int minimal,
        int expected)
        => Assert.Equal(
            expected,
            CompactSemanticEnvelopeNativeTokenHarness.ComputeProjectedWorst(
                representative,
                minimal));

    [Fact]
    public void A647_GREEN12_builds_the_exact_204_request_alternating_schedule()
    {
        var schedule = CompactSemanticEnvelopeNativeTokenHarness
            .BuildMeasurementSchedule(LoadSnapshots());

        Assert.Equal(204, schedule.Count);
        Assert.Equal(Enumerable.Range(1, 204), schedule.Select(item => item.Sequence));
        Assert.Equal(
            new[]
            {
                (CompactSemanticEnvelopeNativeTokenHarness.V1, 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, 2)
            },
            schedule.Take(8).Select(item => (item.Variant, item.Repetition)));
        Assert.Equal(
            new[]
            {
                (CompactSemanticEnvelopeNativeTokenHarness.V2, "representative", 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, "representative", 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, "representative", 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, "representative", 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, "minimal", 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V2, "minimal", 2),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, "minimal", 1),
                (CompactSemanticEnvelopeNativeTokenHarness.V1, "minimal", 2)
            },
            schedule.Skip(100).Take(8)
                .Select(item => (item.Variant, item.Form, item.Repetition)));
    }

    [Fact]
    public async Task A647_GREEN13_fake_counter_executes_all_payloads_deterministically()
    {
        var requestCount = 0;
        var records = await CompactSemanticEnvelopeNativeTokenHarness
            .ExecuteMeasurementScheduleAsync(
                "qwen-fixture",
                LoadSnapshots(),
                LoadContracts(),
                (payload, _) =>
                {
                    requestCount++;
                    return Task.FromResult(Encoding.UTF8.GetByteCount(payload) / 3 + 17);
                },
                CancellationToken.None);

        Assert.Equal(204, requestCount);
        Assert.Equal(204, records.Count);
        Assert.Equal(102, records.Select(item => item.PayloadSha256).Distinct().Count());
        Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateDuplicateMeasurements(
                records.Where(item =>
                    item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V1).ToArray()));
        Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateDuplicateMeasurements(
                records.Where(item =>
                    item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V2).ToArray()));
        var corrupted = records.ToArray();
        corrupted[1] = corrupted[1] with { InputTokens = corrupted[1].InputTokens + 1 };
        Assert.False(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateDuplicateMeasurements(corrupted));
    }

    [Fact]
    public async Task A647_GREEN14_endpoint_guard_allows_only_native_token_measurement_routes()
    {
        var transport = new RecordingHandler();
        var guard = new CompactSemanticEnvelopeNativeTokenHarness.EndpointGuardHandler(transport);
        using var client = new HttpClient(guard) { BaseAddress = new Uri("http://127.0.0.1:19091") };

        using var health = await client.GetAsync("/health");
        using var models = await client.GetAsync("/v1/models");
        using var tokens = await client.PostAsJsonAsync(
            "/v1/chat/completions/input_tokens",
            new { model = "fixture", messages = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        Assert.Equal(3, transport.RequestCount);
        Assert.True(CompactSemanticEnvelopeNativeTokenHarness.ValidateEndpointLedger(guard));
        Assert.All(guard.Ledger, item => Assert.Equal(64, item.RequestSha256.Length));
    }

    [Fact]
    public async Task A647_GREEN15_endpoint_guard_blocks_chat_completions_before_transport()
    {
        var transport = new RecordingHandler();
        var guard = new CompactSemanticEnvelopeNativeTokenHarness.EndpointGuardHandler(transport);
        using var client = new HttpClient(guard) { BaseAddress = new Uri("http://127.0.0.1:19091") };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PostAsJsonAsync(
                "/v1/chat/completions",
                new { model = "fixture", messages = Array.Empty<object>() }));

        Assert.Contains("A648_FORBIDDEN_ENDPOINT", exception.Message);
        Assert.Equal(0, transport.RequestCount);
        Assert.Equal(1, guard.ForbiddenRequestCount);
        Assert.False(guard.Ledger.Single().Allowed);
    }

    [Fact]
    public void A647_GREEN16_writes_a_complete_checkpoint_atomically()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "saaia-a647-checkpoint-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "checkpoint.json");
        try
        {
            CompactSemanticEnvelopeNativeTokenHarness.WriteCheckpointAtomically(
                path,
                new { completedCases = 7, status = "running" });

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(7, document.RootElement.GetProperty("completedCases").GetInt32());
            Assert.Equal("running", document.RootElement.GetProperty("status").GetString());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A647_GREEN17_refuses_reuse_of_an_existing_official_directory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "saaia-a648-official-" + Guid.NewGuid().ToString("N"));
        CompactSemanticEnvelopeNativeTokenHarness.EnsureOfficialDirectoryAbsent(directory);
        Directory.CreateDirectory(directory);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                CompactSemanticEnvelopeNativeTokenHarness.EnsureOfficialDirectoryAbsent(directory));
            Assert.Equal("A648_OFFICIAL_DIRECTORY_ALREADY_EXISTS", exception.Message);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A647_GREEN18_variant_gate_passes_only_complete_consistent_measurements()
    {
        var variant = CompactSemanticEnvelopeNativeTokenHarness.V2;
        var passing = BuildSyntheticRecords(
            variant,
            controllerTokens: 2200,
            representativeTokens: 2800,
            minimalTokens: 2500);

        var evaluation = CompactSemanticEnvelopeNativeTokenHarness
            .EvaluateVariantGates(variant, passing);

        Assert.True(evaluation.Eligible);
        Assert.Equal(25, passing.Count(item => item.Role == "controller") / 2);
        Assert.Equal(13, evaluation.ReviewerProjections.Count);
        Assert.Equal(3044, evaluation.MaximumReviewerProjectedTokens);
        Assert.True(evaluation.MinimumHeadroom > 0);
        Assert.Empty(evaluation.Errors);
    }

    [Fact]
    public void A647_GREEN19_variant_gate_fails_closed_on_null_mismatch_or_overflow()
    {
        var variant = CompactSemanticEnvelopeNativeTokenHarness.V2;
        var records = BuildSyntheticRecords(
            variant,
            controllerTokens: 2200,
            representativeTokens: 2800,
            minimalTokens: 2500).ToArray();
        records[0] = records[0] with { InputTokens = null };
        records[2] = records[2] with
        {
            InputTokens = CompactSemanticEnvelopeNativeTokenHarness.ControllerInputLimit + 1
        };

        var evaluation = CompactSemanticEnvelopeNativeTokenHarness
            .EvaluateVariantGates(variant, records);

        Assert.False(evaluation.Eligible);
        Assert.Contains("duplicate_measurement_mismatch", evaluation.Errors);
        Assert.Contains("input_tokens_missing_or_invalid", evaluation.Errors);
    }

    [Fact]
    public async Task A647_GREEN20_stops_runtime_in_finally_when_measurement_fails()
    {
        var events = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CompactSemanticEnvelopeNativeTokenHarness.RunNativeMeasurementAsync(
                _ =>
                {
                    events.Add("start");
                    return Task.CompletedTask;
                },
                _ =>
                {
                    events.Add("measure");
                    throw new InvalidOperationException("synthetic_failure");
                },
                _ =>
                {
                    events.Add("stop");
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("synthetic_failure", exception.Message);
        Assert.Equal(new[] { "start", "measure", "stop" }, events);
    }

    [Fact]
    public void A647_GREEN21_native_payload_keeps_required_tool_call_controls()
    {
        var contracts = LoadContracts();
        var payload = CompactSemanticEnvelopeNativeTokenHarness
            .BuildControllerPayloads(LoadSnapshots()[0], contracts)[0];
        using var json = JsonDocument.Parse(
            CompactSemanticEnvelopeNativeTokenHarness.SerializeNativePayload(
                "qwen-fixture",
                payload));

        Assert.Equal("qwen-fixture", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("required", json.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(json.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(1, json.RootElement.GetProperty("tools").GetArrayLength());
    }

    private static CompactSemanticEnvelopeNativeTokenHarness.FrozenContracts LoadContracts()
        => CompactSemanticEnvelopeNativeTokenHarness.LoadFrozenContracts(
            Path.Combine(Phase5Root, "A645-CONTRATS-COMPACTS-CANDIDATS.json"),
            ContractsSha256);

    private static IReadOnlyList<CompactSemanticEnvelopeNativeTokenHarness.FrozenCase> LoadCases()
        => CompactSemanticEnvelopeNativeTokenHarness.LoadFrozenCases(
            Path.Combine(Phase5Root, "A630-CASES-NORMALISES-ORACLES.json"),
            ManifestSha256);

    private static IReadOnlyList<CompactSemanticEnvelopeNativeTokenHarness.SnapshotEnvelope> LoadSnapshots()
        => CompactSemanticEnvelopeNativeTokenHarness.BuildSnapshots(LoadCases());

    private static IReadOnlyList<CompactSemanticEnvelopeNativeTokenHarness.NativeMeasurementRecord>
        BuildSyntheticRecords(
            string variant,
            int controllerTokens,
            int representativeTokens,
            int minimalTokens)
    {
        var records = new List<CompactSemanticEnvelopeNativeTokenHarness.NativeMeasurementRecord>();
        foreach (var position in Enumerable.Range(1, 25))
        {
            AddPair("controller", "controller", position, controllerTokens);
        }

        foreach (var snapshot in LoadSnapshots()
                     .Where(item => item.Case.ExpectedDecision == "answer"))
        {
            AddPair("reviewer", "representative", snapshot.Case.CasePosition, representativeTokens);
            AddPair("reviewer", "minimal", snapshot.Case.CasePosition, minimalTokens);
        }

        return records;

        void AddPair(string role, string form, int position, int tokens)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{variant}|{role}|{form}|{position}")));
            for (var repetition = 1; repetition <= 2; repetition++)
            {
                records.Add(new CompactSemanticEnvelopeNativeTokenHarness.NativeMeasurementRecord(
                    variant,
                    role,
                    form,
                    position,
                    repetition,
                    tokens,
                    hash,
                    ElapsedMilliseconds: 1,
                    StatusCode: 200));
            }
        }
    }

    private static string Phase5Root => Path.Combine(
        FindRepositoryRoot(),
        "artifacts",
        "goal-rag-product-20260827-1041",
        "phase5");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("A647_REPOSITORY_ROOT_NOT_FOUND");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
