using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedStructuredCandidateWriterTests
{
    [Fact]
    public void Compact_assignment_protocol_materializes_the_canonical_table()
    {
        var bundle = BuildBundle(8);
        var rowLabels = new[] { "Jour A", "Jour B" };
        var columnLabels = new[] { "Matin", "Midi", "Collation", "Soir" };
        var rawAssignments = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 8).Select(index => $"C{index:D2}=E{index}"));

        var valid = SourceBackedAgentV2Runner
            .TryBuildStructuredCandidateAssignmentTable(
                rawAssignments,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                "Jour",
                rowLabels,
                columnLabels,
                out var renderedTable,
                out var selectedIds,
                out var failureReason);

        Assert.True(valid, failureReason);
        Assert.Equal(
            Enumerable.Range(1, 8).Select(static index => "E" + index),
            selectedIds);
        Assert.Contains(
            "| Jour | Matin | Midi | Collation | Soir |",
            renderedTable,
            StringComparison.Ordinal);
        Assert.Contains(
            "| Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |",
            renderedTable,
            StringComparison.Ordinal);
        Assert.Contains(
            "| Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |",
            renderedTable,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_assignment_protocol_rejects_a_reused_candidate()
    {
        var bundle = BuildBundle(8);
        var rawAssignments = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 8).Select(index =>
                $"C{index:D2}=E{(index == 8 ? 7 : index)}"));

        var valid = SourceBackedAgentV2Runner
            .TryBuildStructuredCandidateAssignmentTable(
                rawAssignments,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                "Jour",
                new[] { "Jour A", "Jour B" },
                new[] { "Matin", "Midi", "Collation", "Soir" },
                out _,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_assignment_duplicate",
            failureReason);
    }

    [Fact]
    public void Partial_table_repair_targets_only_missing_and_duplicate_cells()
    {
        var bundle = BuildBundle(10);
        var candidateIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var table = """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 6 [E6] | — |
            """;

        var prepared = SourceBackedAgentV2Runner
            .TryPrepareStructuredCandidateTableRepair(
                table,
                bundle,
                candidateIds,
                new[] { "Jour A", "Jour B" },
                new[] { "Matin", "Midi", "Collation", "Soir" },
                out var repair);

        Assert.True(prepared);
        Assert.Equal(new[] { 5, 6, 7 }, repair.ConflictCellIndexes);
        Assert.Equal(7, repair.Assignments.Count);
        Assert.Contains("E6", repair.AvailableEvidenceIds);
        Assert.DoesNotContain("E1", repair.AvailableEvidenceIds);
        Assert.Equal("—", repair.OriginalCellValues[7]);
    }

    [Fact]
    public void Partial_table_patch_preserves_locked_cells_and_materializes_one_table()
    {
        var bundle = BuildBundle(10);
        var candidateIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var table = """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 6 [E6] | — |
            """;
        Assert.True(SourceBackedAgentV2Runner
            .TryPrepareStructuredCandidateTableRepair(
                table,
                bundle,
                candidateIds,
                new[] { "Jour A", "Jour B" },
                new[] { "Matin", "Midi", "Collation", "Soir" },
                out var repair));

        var patched = SourceBackedAgentV2Runner
            .TryApplyStructuredCandidateConflictRepair(
                repair,
                "C06=E6\nC07=E7\nC08=E8",
                out var assignments,
                out var patchFailure);
        var materialized = SourceBackedAgentV2Runner
            .TryBuildStructuredCandidateAssignmentTable(
                assignments,
                bundle,
                candidateIds,
                "Jour",
                new[] { "Jour A", "Jour B" },
                new[] { "Matin", "Midi", "Collation", "Soir" },
                out var renderedTable,
                out var selectedIds,
                out var materializationFailure);

        Assert.True(patched, patchFailure);
        Assert.True(materialized, materializationFailure);
        Assert.Equal(8, selectedIds.Count);
        Assert.Contains(
            "| Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |",
            renderedTable,
            StringComparison.Ordinal);
        Assert.Contains(
            "| Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |",
            renderedTable,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_table_with_external_noise_can_be_mechanically_normalized()
    {
        var bundle = BuildBundle(8);
        var candidateIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var table = """
            Brouillon externe [E8]
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Choix [E1] | Choix [E2] | Choix [E3] | Choix [E4] |
            | Jour B | Choix [E5] | Choix [E6] | Choix [E7] | Choix [E8] |
            """;

        var prepared = SourceBackedAgentV2Runner
            .TryPrepareStructuredCandidateTableRepair(
                table,
                bundle,
                candidateIds,
                new[] { "Jour A", "Jour B" },
                new[] { "Matin", "Midi", "Collation", "Soir" },
                out var repair);

        Assert.True(prepared);
        Assert.Empty(repair.ConflictCellIndexes);
        Assert.Equal(8, repair.Assignments.Count);
    }

    [Fact]
    public async Task Writer_normalizes_a_valid_table_with_a_trailing_protocol_example()
    {
        var bundle = BuildBundle(8);
        var llm = new ScriptedWriterLlm(new SourceBackedAgentCompletion(
            """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |
            BESOIN_PREUVES=CXX,CYY
            RAISON=besoin documentaire concis et actionnable
            """,
            Array.Empty<SourceBackedAgentToolCall>(),
            "stop"));
        var runner = BuildRunner(llm);

        var execution = await runner.CompleteStructuredCandidateWriterAsync(
            new SourceBackedIntake(
                "Produis une grille sourcee.",
                "rag.answer",
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                "fr"),
            bundle,
            bundle.Items.Select(static item => item.EvidenceId).ToArray(),
            "Jour",
            new[] { "Jour A", "Jour B" },
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new Dictionary<string, string>(),
            null,
            CancellationToken.None);

        Assert.True(execution.ProtocolValid, execution.FailureReason);
        Assert.False(execution.RequiresMoreEvidence);
        Assert.Equal(1, execution.LlmCallCount);
        Assert.Equal(
            "structured_candidate_table_mechanically_normalized",
            execution.Completion.FinishReason);
        Assert.DoesNotContain(
            "BESOIN_PREUVES",
            execution.Completion.Content,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Valeur 8 [E8]", execution.Completion.Content);
    }

    [Fact]
    public async Task Writer_uses_one_natural_draft_and_one_local_patch()
    {
        var bundle = BuildBundle(10);
        var llm = new ScriptedWriterLlm(
            new SourceBackedAgentCompletion(
                """
                | Jour | Matin | Midi | Collation | Soir |
                | --- | --- | --- | --- | --- |
                | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
                | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 6 [E6] | — |
                """,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"),
            new SourceBackedAgentCompletion(
                "C06=E6\nC07=E7\nC08=E8",
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"));
        var runner = BuildRunner(llm);

        var execution = await runner.CompleteStructuredCandidateWriterAsync(
            new SourceBackedIntake(
                "Produis une grille sourcee.",
                "rag.answer",
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                "fr"),
            bundle,
            bundle.Items.Select(static item => item.EvidenceId).ToArray(),
            "Jour",
            new[] { "Jour A", "Jour B" },
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Matin"] = "premier role",
                ["Midi"] = "deuxieme role",
                ["Collation"] = "troisieme role",
                ["Soir"] = "quatrieme role"
            },
            null,
            CancellationToken.None);

        Assert.True(execution.ProtocolValid, execution.FailureReason);
        Assert.Equal(2, execution.LlmCallCount);
        Assert.Equal(2, llm.CallCount);
        Assert.Equal("structured_candidate_cells_repaired", execution.Completion.FinishReason);
        Assert.Contains("Valeur 8 [E8]", execution.Completion.Content);
        Assert.Contains("CELLULES EN CONFLIT A REAFFECTER", llm.RequestText(1));
        Assert.DoesNotContain("GRILLE A PRODUIRE", llm.RequestText(1));
    }

    [Fact]
    public async Task Writer_can_request_more_evidence_instead_of_forcing_duplicate_cells()
    {
        var bundle = BuildBundle(10);
        var llm = new ScriptedWriterLlm(
            new SourceBackedAgentCompletion(
                """
                | Jour | Matin | Midi | Collation | Soir |
                | --- | --- | --- | --- | --- |
                | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
                | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 6 [E6] | — |
                """,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"),
            new SourceBackedAgentCompletion(
                "BESOIN_PREUVES=C06,C07,C08\n"
                + "RAISON=Il manque trois alternatives distinctes pour les roles affiches.",
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"));
        var runner = BuildRunner(llm);

        var execution = await runner.CompleteStructuredCandidateWriterAsync(
            new SourceBackedIntake(
                "Produis une grille sourcee.",
                "rag.answer",
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                "fr"),
            bundle,
            bundle.Items.Select(static item => item.EvidenceId).ToArray(),
            "Jour",
            new[] { "Jour A", "Jour B" },
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new Dictionary<string, string>(),
            null,
            CancellationToken.None);

        Assert.False(execution.ProtocolValid);
        Assert.True(execution.RequiresMoreEvidence);
        Assert.Equal(3, execution.RequestedAdditionalCandidateCount);
        Assert.Contains("trois alternatives", execution.ResearchNeed);
        Assert.Equal(2, execution.LlmCallCount);
        Assert.Equal(
            "structured_candidate_more_evidence_requested",
            execution.Completion.FinishReason);

        var target = 8;
        var collectionOpen = false;
        var selectionOnly = true;
        var directWriterRequested = true;
        string? feedback = null;
        var maximumRunTurns = 6;
        var traceSequence = 0;
        var traces = new List<SourceBackedTraceEvent>();
        var applied = runner.TryApplyStructuredCandidateWriterEvidenceRequest(
            execution,
            8,
            4,
            ref target,
            ref collectionOpen,
            ref selectionOnly,
            ref directWriterRequested,
            ref feedback,
            ref maximumRunTurns,
            12,
            traces,
            "writer-evidence-request-test",
            ref traceSequence);

        Assert.True(applied);
        Assert.Equal(11, target);
        Assert.True(collectionOpen);
        Assert.False(selectionOnly);
        Assert.False(directWriterRequested);
        Assert.Contains("trois alternatives", feedback);
        Assert.Equal(7, maximumRunTurns);
        Assert.Contains(traces, static trace =>
            trace.EventName
            == "source_backed_agent_v2.structured_candidate_writer.more_evidence_requested"
            && trace.Fields["decision_source"] == "llm_orchestrator_writer");
    }

    [Fact]
    public async Task Writer_retries_the_local_patch_without_restarting_a_full_draft()
    {
        var bundle = BuildBundle(10);
        var llm = new ScriptedWriterLlm(
            new SourceBackedAgentCompletion(
                """
                | Jour | Matin | Midi | Collation | Soir |
                | --- | --- | --- | --- | --- |
                | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
                | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 6 [E6] | — |
                """,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"),
            new SourceBackedAgentCompletion(
                "patch invalide",
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"),
            new SourceBackedAgentCompletion(
                "cette troisieme reponse ne doit jamais etre consommee",
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop"));
        var runner = BuildRunner(llm);

        var execution = await runner.CompleteStructuredCandidateWriterAsync(
            new SourceBackedIntake(
                "Produis une grille sourcee.",
                "rag.answer",
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                "fr"),
            bundle,
            bundle.Items.Select(static item => item.EvidenceId).ToArray(),
            "Jour",
            new[] { "Jour A", "Jour B" },
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new Dictionary<string, string>(),
            null,
            CancellationToken.None);

        Assert.False(execution.ProtocolValid);
        Assert.Equal(
            "structured_candidate_writer_conflict_patch_count_invalid",
            execution.FailureReason);
        Assert.Equal(3, execution.LlmCallCount);
        Assert.Equal(3, llm.CallCount);
        Assert.Contains("PATCH PRECEDENT INVALIDE", llm.RequestText(2));
        Assert.DoesNotContain("GRILLE A PRODUIRE", llm.RequestText(2));
    }

    [Fact]
    public void Validator_accepts_exactly_twenty_distinct_grounded_citations()
    {
        var bundle = BuildBundle(20);
        var completion = BuildCompletion(Enumerable.Range(1, 20));

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out var selectedIds,
                out var failureReason);

        Assert.True(valid, failureReason);
        Assert.Equal(20, selectedIds.Count);
        Assert.Equal(20, selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Validator_rejects_an_incomplete_citation_set()
    {
        var bundle = BuildBundle(20);
        var completion = BuildCompletion(Enumerable.Range(1, 19));

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_citation_count_invalid",
            failureReason);
    }

    [Fact]
    public void Validator_rejects_a_repeated_citation()
    {
        var bundle = BuildBundle(20);
        var ids = Enumerable.Range(1, 19).Append(19);
        var completion = BuildCompletion(ids);

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_citation_count_invalid",
            failureReason);
    }

    [Fact]
    public void Validator_rejects_an_unknown_or_unoffered_evidence_id()
    {
        var bundle = BuildBundle(20);
        var ids = Enumerable.Range(1, 19).Append(99);
        var completion = BuildCompletion(ids);

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_evidence_id_invalid",
            failureReason);
    }

    [Fact]
    public void Validator_rejects_two_ids_resolving_to_the_same_visible_source()
    {
        var bundle = BuildBundle(20);
        var duplicate = bundle.Items[1] with
        {
            DocPath = bundle.Items[0].DocPath,
            PageStart = bundle.Items[0].PageStart,
            PageEnd = bundle.Items[0].PageEnd,
            ChunkId = bundle.Items[0].ChunkId
        };
        bundle = bundle with
        {
            Items = bundle.Items
                .Select((item, index) => index == 1 ? duplicate : item)
                .ToArray()
        };
        var completion = BuildCompletion(Enumerable.Range(1, 20));

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_visible_source_duplicate",
            failureReason);
    }

    [Fact]
    public void Validator_rejects_two_sources_with_the_same_final_display_value()
    {
        var bundle = BuildBundle(20);
        var duplicateLabelHints = new Dictionary<string, string>(
            bundle.Items[1].SelectionHints,
            StringComparer.OrdinalIgnoreCase)
        {
            ["semanticDisplayValue"] = "Valeur 1"
        };
        var firstLabelHints = new Dictionary<string, string>(
            bundle.Items[0].SelectionHints,
            StringComparer.OrdinalIgnoreCase)
        {
            ["semanticDisplayValue"] = "Valeur 1"
        };
        bundle = bundle with
        {
            Items = bundle.Items
                .Select((item, index) => index switch
                {
                    0 => item with { SelectionHints = firstLabelHints },
                    1 => item with { SelectionHints = duplicateLabelHints },
                    _ => item
                })
                .ToArray()
        };

        var valid = SourceBackedAgentV2Runner
            .TryValidateStructuredCandidateWriterCompletion(
                BuildCompletion(Enumerable.Range(1, 20)),
                bundle,
                bundle.Items.Select(static item => item.EvidenceId).ToArray(),
                20,
                out _,
                out var failureReason);

        Assert.False(valid);
        Assert.Equal(
            "structured_candidate_writer_display_value_duplicate",
            failureReason);
    }

    [Fact]
    public void Revision_patch_changes_only_the_rejected_cells()
    {
        var bundle = BuildBundle(8);
        var shape = new SourceBackedStructuredTableShape(
            "Jour",
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new[] { "Jour A", "Jour B" });
        var previousAnswer = """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 2 [E2] | Valeur 1 [E1] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |
            """;
        var draft = new WriterDraft(
            previousAnswer,
            Enumerable.Range(1, 8).Select(static index => "E" + index).ToArray());

        var valid = SourceBackedAgentV2Runner.TryApplyStructuredAssignmentRevision(
            draft,
            shape,
            new[] { 0, 1 },
            new[] { "E1", "E2" },
            new Dictionary<int, string> { [0] = "E2", [1] = "E1" },
            bundle,
            "C01=E1\nC02=E2",
            out var revisedAnswer,
            out var failureReason);

        Assert.True(valid, failureReason);
        Assert.Contains(
            "| Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |",
            revisedAnswer,
            StringComparison.Ordinal);
        Assert.Contains(
            "| Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |",
            revisedAnswer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Revision_patch_rejects_a_duplicate_candidate_assignment()
    {
        var bundle = BuildBundle(8);
        var shape = new SourceBackedStructuredTableShape(
            "Jour",
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new[] { "Jour A", "Jour B" });
        var draft = new WriterDraft(
            """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 2 [E2] | Valeur 1 [E1] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |
            """,
            Enumerable.Range(1, 8).Select(static index => "E" + index).ToArray());

        var valid = SourceBackedAgentV2Runner.TryApplyStructuredAssignmentRevision(
            draft,
            shape,
            new[] { 0, 1 },
            new[] { "E1", "E2" },
            new Dictionary<int, string> { [0] = "E2", [1] = "E1" },
            bundle,
            "C01=E1\nC02=E1",
            out _,
            out var failureReason);

        Assert.False(valid);
        Assert.Contains("duplique", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revision_patch_accepts_harmless_line_indentation()
    {
        var bundle = BuildBundle(8);
        var shape = new SourceBackedStructuredTableShape(
            "Jour",
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new[] { "Jour A", "Jour B" });
        var draft = new WriterDraft(
            """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 2 [E2] | Valeur 1 [E1] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |
            """,
            Enumerable.Range(1, 8).Select(static index => "E" + index).ToArray());

        var valid = SourceBackedAgentV2Runner.TryApplyStructuredAssignmentRevision(
            draft,
            shape,
            new[] { 0, 1 },
            new[] { "E1", "E2" },
            new Dictionary<int, string> { [0] = "E2", [1] = "E1" },
            bundle,
            "C01=E1\r\n C02 = E2",
            out var revisedAnswer,
            out var failureReason);

        Assert.True(valid, failureReason);
        Assert.Contains(
            "| Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |",
            revisedAnswer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Revision_patch_accepts_scrambled_cells_and_rotates_rejected_candidates()
    {
        var bundle = BuildBundle(8);
        var shape = new SourceBackedStructuredTableShape(
            "Jour",
            new[] { "Matin", "Midi", "Collation", "Soir" },
            new[] { "Jour A", "Jour B" });
        var draft = new WriterDraft(
            """
            | Jour | Matin | Midi | Collation | Soir |
            | --- | --- | --- | --- | --- |
            | Jour A | Valeur 1 [E1] | Valeur 2 [E2] | Valeur 3 [E3] | Valeur 4 [E4] |
            | Jour B | Valeur 5 [E5] | Valeur 6 [E6] | Valeur 7 [E7] | Valeur 8 [E8] |
            """,
            Enumerable.Range(1, 8).Select(static index => "E" + index).ToArray());

        var valid = SourceBackedAgentV2Runner.TryApplyStructuredAssignmentRevision(
            draft,
            shape,
            new[] { 0, 2, 4 },
            new[] { "E1", "E3", "E5" },
            new Dictionary<int, string>
            {
                [0] = "E1",
                [2] = "E3",
                [4] = "E5"
            },
            bundle,
            " C03=E5\nC05=E1\n C01 = E3",
            out var revisedAnswer,
            out var failureReason);

        Assert.True(valid, failureReason);
        Assert.Contains("Valeur 3 [E3]", revisedAnswer, StringComparison.Ordinal);
        Assert.Contains("Valeur 5 [E5]", revisedAnswer, StringComparison.Ordinal);
        Assert.Contains("Valeur 1 [E1]", revisedAnswer, StringComparison.Ordinal);
    }

    private static SourceBackedAgentCompletion BuildCompletion(
        IEnumerable<int> evidenceIndexes)
        => new(
            string.Join(
                Environment.NewLine,
                evidenceIndexes.Select(index => $"Valeur {index} [E{index}]")),
            Array.Empty<SourceBackedAgentToolCall>(),
            "stop");

    private static SourceBackedAgentV2Runner BuildRunner(
        ISourceBackedAgentLlmClient llm)
        => new(
            llm,
            new NoopToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 6,
                MaximumToolCalls: 8,
                MaximumObservationItems: 8,
                MaximumObservationExcerptCharacters: 360,
                MaximumOutputTokens: 900));

    private static EvidenceBundle BuildBundle(int count)
        => new(
            "structured-candidate-writer-tests",
            "Produis une grille structuree.",
            Enumerable.Range(1, count)
                .Select(static index => new EvidenceItem(
                    "E" + index,
                    "canonical_content_card",
                    "documents.content_cards",
                    "inventaire",
                    "doc-" + index,
                    "document-" + index + ".pdf",
                    "Categorie/document-" + index + ".pdf",
                    "hash-" + index,
                    "revision-active",
                    index,
                    index,
                    "content-card:" + index,
                    "Valeur source " + index,
                    "Valeur source " + index,
                    1,
                    index,
                    "Categorie",
                    "fr",
                    "fr",
                    "native",
                    JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            contentCardId = "card-" + index,
                            title = "Valeur " + index,
                            kind = "named_item"
                        }
                    }),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Array.Empty<string>(),
                    new[] { "deterministic-test" }))
                .ToArray(),
            Array.Empty<SourceBackedTraceEvent>());

    private sealed class ScriptedWriterLlm : ISourceBackedAgentLlmClient
    {
        private readonly Queue<SourceBackedAgentCompletion> _completions;

        public ScriptedWriterLlm(params SourceBackedAgentCompletion[] completions)
        {
            _completions = new Queue<SourceBackedAgentCompletion>(completions);
        }

        public List<IReadOnlyList<SourceBackedAgentMessage>> Requests { get; } = new();

        public int CallCount => Requests.Count;

        public string RequestText(int index)
            => string.Join(
                Environment.NewLine,
                Requests[index].Select(static message => message.Content));

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            Requests.Add(messages);
            if (_completions.Count == 0)
                throw new InvalidOperationException("No scripted writer completion remains.");
            return Task.FromResult(_completions.Dequeue());
        }
    }

    private sealed class NoopToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The structured writer must not execute retrieval tools.");
    }
}
