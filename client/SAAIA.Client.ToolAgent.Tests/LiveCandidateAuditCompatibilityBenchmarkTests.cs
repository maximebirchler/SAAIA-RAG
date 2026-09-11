using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveCandidateAuditCompatibilityBenchmarkTests(
    ITestOutputHelper output)
{
    private static readonly string[] ExpectedContentCardIds =
    {
        "7ab89814-ca8d-aa36-f596-03eb402b3c72",
        "5af80f4d-68b8-3228-935b-956a86754598",
        "6b02209d-5b37-0552-3ad5-b5770f9e7707",
        "bfa8f2eb-d552-d745-f887-9488c0df3550",
        "442d07a7-c8d9-7ac4-4bb2-b27d4ce885fe",
        "7ae15331-2af8-e0a4-b801-f9887464d7d0",
        "c5e80cfc-76c7-b34c-3c74-0a5718c11034",
        "84fc1b1b-3403-8d3c-a0f3-9969e459dc31",
        "674e554f-e4e5-27f3-3839-f4fb6bbb5f94",
        "28a2d4b7-de46-e870-9400-4f370c4ebdbc",
        "620969d4-745f-69b5-9683-5ca1eea38232",
        "d8de7296-cc41-a5b9-de08-b1561fdff17e",
        "4a74fa99-1a83-7675-7eb0-45d2e1397069",
        "130e9887-d850-0b04-b656-da4d340fa7b8",
        "85f65bb7-47f6-58a9-c063-9ca904499404",
        "b03abb3b-46b2-c1b5-357f-f4c1f107b374",
        "97c2a264-f763-d643-e551-96c9edaf9a40",
        "6c66299a-c502-2d43-035e-cf1234191fc4",
        "18e05cbe-5efd-16a6-12d5-1c50b94d587d",
        "ec7f6672-c6ca-0491-91e0-deb37799b063"
    };

    private static readonly string[] RowLabels =
    {
        "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"
    };

    private static readonly string[] ColumnLabels =
    {
        "Petit-déjeuner", "Déjeuner", "Collation", "Souper"
    };

    [Fact]
    public async Task Live_qwen3_measures_frozen_phase2_baseline_a_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PHASE2_CANDIDATE_AUDIT_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_PHASE2_CANDIDATE_AUDIT_BENCHMARK=1 to run EXP-019.");
            return;
        }

        var configuredVariant = Environment.GetEnvironmentVariable(
            "SAAIA_PHASE2_AUDIT_VARIANT")?.Trim();
        var usePostObservationRoleReview = string.Equals(
            configuredVariant,
            "B0",
            StringComparison.OrdinalIgnoreCase);
        var useCompatibilityMissionContext = string.Equals(
            configuredVariant,
            "D0",
            StringComparison.OrdinalIgnoreCase);
        var useSingleCandidateNamedRoles = string.Equals(
            configuredVariant,
            "E0",
            StringComparison.OrdinalIgnoreCase);
        var usePairwiseBinaryReference = string.Equals(
            configuredVariant,
            "F0",
            StringComparison.OrdinalIgnoreCase);
        var usePairwiseLlmBoundaries = string.Equals(
            configuredVariant,
            "G0",
            StringComparison.OrdinalIgnoreCase);
        var useRoleGenerationRepeatability = string.Equals(
            configuredVariant,
            "H0",
            StringComparison.OrdinalIgnoreCase);
        var useSingleCandidateIdentity = string.Equals(
            configuredVariant,
            "I0",
            StringComparison.OrdinalIgnoreCase);
        var useHierarchicalLabelResolution = string.Equals(
            configuredVariant,
            "J0",
            StringComparison.OrdinalIgnoreCase);
        var useQuantizationIdentity = string.Equals(
            configuredVariant,
            "K0",
            StringComparison.OrdinalIgnoreCase);
        var useLargerModelIdentity = string.Equals(
            configuredVariant,
            "L0",
            StringComparison.OrdinalIgnoreCase);
        var useAssignmentWithoutIdentityGate = string.Equals(
            configuredVariant,
            "M0",
            StringComparison.OrdinalIgnoreCase);
        var usePostObservationCandidateDefinition =
            useCompatibilityMissionContext
            || string.Equals(
                configuredVariant,
                "C0",
                StringComparison.OrdinalIgnoreCase);
        var experiment = usePostObservationRoleReview
            ? "EXP-020"
            : useAssignmentWithoutIdentityGate
                ? "EXP-031"
            : useLargerModelIdentity
                ? "EXP-030"
            : useQuantizationIdentity
                ? "EXP-029"
            : useHierarchicalLabelResolution
                ? "EXP-028"
            : useSingleCandidateIdentity
                ? "EXP-027"
            : useRoleGenerationRepeatability
                ? "EXP-026"
            : usePairwiseLlmBoundaries
                ? "EXP-025"
            : usePairwiseBinaryReference
                ? "EXP-024"
            : useSingleCandidateNamedRoles
                ? "EXP-023"
            : useCompatibilityMissionContext
                ? "EXP-022"
                : usePostObservationCandidateDefinition
                ? "EXP-021"
                : "EXP-019";
        var variant = usePostObservationRoleReview
            ? "B.0"
            : useAssignmentWithoutIdentityGate
                ? "M.0"
            : useLargerModelIdentity
                ? "L.0"
            : useQuantizationIdentity
                ? "K.0"
            : useHierarchicalLabelResolution
                ? "J.0"
            : useSingleCandidateIdentity
                ? "I.0"
            : useRoleGenerationRepeatability
                ? "H.0"
            : usePairwiseLlmBoundaries
                ? "G.0"
            : usePairwiseBinaryReference
                ? "F.0"
            : useSingleCandidateNamedRoles
                ? "E.0"
            : useCompatibilityMissionContext
                ? "D.0"
                : usePostObservationCandidateDefinition
                ? "C.0"
                : "A";
        var candidateRotation = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_PHASE2_CANDIDATE_ROTATION"),
            out var configuredRotation)
                ? Math.Clamp(configuredRotation, 0, 19)
                : 0;
        var roleRotation = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_PHASE2_ROLE_ROTATION"),
            out var configuredRoleRotation)
                ? Math.Clamp(configuredRoleRotation, 0, 3)
                : 0;
        var pairRotation = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_PHASE2_PAIR_ROTATION"),
            out var configuredPairRotation)
                ? Math.Clamp(
                    configuredPairRotation,
                    0,
                    useAssignmentWithoutIdentityGate ? 79 : 23)
                : 0;

        var settings = AppSettings.Load();
        var backendUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
            settings.BackendUrl);
        var apiKey = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
            SecureLocalStore.GetServerApiKey());
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(backendUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "EXP-019 requires backend, API key and local LLM settings.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var total = Stopwatch.StartNew();
        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var raw = await api.DocumentsContentCardsAsync(
            "Cuisine",
            null,
            null,
            null,
            null,
            "representative",
            20,
            0,
            cts.Token);
        var rawCandidates = ReadRawCandidates(raw);
        var snapshotMatches = rawCandidates
            .Select(static candidate => candidate.ContentCardId)
            .SequenceEqual(ExpectedContentCardIds, StringComparer.OrdinalIgnoreCase);

        var artifactPath = ResolveArtifactPath(experiment, variant);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        if (!snapshotMatches)
        {
            await WriteJsonAsync(
                artifactPath,
                new
                {
                    experiment,
                    variant,
                    approval = "TESTE_NON_APPROUVE",
                    comparability = "INCOMPARABLE_CORPUS_DRIFT",
                    expectedContentCardIds = ExpectedContentCardIds,
                    observedCandidates = rawCandidates,
                    totalElapsedMilliseconds = total.ElapsedMilliseconds
                });
            output.WriteLine("Artifact: " + artifactPath);
            Assert.True(snapshotMatches,
                "The representative/20 corpus differs from the frozen EXP-019 snapshot. "
                + "The drift was archived and no LLM audit was executed.");
        }

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.content_cards",
            Result = raw.Clone(),
            DurationMs = 0
        });
        var originalBundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            FrozenQuestion);
        Assert.Equal(rawCandidates.Count, originalBundle.Items.Count);
        var candidateInputs = originalBundle.Items
            .Select((candidate, index) =>
            {
                var hints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["phase2FrozenContentCardId"] =
                        rawCandidates[index].ContentCardId
                };
                if (useHierarchicalLabelResolution)
                    hints.Remove("sourceAnchorLabel");
                else
                    hints["sourceAnchorLabel"] = rawCandidates[index].Title;
                return new CandidateInput(
                    candidate with { SelectionHints = hints },
                    rawCandidates[index]);
            })
            .ToArray();
        var rotatedInputs = candidateInputs
            .Skip(candidateRotation)
            .Concat(candidateInputs.Take(candidateRotation))
            .ToArray();
        var candidates = rotatedInputs
            .Select(static input => input.Candidate)
            .ToArray();
        var auditRawCandidates = rotatedInputs
            .Select(static input => input.Raw)
            .ToArray();

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var recordingLlm = new RecordingLlm(innerLlm);
        var runner = new SourceBackedAgentV2Runner(
            recordingLlm,
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 4096,
                MaximumSemanticCandidatesPerAuditTurn: 20,
                MaximumSemanticCandidateAuditConcurrency: 1,
                SemanticCandidateLabelResolutionEnabled: true,
                MaximumSemanticCandidatesPerAuditBatch:
                    useSingleCandidateIdentity
                    || useHierarchicalLabelResolution
                    || useQuantizationIdentity
                    || useLargerModelIdentity
                        ? 1
                        : 6));

        if (useRoleGenerationRepeatability)
        {
            var roleWall = Stopwatch.StartNew();
            var repeatRolePreparation = await InvokePrivateAsync(
                runner,
                "PrepareSemanticColumnRolesAsync",
                BuildMealPlanIntake(),
                FrozenMission,
                CreateSemanticLayoutDimensions(5, 4),
                ColumnLabels.ToDictionary(
                    static label => label,
                    static label => label,
                    StringComparer.OrdinalIgnoreCase),
                cts.Token,
                true);
            roleWall.Stop();
            total.Stop();
            var roles = ReadProperty<IReadOnlyDictionary<string, string>>(
                repeatRolePreparation,
                "Roles");
            var outcome = ReadOptionalProperty(repeatRolePreparation, "Outcome");
            Assert.NotNull(outcome);
            var roleCalls = recordingLlm.Calls
                .Where(static call => call.Phase == "role-definition")
                .ToArray();
            var normalizedHash = HashRoleDefinitions(roles);
            var frozenB0Hash = HashRoleDefinitions(FrozenB0RoleDefinitions);
            var exactMatchToFrozenB0 = ColumnLabels.All(label =>
                roles.TryGetValue(label, out var definition)
                && string.Equals(
                    definition,
                    FrozenB0RoleDefinitions[label],
                    StringComparison.Ordinal));
            var h0Report = new
            {
                experiment,
                variant,
                generatedAt = DateTimeOffset.Now,
                approval = "TESTE_NON_APPROUVE",
                comparability = "FROZEN_INPUT_AND_CORPUS_MATCH",
                configuration = new
                {
                    backendUrl,
                    model,
                    scope = "Cuisine",
                    inventoryMode = "representative",
                    limit = 20,
                    offset = 0,
                    question = FrozenQuestion,
                    mission = FrozenMission,
                    rows = 5,
                    columns = 4,
                    inputRoles = ColumnLabels.ToDictionary(
                        static label => label,
                        static label => label,
                        StringComparer.OrdinalIgnoreCase),
                    sourceRun = "EXP-020/B.0"
                },
                snapshot = new
                {
                    matches = snapshotMatches,
                    expectedContentCardIds = ExpectedContentCardIds
                },
                roleDefinition = new
                {
                    protocolValid = ReadProperty<bool>(outcome!, "ProtocolValid"),
                    failureReason = ReadProperty<string>(outcome!, "FailureReason"),
                    attempts = ReadProperty<int>(outcome!, "Attempts"),
                    roles,
                    normalizedHash,
                    frozenB0Hash,
                    exactMatchToFrozenB0
                },
                counts = new
                {
                    recordedCalls = recordingLlm.Calls.Count,
                    roleDefinitionCalls = roleCalls.Length,
                    otherCalls = recordingLlm.Calls.Count - roleCalls.Length
                },
                timing = new
                {
                    roleWallMilliseconds = roleWall.ElapsedMilliseconds,
                    sumRecordedCallMilliseconds = recordingLlm.Calls.Sum(
                        static call => call.ElapsedMilliseconds),
                    totalIncludingBackendMilliseconds = total.ElapsedMilliseconds
                },
                calls = recordingLlm.Calls,
                humanInspection = new
                {
                    status = "A_FAIRE",
                    instruction =
                        "Comparer texte, hash et sens a EXP-020/B.0 puis a l'autre repetition H.0."
                }
            };
            await WriteJsonAsync(artifactPath, h0Report);
            output.WriteLine("Artifact: " + artifactPath);
            output.WriteLine(
                $"H.0 role call: {roleCalls.Length}; hash: {normalizedHash}; "
                + $"exact B.0: {exactMatchToFrozenB0}");
            Assert.True(snapshotMatches);
            Assert.True(ReadProperty<bool>(outcome!, "ProtocolValid"));
            Assert.Single(roleCalls);
            Assert.Equal(recordingLlm.Calls.Count, roleCalls.Length);
            Assert.Equal(ColumnLabels, roles.Keys);
            Assert.All(roles, pair =>
                Assert.False(string.Equals(
                    pair.Key,
                    pair.Value,
                    StringComparison.OrdinalIgnoreCase)));
            return;
        }

        if (useSingleCandidateIdentity
            || useHierarchicalLabelResolution
            || useQuantizationIdentity
            || useLargerModelIdentity)
        {
            var i0AuditWall = Stopwatch.StartNew();
            var i0Execution = await InvokePrivateAsync(
                runner,
                "CompleteBatchedCandidateAuditAsync",
                BuildMealPlanIntake(),
                FrozenMission,
                FrozenC0CandidateObjectType,
                FrozenC0CandidateEligibilityRule,
                "Jour",
                RowLabels,
                Array.Empty<string>(),
                candidates,
                cts.Token);
            i0AuditWall.Stop();
            total.Stop();

            var i0Completion = ReadProperty<SourceBackedAgentCompletion>(
                i0Execution,
                "Completion");
            var i0Decision = ReadProperty<object>(i0Execution, "Decision");
            var i0Approvals = ReadObjects(i0Decision, "ApprovedCandidates")
                .ToDictionary(
                    item => ReadProperty<string>(item, "EvidenceId"),
                    item => ReadProperty<string>(item, "DisplayValue"),
                    StringComparer.OrdinalIgnoreCase);
            var i0ClassificationObjects = ReadObjects(
                i0Decision,
                "Classifications");
            var i0Classifications = i0ClassificationObjects
                .ToDictionary(
                    item => ReadProperty<string>(item, "EvidenceId"),
                    item => ReadProperty<string>(item, "Classification"),
                    StringComparer.OrdinalIgnoreCase);
            var i0ResolvedDisplayValues = i0ClassificationObjects
                .ToDictionary(
                    item => ReadProperty<string>(item, "EvidenceId"),
                    item => ReadProperty<string>(item, "DisplayValue"),
                    StringComparer.OrdinalIgnoreCase);
            var i0CandidateDecisions = candidates
                .Select((candidate, index) => new CandidateObservation(
                    index + 1,
                    candidate.EvidenceId,
                    auditRawCandidates[index].ContentCardId,
                    auditRawCandidates[index].Title,
                    auditRawCandidates[index].DocPath,
                    auditRawCandidates[index].PageStart,
                    auditRawCandidates[index].CardIndex,
                    i0Approvals.ContainsKey(candidate.EvidenceId)
                        ? "ACCEPT"
                        : "REJECT",
                    i0Classifications.GetValueOrDefault(
                        candidate.EvidenceId,
                        "unclassified"),
                    i0Approvals.GetValueOrDefault(candidate.EvidenceId)
                    ?? i0ResolvedDisplayValues.GetValueOrDefault(
                        candidate.EvidenceId),
                    Array.Empty<string>()))
                .ToArray();
            var i0IdentityCalls = recordingLlm.Calls
                .Where(static call => call.Phase == "identity")
                .ToArray();
            var i0CompatibilityCalls = recordingLlm.Calls
                .Where(static call => call.Phase == "compatibility")
                .ToArray();
            var i0LabelResolutionCalls = recordingLlm.Calls
                .Where(static call => call.Phase == "label-resolution")
                .ToArray();
            var i0Report = new
            {
                experiment,
                variant,
                generatedAt = DateTimeOffset.Now,
                approval = "TESTE_NON_APPROUVE",
                comparability = useHierarchicalLabelResolution
                    ? "FROZEN_CORPUS_C0_DEFINITION_AND_HIERARCHY_MATCH"
                    : "FROZEN_CORPUS_AND_C0_DEFINITION_MATCH",
                configuration = new
                {
                    backendUrl,
                    model,
                    scope = "Cuisine",
                    inventoryMode = "representative",
                    limit = 20,
                    offset = 0,
                    candidateRotation,
                    maximumCandidatesPerAuditBatch = 1,
                    hierarchicalLabelResolutionEnabled =
                        useHierarchicalLabelResolution,
                    sourceAnchorPreResolved =
                        !useHierarchicalLabelResolution,
                    candidateObjectType = FrozenC0CandidateObjectType,
                    candidateEligibilityRule = FrozenC0CandidateEligibilityRule,
                    rowHeader = "Jour",
                    rowLabels = RowLabels,
                    columnLabels = Array.Empty<string>()
                },
                snapshot = new
                {
                    matches = snapshotMatches,
                    expectedContentCardIds = ExpectedContentCardIds,
                    observedCandidates = rawCandidates
                },
                protocol = new
                {
                    valid = ReadProperty<bool>(i0Decision, "ProtocolValid"),
                    failureReason = ReadNullableProperty<string>(
                        i0Decision,
                        "FailureReason"),
                    columnCompatibilityApplied = ReadProperty<bool>(
                        i0Execution,
                        "ColumnCompatibilityApplied"),
                    columnCompatibilityProtocolValid = ReadProperty<bool>(
                        i0Execution,
                        "ColumnCompatibilityProtocolValid")
                },
                counts = new
                {
                    candidates = candidates.Length,
                    approved = i0Approvals.Count,
                    rejected = candidates.Length - i0Approvals.Count,
                    recordedCalls = recordingLlm.Calls.Count,
                    labelResolutionCalls = i0LabelResolutionCalls.Length,
                    identityCalls = i0IdentityCalls.Length,
                    compatibilityCalls = i0CompatibilityCalls.Length,
                    candidateDecisionCount = ReadProperty<int>(
                        i0Execution,
                        "CandidateDecisionCount"),
                    protocolRepairCount = ReadProperty<int>(
                        i0Execution,
                        "ProtocolRepairCount"),
                    executedIdentityBatchCount = ReadProperty<int>(
                        i0Execution,
                        "ExecutedBatchCount"),
                    largestExecutedIdentityBatchCandidateCount = ReadProperty<int>(
                        i0Execution,
                        "LargestExecutedBatchCandidateCount")
                },
                timing = new
                {
                    identityElapsedMilliseconds = ReadProperty<long>(
                        i0Execution,
                        "ElapsedMilliseconds"),
                    auditWallMilliseconds = i0AuditWall.ElapsedMilliseconds,
                    sumRecordedCallMilliseconds = recordingLlm.Calls.Sum(
                        static call => call.ElapsedMilliseconds),
                    totalIncludingBackendMilliseconds = total.ElapsedMilliseconds
                },
                calls = recordingLlm.Calls,
                candidateDecisions = i0CandidateDecisions,
                aggregateCompletion = i0Completion.Content,
                humanInspection = new
                {
                    status = "A_FAIRE",
                    instruction =
                        "Verifier les 20 libelles resolus et identites par contentCardId avant toute rotation."
                }
            };
            await WriteJsonAsync(artifactPath, i0Report);
            output.WriteLine("Artifact: " + artifactPath);
            output.WriteLine(
                $"{variant} resolution calls: {i0LabelResolutionCalls.Length}; "
                + $"identity calls: {i0IdentityCalls.Length}; approved: "
                + $"{i0Approvals.Count}/{candidates.Length}");
            Assert.True(snapshotMatches);
            Assert.True(
                ReadProperty<bool>(i0Decision, "ProtocolValid"),
                i0Completion.Content);
            Assert.False(ReadProperty<bool>(
                i0Execution,
                "ColumnCompatibilityApplied"));
            Assert.Equal(20, i0IdentityCalls.Length);
            Assert.Empty(i0CompatibilityCalls);
            if (useHierarchicalLabelResolution)
                Assert.NotEmpty(i0LabelResolutionCalls);
            else
                Assert.Empty(i0LabelResolutionCalls);
            Assert.Equal(20, ReadProperty<int>(
                i0Execution,
                "CandidateDecisionCount"));
            Assert.Equal(20, ReadProperty<int>(
                i0Execution,
                "ExecutedBatchCount"));
            Assert.Equal(1, ReadProperty<int>(
                i0Execution,
                "LargestExecutedBatchCandidateCount"));
            Assert.Equal(0, ReadProperty<int>(
                i0Execution,
                "ProtocolRepairCount"));
            return;
        }

        if (usePairwiseBinaryReference
            || usePairwiseLlmBoundaries
            || useAssignmentWithoutIdentityGate)
        {
            var roleWall = Stopwatch.StartNew();
            object? pairwiseRolePreparation = null;
            object? pairwiseRoleOutcome = null;
            IReadOnlyDictionary<string, string> pairwiseRoleDefinitions;
            string roleDefinitionSource;
            if (useAssignmentWithoutIdentityGate)
            {
                pairwiseRolePreparation = await InvokePrivateAsync(
                    runner,
                    "PrepareSemanticColumnRolesAsync",
                    BuildMealPlanIntake(),
                    FrozenMission,
                    CreateSemanticLayoutDimensions(5, 4),
                    ColumnLabels.ToDictionary(
                        static label => label,
                        static label => label,
                        StringComparer.OrdinalIgnoreCase),
                    cts.Token,
                    true);
                pairwiseRoleDefinitions =
                    ReadProperty<IReadOnlyDictionary<string, string>>(
                        pairwiseRolePreparation,
                        "Roles");
                pairwiseRoleOutcome = ReadOptionalProperty(
                    pairwiseRolePreparation,
                    "Outcome");
                roleDefinitionSource = "regenerated_exp031_m0";
            }
            else
            {
                pairwiseRoleDefinitions = usePairwiseLlmBoundaries
                    ? FrozenB0RoleDefinitions
                    : ColumnLabels.ToDictionary(
                        static label => label,
                        static label => label,
                        StringComparer.OrdinalIgnoreCase);
                roleDefinitionSource = usePairwiseLlmBoundaries
                    ? "frozen_exp020_b0"
                    : "label_only";
            }
            roleWall.Stop();

            var pairwiseInputs = useAssignmentWithoutIdentityGate
                ? candidateInputs
                : candidateInputs
                    .Where(input => FrozenC0ApprovedContentCardIds.Contains(
                        input.Raw.ContentCardId,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            Assert.Equal(
                useAssignmentWithoutIdentityGate
                    ? ExpectedContentCardIds.Length
                    : FrozenC0ApprovedContentCardIds.Length,
                pairwiseInputs.Length);
            var pairs = pairwiseInputs
                .SelectMany(input => ColumnLabels.Select(role =>
                    new CandidateRolePair(input, role)))
                .ToArray();
            var rotatedPairs = pairs
                .Skip(pairRotation)
                .Concat(pairs.Take(pairRotation))
                .ToArray();
            var compatibilityWall = Stopwatch.StartNew();
            var pairDecisions = new List<CandidateRolePairDecision>();
            foreach (var pair in rotatedPairs)
            {
                var f0Completion = await recordingLlm.CompleteStructuredAsync(
                    BuildCandidateRolePairMessages(
                        pair,
                        pairwiseRoleDefinitions[pair.Role]),
                    BuildCandidateRolePairContract(),
                    128,
                    cts.Token,
                    temperatureOverride: 0);
                pairDecisions.Add(ReadCandidateRolePairDecision(
                    pair,
                    f0Completion));
            }
            compatibilityWall.Stop();
            total.Stop();
            var f0Report = new
            {
                experiment,
                variant,
                generatedAt = DateTimeOffset.Now,
                approval = "TESTE_NON_APPROUVE",
                comparability = useAssignmentWithoutIdentityGate
                    ? "FROZEN_CORPUS_H0_ROLES_AND_NO_IDENTITY_GATE_MATCH"
                    : "FROZEN_CORPUS_AND_C0_APPROVALS_MATCH",
                configuration = new
                {
                    backendUrl,
                    model,
                    scope = "Cuisine",
                    inventoryMode = "representative",
                    limit = 20,
                    offset = 0,
                    pairRotation,
                    candidateObjectType = FrozenC0CandidateObjectType,
                    candidateEligibilityRule = FrozenC0CandidateEligibilityRule,
                    roleDefinitions = pairwiseRoleDefinitions,
                    roleDefinitionSource,
                    roleDefinitionHash = HashRoleDefinitions(
                        pairwiseRoleDefinitions),
                    frozenApprovalSource = useAssignmentWithoutIdentityGate
                        ? null
                        : "EXP-021/C.0",
                    identityGateApplied = false
                },
                snapshot = new
                {
                    matches = snapshotMatches,
                    expectedContentCardIds = ExpectedContentCardIds,
                    frozenApprovedContentCardIds = useAssignmentWithoutIdentityGate
                        ? null
                        : FrozenC0ApprovedContentCardIds
                },
                roleGeneration = new
                {
                    executed = useAssignmentWithoutIdentityGate,
                    protocolValid = pairwiseRoleOutcome is null
                        ? (bool?)null
                        : ReadProperty<bool>(
                            pairwiseRoleOutcome,
                            "ProtocolValid"),
                    failureReason = pairwiseRoleOutcome is null
                        ? null
                        : ReadNullableProperty<string>(
                            pairwiseRoleOutcome,
                            "FailureReason"),
                    attempts = pairwiseRoleOutcome is null
                        ? (int?)null
                        : ReadProperty<int>(
                            pairwiseRoleOutcome,
                            "Attempts"),
                    exactMatchToH0 = useAssignmentWithoutIdentityGate
                        && ColumnLabels.All(label =>
                            pairwiseRoleDefinitions.TryGetValue(
                                label,
                                out var definition)
                            && string.Equals(
                                definition,
                                FrozenB0RoleDefinitions[label],
                                StringComparison.Ordinal))
                },
                counts = new
                {
                    candidates = pairwiseInputs.Length,
                    roles = ColumnLabels.Length,
                    pairs = pairDecisions.Count,
                    calls = recordingLlm.Calls.Count,
                    roleDefinitionCalls = recordingLlm.Calls.Count(
                        static call => call.Phase == "role-definition"),
                    identityCalls = recordingLlm.Calls.Count(
                        static call => call.Phase == "identity"),
                    compatibilityCalls = recordingLlm.Calls.Count(
                        static call => call.Phase == "compatibility"),
                    protocolValid = pairDecisions.Count(
                        static decision => decision.ProtocolValid),
                    protocolInvalid = pairDecisions.Count(
                        static decision => !decision.ProtocolValid),
                    compatible = pairDecisions.Count(
                        static decision => decision.Compatible)
                },
                timing = new
                {
                    roleWallMilliseconds = useAssignmentWithoutIdentityGate
                        ? roleWall.ElapsedMilliseconds
                        : 0,
                    compatibilityWallMilliseconds =
                        compatibilityWall.ElapsedMilliseconds,
                    sumRecordedCallMilliseconds = recordingLlm.Calls.Sum(
                        static call => call.ElapsedMilliseconds),
                    totalIncludingBackendMilliseconds = total.ElapsedMilliseconds
                },
                calls = recordingLlm.Calls,
                pairDecisions,
                humanInspection = new
                {
                    status = "A_FAIRE",
                    instruction = useAssignmentWithoutIdentityGate
                        ? "Verifier les cinq controles positifs, les quatre pieges et chaque autre titre avant toute rotation."
                        : "Comparer chaque booléen par contentCardId+role sur les trois ordres."
                }
            };
            await WriteJsonAsync(artifactPath, f0Report);
            output.WriteLine("Artifact: " + artifactPath);
            output.WriteLine(
                $"{variant} calls: {recordingLlm.Calls.Count}; compatible: "
                + pairDecisions.Count(static decision => decision.Compatible));
            Assert.Equal(
                useAssignmentWithoutIdentityGate ? 81 : 24,
                recordingLlm.Calls.Count);
            Assert.All(pairDecisions, static decision =>
                Assert.True(decision.ProtocolValid, decision.FailureReason));
            if (useAssignmentWithoutIdentityGate)
            {
                Assert.NotNull(pairwiseRolePreparation);
                Assert.NotNull(pairwiseRoleOutcome);
                Assert.True(ReadProperty<bool>(
                    pairwiseRoleOutcome!,
                    "ProtocolValid"));
                Assert.Equal(
                    HashRoleDefinitions(FrozenB0RoleDefinitions),
                    HashRoleDefinitions(pairwiseRoleDefinitions));
                Assert.Single(
                    recordingLlm.Calls,
                    static call => call.Phase == "role-definition");
                Assert.DoesNotContain(
                    recordingLlm.Calls,
                    static call => call.Phase == "identity");
                Assert.Equal(80, recordingLlm.Calls.Count(
                    static call => call.Phase == "compatibility"));
            }
            return;
        }

        if (useSingleCandidateNamedRoles)
        {
            var frozenIds = FrozenC0ApprovedContentCardIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var frozenInputs = candidateInputs
                .Where(input => frozenIds.Contains(input.Raw.ContentCardId))
                .ToArray();
            Assert.Equal(FrozenC0ApprovedContentCardIds.Length, frozenInputs.Length);
            var rotatedRoles = ColumnLabels
                .Skip(roleRotation)
                .Concat(ColumnLabels.Take(roleRotation))
                .ToArray();
            var compatibilityWall = Stopwatch.StartNew();
            var singleCandidateDecisions = new List<SingleCandidateRoleDecision>();
            foreach (var input in frozenInputs)
            {
                var e0Completion = await recordingLlm.CompleteStructuredAsync(
                    BuildSingleCandidateRoleMessages(
                        input.Candidate.EvidenceId,
                        input.Raw.Title,
                        rotatedRoles),
                    BuildSingleCandidateRoleContract(rotatedRoles),
                    192,
                    cts.Token,
                    temperatureOverride: 0);
                singleCandidateDecisions.Add(ReadSingleCandidateRoleDecision(
                    input,
                    e0Completion,
                    ColumnLabels));
            }
            compatibilityWall.Stop();
            total.Stop();
            var e0Report = new
            {
                experiment,
                variant,
                generatedAt = DateTimeOffset.Now,
                approval = "TESTE_NON_APPROUVE",
                comparability = "FROZEN_CORPUS_AND_C0_APPROVALS_MATCH",
                configuration = new
                {
                    backendUrl,
                    model,
                    scope = "Cuisine",
                    inventoryMode = "representative",
                    limit = 20,
                    offset = 0,
                    roleRotation,
                    roleOrder = rotatedRoles,
                    candidateObjectType = FrozenC0CandidateObjectType,
                    candidateEligibilityRule = FrozenC0CandidateEligibilityRule,
                    frozenApprovalSource = "EXP-021/C.0"
                },
                snapshot = new
                {
                    matches = snapshotMatches,
                    expectedContentCardIds = ExpectedContentCardIds,
                    frozenApprovedContentCardIds = FrozenC0ApprovedContentCardIds
                },
                counts = new
                {
                    candidates = frozenInputs.Length,
                    calls = recordingLlm.Calls.Count,
                    protocolValid = singleCandidateDecisions.Count(
                        static decision => decision.ProtocolValid),
                    protocolInvalid = singleCandidateDecisions.Count(
                        static decision => !decision.ProtocolValid),
                    compatiblePairs = singleCandidateDecisions.Sum(
                        static decision => decision.CompatibleColumnLabels.Count)
                },
                timing = new
                {
                    compatibilityWallMilliseconds =
                        compatibilityWall.ElapsedMilliseconds,
                    sumRecordedCallMilliseconds = recordingLlm.Calls.Sum(
                        static call => call.ElapsedMilliseconds),
                    totalIncludingBackendMilliseconds = total.ElapsedMilliseconds
                },
                calls = recordingLlm.Calls,
                candidateDecisions = singleCandidateDecisions,
                humanInspection = new
                {
                    status = "A_FAIRE",
                    instruction =
                        "Comparer les labels par contentCardId sur les rotations 0/1/2."
                }
            };
            await WriteJsonAsync(artifactPath, e0Report);
            output.WriteLine("Artifact: " + artifactPath);
            output.WriteLine(
                $"E.0 calls: {recordingLlm.Calls.Count}; pairs: "
                + singleCandidateDecisions.Sum(static decision =>
                    decision.CompatibleColumnLabels.Count));
            Assert.Equal(6, recordingLlm.Calls.Count);
            Assert.All(singleCandidateDecisions, static decision =>
                Assert.True(decision.ProtocolValid, decision.FailureReason));
            return;
        }

        var effectiveIntake = BuildMealPlanIntake();
        IReadOnlyDictionary<string, string> effectiveRoles =
            ColumnLabels.ToDictionary(
                static label => label,
                static label => label,
                StringComparer.OrdinalIgnoreCase);
        object? rolePreparation = null;
        object? roleOutcome = null;
        if (usePostObservationRoleReview)
        {
            rolePreparation = await InvokePrivateAsync(
                runner,
                "PrepareSemanticColumnRolesAsync",
                effectiveIntake,
                FrozenMission,
                CreateSemanticLayoutDimensions(5, 4),
                effectiveRoles,
                cts.Token,
                true);
            effectiveIntake = ReadProperty<SourceBackedIntake>(
                rolePreparation,
                "Intake");
            effectiveRoles = ReadProperty<IReadOnlyDictionary<string, string>>(
                rolePreparation,
                "Roles");
            roleOutcome = ReadOptionalProperty(rolePreparation, "Outcome");
        }

        var candidateObjectType = "repas";
        var candidateEligibilityRule = string.Empty;
        object? candidateDefinitionOutcome = null;
        if (usePostObservationCandidateDefinition)
        {
            candidateDefinitionOutcome = await InvokePrivateAsync(
                runner,
                "ReviewSemanticCandidateDefinitionAsync",
                effectiveIntake,
                FrozenMission,
                "Jour",
                RowLabels,
                effectiveRoles,
                cts.Token);
            if (ReadProperty<bool>(candidateDefinitionOutcome, "ProtocolValid"))
            {
                candidateObjectType = ReadProperty<string>(
                    candidateDefinitionOutcome,
                    "CandidateObjectType");
                candidateEligibilityRule = ReadProperty<string>(
                    candidateDefinitionOutcome,
                    "CandidateEligibilityRule");
            }
        }
        var observedCandidateObjectType = candidateObjectType;
        var observedCandidateEligibilityRule = candidateEligibilityRule;
        if (useCompatibilityMissionContext)
        {
            candidateObjectType = FrozenC0CandidateObjectType;
            candidateEligibilityRule = FrozenC0CandidateEligibilityRule;
        }
        if (useCompatibilityMissionContext)
        {
            recordingLlm.CompatibilityMissionContext = new(
                FrozenQuestion,
                candidateObjectType,
                candidateEligibilityRule);
        }

        var auditWall = Stopwatch.StartNew();
        var execution = await InvokePrivateAsync(
            runner,
            "CompleteBatchedCandidateAuditAsync",
            effectiveIntake,
            FrozenMission,
            candidateObjectType,
            candidateEligibilityRule,
            "Jour",
            RowLabels,
            ColumnLabels,
            candidates,
            cts.Token);
        auditWall.Stop();
        total.Stop();

        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var decision = ReadProperty<object>(execution, "Decision");
        var approvalObjects = ReadObjects(decision, "ApprovedCandidates");
        var approvals = approvalObjects.ToDictionary(
            item => ReadProperty<string>(item, "EvidenceId"),
            item => new ApprovalObservation(
                ReadProperty<string>(item, "EvidenceId"),
                ReadProperty<string>(item, "DisplayValue"),
                ReadStringList(item, "CompatibleColumnLabels")),
            StringComparer.OrdinalIgnoreCase);
        var classifications = ReadObjects(decision, "Classifications")
            .ToDictionary(
                item => ReadProperty<string>(item, "EvidenceId"),
                item => ReadProperty<string>(item, "Classification"),
                StringComparer.OrdinalIgnoreCase);
        var candidateDecisions = candidates
            .Select((candidate, index) => new CandidateObservation(
                index + 1,
                candidate.EvidenceId,
                auditRawCandidates[index].ContentCardId,
                auditRawCandidates[index].Title,
                auditRawCandidates[index].DocPath,
                auditRawCandidates[index].PageStart,
                auditRawCandidates[index].CardIndex,
                approvals.ContainsKey(candidate.EvidenceId)
                    ? "ACCEPT"
                    : "REJECT",
                classifications.GetValueOrDefault(
                    candidate.EvidenceId,
                    "unclassified"),
                approvals.GetValueOrDefault(candidate.EvidenceId)?.DisplayValue,
                approvals.GetValueOrDefault(candidate.EvidenceId)
                    ?.CompatibleColumnLabels
                    ?? Array.Empty<string>()))
            .ToArray();

        var identityCalls = recordingLlm.Calls
            .Where(static call => call.Phase == "identity")
            .ToArray();
        var compatibilityCalls = recordingLlm.Calls
            .Where(static call => call.Phase == "compatibility")
            .ToArray();
        var roleDefinitionCalls = recordingLlm.Calls
            .Where(static call => call.Phase == "role-definition")
            .ToArray();
        var candidateDefinitionCalls = recordingLlm.Calls
            .Where(static call => call.Phase == "candidate-definition")
            .ToArray();
        var identityElapsed = ReadProperty<long>(execution, "ElapsedMilliseconds");
        var compatibilityElapsed = ReadProperty<long>(
            execution,
            "ColumnCompatibilityElapsedMilliseconds");
        var report = new
        {
            experiment,
            variant,
            generatedAt = DateTimeOffset.Now,
            approval = "TESTE_NON_APPROUVE",
            comparability = "FROZEN_CORPUS_MATCH",
            configuration = new
            {
                backendUrl,
                model,
                scope = "Cuisine",
                inventoryMode = "representative",
                limit = 20,
                offset = 0,
                candidateRotation,
                maximumContextTokens = 4096,
                auditBatchSize = 6,
                candidateObjectType,
                candidateEligibilityRule,
                compatibilityMissionContextInjected =
                    useCompatibilityMissionContext,
                rowHeader = "Jour",
                rowLabels = RowLabels,
                columnLabels = ColumnLabels,
                canonicalColumnSemanticRoles = effectiveRoles
            },
            snapshot = new
            {
                matches = snapshotMatches,
                expectedContentCardIds = ExpectedContentCardIds,
                observedCandidates = rawCandidates
            },
            protocol = new
            {
                valid = ReadProperty<bool>(decision, "ProtocolValid"),
                failureReason = ReadNullableProperty<string>(decision, "FailureReason"),
                columnCompatibilityApplied = ReadProperty<bool>(
                    execution,
                    "ColumnCompatibilityApplied"),
                columnCompatibilityProtocolValid = ReadProperty<bool>(
                    execution,
                    "ColumnCompatibilityProtocolValid"),
                columnCompatibilityFailureReason = ReadNullableProperty<string>(
                    execution,
                    "ColumnCompatibilityFailureReason")
            },
            roleDefinition = new
            {
                requested = usePostObservationRoleReview,
                protocolValid = roleOutcome is null
                    ? (bool?)null
                    : ReadProperty<bool>(roleOutcome, "ProtocolValid"),
                failureReason = roleOutcome is null
                    ? null
                    : ReadProperty<string>(roleOutcome, "FailureReason"),
                attempts = roleOutcome is null
                    ? (int?)null
                    : ReadProperty<int>(roleOutcome, "Attempts"),
                roles = effectiveRoles
            },
            candidateDefinition = new
            {
                requested = usePostObservationCandidateDefinition,
                protocolValid = candidateDefinitionOutcome is null
                    ? (bool?)null
                    : ReadProperty<bool>(candidateDefinitionOutcome, "ProtocolValid"),
                failureReason = candidateDefinitionOutcome is null
                    ? null
                    : ReadProperty<string>(candidateDefinitionOutcome, "FailureReason"),
                llmCallCount = candidateDefinitionOutcome is null
                    ? (int?)null
                    : ReadProperty<int>(candidateDefinitionOutcome, "LlmCallCount"),
                hypotheticalSinglePositionValue = candidateDefinitionOutcome is null
                    ? null
                    : ReadProperty<string>(
                        candidateDefinitionOutcome,
                        "HypotheticalSinglePositionValue"),
                observedCandidateObjectType,
                observedCandidateEligibilityRule,
                candidateObjectType,
                candidateEligibilityRule,
                appliedDefinitionSource = useCompatibilityMissionContext
                    ? "frozen_exp021_c0"
                    : "current_run"
            },
            counts = new
            {
                candidates = candidates.Length,
                approved = approvals.Count,
                rejected = candidates.Length - approvals.Count,
                llmCalls = ReadProperty<int>(execution, "LlmCallCount"),
                recordedCalls = recordingLlm.Calls.Count,
                roleDefinitionCalls = roleDefinitionCalls.Length,
                candidateDefinitionCalls = candidateDefinitionCalls.Length,
                identityCalls = identityCalls.Length,
                compatibilityCalls = compatibilityCalls.Length,
                candidateDecisionCount = ReadProperty<int>(
                    execution,
                    "CandidateDecisionCount"),
                protocolRepairCount = ReadProperty<int>(
                    execution,
                    "ProtocolRepairCount"),
                labelReviewLlmCallCount = ReadProperty<int>(
                    execution,
                    "LabelReviewLlmCallCount"),
                executedIdentityBatchCount = ReadProperty<int>(
                    execution,
                    "ExecutedBatchCount"),
                identityInputBudgetSplitCount = ReadProperty<int>(
                    execution,
                    "InputBudgetSplitCount"),
                largestExecutedIdentityBatchCandidateCount = ReadProperty<int>(
                    execution,
                    "LargestExecutedBatchCandidateCount"),
                maximumMeasuredIdentityInputTokens = ReadNullableIntProperty(
                    execution,
                    "MaximumMeasuredInputTokens"),
                compatibilityBatchCount = ReadProperty<int>(
                    execution,
                    "ColumnCompatibilityBatchCount"),
                compatibilityInputBudgetSplitCount = ReadProperty<int>(
                    execution,
                    "ColumnCompatibilityInputBudgetSplitCount")
            },
            timing = new
            {
                identityElapsedMilliseconds = identityElapsed,
                compatibilityElapsedMilliseconds = compatibilityElapsed,
                roleDefinitionElapsedMilliseconds = roleDefinitionCalls.Sum(
                    static call => call.ElapsedMilliseconds),
                candidateDefinitionElapsedMilliseconds =
                    candidateDefinitionCalls.Sum(
                        static call => call.ElapsedMilliseconds),
                measuredPipelineMilliseconds = identityElapsed
                    + compatibilityElapsed
                    + roleDefinitionCalls.Sum(
                        static call => call.ElapsedMilliseconds)
                    + candidateDefinitionCalls.Sum(
                        static call => call.ElapsedMilliseconds),
                auditWallMilliseconds = auditWall.ElapsedMilliseconds,
                totalIncludingBackendMilliseconds = total.ElapsedMilliseconds,
                sumRecordedCallMilliseconds = recordingLlm.Calls.Sum(
                    static call => call.ElapsedMilliseconds)
            },
            calls = recordingLlm.Calls,
            candidateDecisions,
            aggregateCompletion = completion.Content,
            humanInspection = new
            {
                status = "A_FAIRE",
                instruction = "Inspecter chaque identité puis chaque compatibilité de rôle avant approbation."
            }
        };
        await WriteJsonAsync(artifactPath, report);
        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"Calls: {recordingLlm.Calls.Count} ({roleDefinitionCalls.Length} role + "
            + $"{candidateDefinitionCalls.Length} definition + {identityCalls.Length} identity + "
            + $"{compatibilityCalls.Length} compatibility)");
        output.WriteLine($"Approved: {approvals.Count}/{candidates.Length}");
        output.WriteLine($"Audit wall: {auditWall.ElapsedMilliseconds} ms");

        Assert.True(snapshotMatches);
        Assert.True(ReadProperty<bool>(decision, "ProtocolValid"), completion.Content);
        Assert.True(ReadProperty<bool>(execution, "ColumnCompatibilityApplied"));
        Assert.True(
            ReadProperty<bool>(execution, "ColumnCompatibilityProtocolValid"),
            completion.Content);
        Assert.Equal(20, ReadProperty<int>(execution, "CandidateDecisionCount"));
        Assert.Equal(4, identityCalls.Length);
        Assert.Equal(
            ReadProperty<int>(execution, "LlmCallCount")
            + roleDefinitionCalls.Length
            + candidateDefinitionCalls.Length,
            recordingLlm.Calls.Count);
        Assert.Equal(0, ReadProperty<int>(execution, "LabelReviewLlmCallCount"));
        if (usePostObservationRoleReview)
        {
            Assert.NotNull(roleOutcome);
            Assert.True(ReadProperty<bool>(roleOutcome!, "ProtocolValid"));
            Assert.Single(roleDefinitionCalls);
            Assert.Equal(ColumnLabels, effectiveRoles.Keys);
            Assert.All(effectiveRoles, pair =>
                Assert.False(string.Equals(
                    pair.Key,
                    pair.Value,
                    StringComparison.OrdinalIgnoreCase)));
        }
        if (usePostObservationCandidateDefinition)
        {
            Assert.NotNull(candidateDefinitionOutcome);
            Assert.True(ReadProperty<bool>(
                candidateDefinitionOutcome!,
                "ProtocolValid"));
            Assert.Single(candidateDefinitionCalls);
            Assert.False(string.IsNullOrWhiteSpace(candidateObjectType));
            Assert.False(string.IsNullOrWhiteSpace(candidateEligibilityRule));
        }
    }

    private const string FrozenQuestion =
        "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi "
        + "incluant petit-déjeuner, déjeuner, collation et souper. Fais un format "
        + "clair et professionnel, avec uniquement des sources utiles, non dupliquées "
        + "inutilement. N'invente rien.";

    private const string FrozenMission = """
        LIVRABLE: planning de repas avec vingt préparations documentées
        DIMENSIONS: cinq jours x quatre moments
        PREUVES_ATOMIQUES: vingt préparations culinaires nommées distinctes
        ACCEPTER_SI: vingt titres distincts sont prouvés par fichier et page
        INSUFFISANT_SEULEMENT_SI: moins de vingt noms distincts sont prouvés
        """;

    private const string FrozenC0CandidateObjectType = "recette de cuisine";

    private const string FrozenC0CandidateEligibilityRule =
        "La valeur doit nommer une recette spécifique, identifiable par son nom "
        + "complet, telle qu'elle apparaît dans un document de cuisine, sans "
        + "référence à un jour, un type de repas ou un contexte de préparation.";

    private static readonly string[] FrozenC0ApprovedContentCardIds =
    {
        "6b02209d-5b37-0552-3ad5-b5770f9e7707",
        "442d07a7-c8d9-7ac4-4bb2-b27d4ce885fe",
        "7ae15331-2af8-e0a4-b801-f9887464d7d0",
        "28a2d4b7-de46-e870-9400-4f370c4ebdbc",
        "620969d4-745f-69b5-9683-5ca1eea38232",
        "18e05cbe-5efd-16a6-12d5-1c50b94d587d"
    };

    private static readonly IReadOnlyDictionary<string, string>
        FrozenB0RoleDefinitions = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Petit-déjeuner"] =
                "IN_SCOPE: Repas matinal composé de produits frais comme du pain, "
                + "du lait ou du jus d'orange. | OUT_OF_SCOPE: Déjeuner, collation "
                + "ou souper, éléments liés à d'autres moments de la journée",
            ["Déjeuner"] =
                "IN_SCOPE: Repas principal de l'après-midi avec une variété de "
                + "protéines, légumes et céréales. | OUT_OF_SCOPE: Petit-déjeuner, "
                + "collation ou souper, éléments liés à d'autres moments",
            ["Collation"] =
                "IN_SCOPE: Goût ou aliment léger entre les repas, comme un fruit ou "
                + "une barre de céréales. | OUT_OF_SCOPE: Petit-déjeuner, déjeuner "
                + "ou souper, éléments liés à d'autres repas",
            ["Souper"] =
                "IN_SCOPE: Repas de fin de journée, équilibré avec des protéines, "
                + "des légumes et des céréales. | OUT_OF_SCOPE: Petit-déjeuner, "
                + "déjeuner ou collation, éléments liés à d'autres moments"
        };

    private static SourceBackedIntake BuildMealPlanIntake()
        => new(
            FrozenQuestion,
            "rag.plan_repas",
            new[] { "5 jours", "4 moments", "20 noms distincts" },
            Enumerable.Range(1, 20).Select(static index => "case:" + index).ToArray(),
            AllowsPartialAnswer: false,
            Language: "fr")
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine",
                    "Cuisine",
                    10,
                    Array.Empty<string>())
            },
            CanonicalColumnSemanticRoles = ColumnLabels.ToDictionary(
                static label => label,
                static label => label,
                StringComparer.OrdinalIgnoreCase)
        };

    private static IReadOnlyList<RawCandidateObservation> ReadRawCandidates(
        JsonElement raw)
    {
        Assert.True(raw.TryGetProperty("items", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        return items.EnumerateArray()
            .Select((item, index) => new RawCandidateObservation(
                index + 1,
                item.GetProperty("contentCardId").GetString() ?? string.Empty,
                item.GetProperty("title").GetString() ?? string.Empty,
                item.GetProperty("docPath").GetString() ?? string.Empty,
                item.GetProperty("pageStart").GetInt32(),
                item.GetProperty("cardIndex").GetInt32()))
            .ToArray();
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildSingleCandidateRoleMessages(
            string evidenceId,
            string displayValue,
            IReadOnlyList<string> roleLabels)
        => new[]
        {
            SourceBackedAgentMessage.System(
                "Tu juges la compatibilite semantique d'un seul candidat source "
                + "deja audite avec les roles visibles d'un livrable. Le LLM est "
                + "le seul decideur du sens. Retourne uniquement l'objet JSON demande."),
            SourceBackedAgentMessage.User(
                "SAAIA_SOURCE_BACKED_STEP=SingleCandidateNamedRoleCompatibility\n"
                + "DEMANDE ORIGINALE: " + FrozenQuestion + "\n"
                + "TYPE ATOMIQUE DECIDE PAR LE LLM: "
                + FrozenC0CandidateObjectType + "\n"
                + "REGLE D'ELIGIBILITE DECIDEE PAR LE LLM: "
                + FrozenC0CandidateEligibilityRule + "\n"
                + "ROLES VISIBLES, SANS ORDRE DE PREFERENCE: "
                + string.Join(" | ", roleLabels) + "\n"
                + "CANDIDAT UNIQUE:\n"
                + evidenceId + " = " + displayValue + "\n"
                + "Retourne tous les labels exacts auxquels ce candidat convient "
                + "naturellement. Retourne une liste vide s'il ne convient a aucun. "
                + "Ne choisis jamais par quota ni par position.")
        };

    private static LlmStructuredOutputContract BuildSingleCandidateRoleContract(
        IReadOnlyList<string> roleLabels)
        => new(
            "phase2_single_candidate_role_compatibility_v1",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    compatibleColumnLabels = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            @enum = roleLabels
                        },
                        uniqueItems = true,
                        maxItems = roleLabels.Count
                    }
                },
                required = new[] { "compatibleColumnLabels" },
                additionalProperties = false
            }));

    private static SingleCandidateRoleDecision ReadSingleCandidateRoleDecision(
        CandidateInput input,
        SourceBackedAgentCompletion completion,
        IReadOnlyList<string> canonicalRoleOrder)
    {
        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "compatibleColumnLabels",
                    out var labels)
                || labels.ValueKind != JsonValueKind.Array)
            {
                return InvalidSingleCandidateRoleDecision(
                    input,
                    completion,
                    "compatible_column_labels_array_required");
            }
            var allowed = canonicalRoleOrder.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var parsed = new List<string>();
            foreach (var label in labels.EnumerateArray())
            {
                var value = label.ValueKind == JsonValueKind.String
                    ? label.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(value)
                    || !allowed.Contains(value)
                    || parsed.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return InvalidSingleCandidateRoleDecision(
                        input,
                        completion,
                        "compatible_column_label_unknown_or_duplicate");
                }
                parsed.Add(value);
            }
            var canonical = canonicalRoleOrder
                .Where(role => parsed.Contains(
                    role,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            return new SingleCandidateRoleDecision(
                input.Candidate.EvidenceId,
                input.Raw.ContentCardId,
                input.Raw.Title,
                true,
                string.Empty,
                canonical,
                completion.Content);
        }
        catch (JsonException)
        {
            return InvalidSingleCandidateRoleDecision(
                input,
                completion,
                "compatibility_json_invalid");
        }
    }

    private static SingleCandidateRoleDecision
        InvalidSingleCandidateRoleDecision(
            CandidateInput input,
            SourceBackedAgentCompletion completion,
            string failureReason)
        => new(
            input.Candidate.EvidenceId,
            input.Raw.ContentCardId,
            input.Raw.Title,
            false,
            failureReason,
            Array.Empty<string>(),
            completion.Content);

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateRolePairMessages(
            CandidateRolePair pair,
            string roleDefinition)
        => new[]
        {
            SourceBackedAgentMessage.System(
                "Tu juges un seul couple candidat source et role de livrable. "
                + "Le LLM est le seul decideur du sens. Retourne uniquement "
                + "l'objet JSON demande."),
            SourceBackedAgentMessage.User(
                "SAAIA_SOURCE_BACKED_STEP=CandidateRolePairCompatibility\n"
                + "DEMANDE ORIGINALE: " + FrozenQuestion + "\n"
                + "TYPE ATOMIQUE DECIDE PAR LE LLM: "
                + FrozenC0CandidateObjectType + "\n"
                + "REGLE D'ELIGIBILITE DECIDEE PAR LE LLM: "
                + FrozenC0CandidateEligibilityRule + "\n"
                + "TARGET ROLE LABEL: " + pair.Role + "\n"
                + "TARGET ROLE DEFINITION AUTHORED BY THE LLM: "
                + roleDefinition + "\n"
                + "CANDIDAT UNIQUE:\n"
                + pair.Input.Candidate.EvidenceId + " = "
                + pair.Input.Raw.Title + "\n"
                + "Decide compatible=true seulement si ce candidat convient "
                + "naturellement a ce role precis dans la demande. Sinon false. "
                + "Ne raisonne ni par quota ni par position.")
        };

    private static LlmStructuredOutputContract BuildCandidateRolePairContract()
        => new(
            "phase2_single_candidate_role_compatibility_boolean_v1",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    compatible = new { type = "boolean" }
                },
                required = new[] { "compatible" },
                additionalProperties = false
            }));

    private static CandidateRolePairDecision ReadCandidateRolePairDecision(
        CandidateRolePair pair,
        SourceBackedAgentCompletion completion)
    {
        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "compatible",
                    out var compatible)
                || compatible.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                return InvalidCandidateRolePairDecision(
                    pair,
                    completion,
                    "compatible_boolean_required");
            }
            return new CandidateRolePairDecision(
                pair.Input.Candidate.EvidenceId,
                pair.Input.Raw.ContentCardId,
                pair.Input.Raw.Title,
                pair.Role,
                true,
                string.Empty,
                compatible.GetBoolean(),
                completion.Content);
        }
        catch (JsonException)
        {
            return InvalidCandidateRolePairDecision(
                pair,
                completion,
                "compatibility_json_invalid");
        }
    }

    private static CandidateRolePairDecision InvalidCandidateRolePairDecision(
        CandidateRolePair pair,
        SourceBackedAgentCompletion completion,
        string failureReason)
        => new(
            pair.Input.Candidate.EvidenceId,
            pair.Input.Raw.ContentCardId,
            pair.Input.Raw.Title,
            pair.Role,
            false,
            failureReason,
            false,
            completion.Content);

    private static async Task<object> InvokePrivateAsync(
        SourceBackedAgentV2Runner runner,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(SourceBackedAgentV2Runner)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name == methodName)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                       && parameters.Zip(arguments).All(static pair =>
                           pair.Second is null
                           || pair.First.ParameterType.IsInstanceOfType(pair.Second));
            });
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(runner, arguments));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return result!;
    }

    private static T ReadProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(value));
    }

    private static T? ReadNullableProperty<T>(object value, string propertyName)
        where T : class
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(value) as T;
    }

    private static int? ReadNullableIntProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(value) is System.Int32 number ? number : null;
    }

    private static IReadOnlyList<object> ReadObjects(object value, string propertyName)
        => ((IEnumerable)value.GetType().GetProperty(propertyName)!.GetValue(value)!)
            .Cast<object>()
            .ToArray();

    private static object? ReadOptionalProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(value);
    }

    private static IReadOnlyList<string> ReadStringList(
        object value,
        string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property!.GetValue(value) is IEnumerable values
            ? values.Cast<object>().Select(static item => item.ToString()!).ToArray()
            : Array.Empty<string>();
    }

    private static object CreateSemanticLayoutDimensions(int rows, int columns)
    {
        var dimensionsType = typeof(SourceBackedAgentV2Runner).GetNestedType(
            "SemanticLayoutDimensions",
            BindingFlags.NonPublic);
        Assert.NotNull(dimensionsType);
        var dimensions = Activator.CreateInstance(
            dimensionsType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { rows, columns },
            culture: null);
        Assert.NotNull(dimensions);
        return dimensions!;
    }

    private static string ResolveArtifactPath(string experiment, string variant)
    {
        var configured = Environment.GetEnvironmentVariable(
            "SAAIA_PHASE2_AUDIT_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());
        return Path.Combine(
            FindRepoRoot(),
            "artifacts",
            "goal-rag-end-to-end-20260826-233646",
            "phase2",
            experiment.ToLowerInvariant()
            + "-baseline-"
            + variant.Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant()
            + "-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss")
            + ".json");
    }

    private static Task WriteJsonAsync(string path, object report)
        => File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

    private static string HashRoleDefinitions(
        IReadOnlyDictionary<string, string> roles)
    {
        var canonical = string.Join(
            "\n",
            ColumnLabels.Select(label =>
                label + "=" + roles.GetValueOrDefault(label, string.Empty).Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string NormalizeLlmBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class RecordingLlm(OpenAiLlmClient inner)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient,
          ISourceBackedAgentInputTokenCounter
    {
        public List<LlmCallObservation> Calls { get; } = new();
        public CompatibilityMissionContext? CompatibilityMissionContext { get; set; }

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var measuredInputTokens = await inner.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);
            var stopwatch = Stopwatch.StartNew();
            var completion = await inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? 0,
                Math.Clamp(maxTokens, 128, 1200),
                ct,
                requireToolCall);
            stopwatch.Stop();
            var contract = tools.Count == 0
                ? null
                : string.Join(",", tools.Select(static tool => tool.Name));
            Calls.Add(new LlmCallObservation(
                Calls.Count + 1,
                tools.Any(static tool => string.Equals(
                    tool.Name,
                    "submit_column_semantics",
                    StringComparison.OrdinalIgnoreCase))
                    ? "role-definition"
                    : "native",
                contract,
                null,
                ExtractCandidateLines(messages),
                measuredInputTokens,
                messages.Sum(static message => message.Content?.Length ?? 0),
                tools.Sum(static tool => tool.Parameters.GetRawText().Length),
                maxTokens,
                completion.Content?.Length ?? 0,
                stopwatch.ElapsedMilliseconds,
                RenderCompletion(completion)));
            return completion;
        }

        public async Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            var isCompatibility = contract.Name.Contains(
                "column_compatibility",
                StringComparison.OrdinalIgnoreCase)
                || contract.Name.Contains(
                    "role_compatibility",
                    StringComparison.OrdinalIgnoreCase);
            var effectiveMessages = isCompatibility
                                    && CompatibilityMissionContext is { } context
                ? AddCompatibilityMissionContext(messages, context)
                : messages;
            var measuredInputTokens = await inner.CountNativeInputTokensAsync(
                effectiveMessages,
                Array.Empty<SourceBackedAgentToolDefinition>(),
                ct,
                requireToolCall: false);
            var stopwatch = Stopwatch.StartNew();
            var content = await inner.ChatOnceStructuredAsync(
                effectiveMessages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray(),
                temperatureOverride ?? 0,
                Math.Clamp(maxTokens, 128, 1200),
                contract,
                ct);
            stopwatch.Stop();
            var phase = isCompatibility
                ? "compatibility"
                : contract.Name.Contains(
                    "candidate_label_resolution",
                    StringComparison.OrdinalIgnoreCase)
                    ? "label-resolution"
                : contract.Name.Contains(
                    "atomic_candidate_definition",
                    StringComparison.OrdinalIgnoreCase)
                    ? "candidate-definition"
                    : "identity";
            Calls.Add(new LlmCallObservation(
                Calls.Count + 1,
                phase,
                contract.Name,
                ExtractRole(effectiveMessages),
                ExtractCandidateLines(effectiveMessages),
                measuredInputTokens,
                effectiveMessages.Sum(static message => message.Content?.Length ?? 0),
                contract.Schema.GetRawText().Length,
                maxTokens,
                content.Length,
                stopwatch.ElapsedMilliseconds,
                content));
            return new SourceBackedAgentCompletion(
                content,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop");
        }

        private static IReadOnlyList<SourceBackedAgentMessage>
            AddCompatibilityMissionContext(
                IReadOnlyList<SourceBackedAgentMessage> messages,
                CompatibilityMissionContext context)
        {
            var prefix = """
                SAAIA_SOURCE_BACKED_CONTEXT=CompatibilityMission
                DEMANDE ORIGINALE: {0}
                TYPE ATOMIQUE DECIDE PAR LE LLM: {1}
                REGLE D'ELIGIBILITE DECIDEE PAR LE LLM: {2}
                Ces informations definissent la mission et le type des candidats deja audites. Juge uniquement leur compatibilite naturelle avec le role cible nomme ci-dessous, sans quota.

                """;
            var rendered = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                prefix,
                context.UserQuestion,
                context.CandidateObjectType,
                context.CandidateEligibilityRule);
            return messages.Select(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && (message.Content ?? string.Empty).Contains(
                    "SAAIA_SOURCE_BACKED_STEP=CandidateColumnCompatibility",
                    StringComparison.Ordinal)
                    ? message with { Content = rendered + message.Content }
                    : message).ToArray();
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => inner.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);

        private static string? ExtractRole(
            IReadOnlyList<SourceBackedAgentMessage> messages)
        {
            const string prefix = "TARGET ROLE LABEL:";
            return messages
                .SelectMany(static message =>
                    (message.Content ?? string.Empty).Split('\n'))
                .Select(static line => line.Trim())
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
                .Select(line => line[prefix.Length..].Trim())
                .FirstOrDefault();
        }

        private static IReadOnlyList<string> ExtractCandidateLines(
            IReadOnlyList<SourceBackedAgentMessage> messages)
            => messages
                .SelectMany(static message =>
                    (message.Content ?? string.Empty).Split('\n'))
                .Select(static line => line.Trim())
                .Where(static line =>
                    (line.StartsWith('c') && line.Contains(" | libelle=", StringComparison.Ordinal))
                    || (line.StartsWith('E') && line.Contains(" = ", StringComparison.Ordinal)))
                .ToArray();

        private static string RenderCompletion(SourceBackedAgentCompletion completion)
        {
            var toolCalls = completion.ToolCalls.Select(call => new
            {
                call.Name,
                Arguments = call.Arguments
            });
            return JsonSerializer.Serialize(new
            {
                completion.Content,
                ToolCalls = toolCalls,
                completion.FinishReason
            });
        }
    }

    private sealed class UnusedToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "EXP-019 stops after identity and compatibility audit.");
    }

    private sealed record RawCandidateObservation(
        int Position,
        string ContentCardId,
        string Title,
        string DocPath,
        int PageStart,
        int CardIndex);

    private sealed record CandidateInput(
        EvidenceItem Candidate,
        RawCandidateObservation Raw);

    private sealed record ApprovalObservation(
        string EvidenceId,
        string DisplayValue,
        IReadOnlyList<string> CompatibleColumnLabels);

    private sealed record CandidateObservation(
        int Position,
        string EvidenceId,
        string ContentCardId,
        string Title,
        string DocPath,
        int PageStart,
        int CardIndex,
        string Decision,
        string Classification,
        string? DisplayValue,
        IReadOnlyList<string> CompatibleColumnLabels);

    private sealed record CompatibilityMissionContext(
        string UserQuestion,
        string CandidateObjectType,
        string CandidateEligibilityRule);

    private sealed record SingleCandidateRoleDecision(
        string EvidenceId,
        string ContentCardId,
        string Title,
        bool ProtocolValid,
        string FailureReason,
        IReadOnlyList<string> CompatibleColumnLabels,
        string RawOutput);

    private sealed record CandidateRolePair(
        CandidateInput Input,
        string Role);

    private sealed record CandidateRolePairDecision(
        string EvidenceId,
        string ContentCardId,
        string Title,
        string Role,
        bool ProtocolValid,
        string FailureReason,
        bool Compatible,
        string RawOutput);

    private sealed record LlmCallObservation(
        int Index,
        string Phase,
        string? Contract,
        string? Role,
        IReadOnlyList<string> Candidates,
        int? MeasuredInputTokens,
        int PromptCharacters,
        int SchemaCharacters,
        int MaximumOutputTokens,
        int OutputCharacters,
        long ElapsedMilliseconds,
        string Output);
}
