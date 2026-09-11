using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceContractVerifierTests
{
    [Fact]
    public void Verify_rejects_cited_corpus_evidence_without_exact_revision_and_sha256()
    {
        var evidence = new EvidenceItem(
            "E1",
            "rag_hit",
            "rag.search",
            "documented fact",
            "doc-1",
            "Manual.pdf",
            "Knowledge/Manual.pdf",
            "e2f23af762e16714266050b3ad56078f",
            null,
            7,
            7,
            "chunk-7",
            "Documented source-backed fact.",
            "documented source-backed fact",
            0.9,
            1,
            "Knowledge",
            "en",
            "en",
            "native_text",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-identity-missing",
            "Give the fact.",
            new[] { evidence },
            Array.Empty<SourceBackedTraceEvent>());

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("Documented fact [E1]", new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.Contains(
            verification.Errors,
            static error => error.Code == "missing_revision_id");
        Assert.Contains(
            verification.Errors,
            static error => error.Code == "invalid_source_sha256");
    }

    [Fact]
    public void Verify_rejects_legacy_content_card_identity_encoded_as_chunk()
    {
        var evidence = new EvidenceItem(
            "E1",
            "canonical_content_card",
            "documents.content_cards",
            "documented option",
            "doc-1",
            "Options.pdf",
            "Knowledge/Options.pdf",
            new string('a', 64),
            "revision-active",
            7,
            7,
            "content-card:card-7",
            "Documented option.",
            "documented option",
            1,
            1,
            "Knowledge",
            "en",
            "en",
            "native_text",
            null,
            new Dictionary<string, string>
            {
                ["contentCardId"] = "card-7"
            },
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-legacy-card",
            "Give the option.",
            new[] { evidence },
            Array.Empty<SourceBackedTraceEvent>());

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("Documented option [E1]", new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.Contains(
            verification.Errors,
            static error => error.Code == "missing_content_card_id");
        Assert.Contains(
            verification.Errors,
            static error => error.Code == "overloaded_chunk_identity");
    }

    [Fact]
    public void Verify_ReportsTheExactEvidenceIdOfACitationOnlyTableCell()
    {
        var evidence = new EvidenceItem(
            "E7",
            "actionable_item",
            "rag.search",
            "documented option",
            "doc-7",
            "Options.pdf",
            "Knowledge/Options.pdf",
            "hash-7",
            "revision-7",
            7,
            7,
            "chunk-7",
            "Documented option with usable content.",
            "Documented option with usable content.",
            0.9,
            1,
            "Knowledge",
            "en",
            "en",
            "native_text",
            (JsonElement?)null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-1",
            "Give me one documented option.",
            new[] { evidence },
            Array.Empty<SourceBackedTraceEvent>());
        var draft = new WriterDraft(
            "| Slot | Option |\n| --- | --- |\n| A | [E7] |",
            new[] { "E7" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            enforceRequestedShape: false);

        var error = Assert.Single(
            verification.Errors,
            static item => item.Code == "citation_only_structured_item");
        Assert.Equal("E7", error.EvidenceId);
    }

    [Fact]
    public void Verify_DoesNotTreatAColonTerminatedTableLabelAsCitationOnly()
    {
        var evidence = new EvidenceItem(
            "E7",
            "actionable_item",
            "rag.search",
            "Conseils :",
            "doc-7",
            "Options.pdf",
            "Knowledge/Options.pdf",
            "hash-7",
            "revision-7",
            7,
            7,
            "chunk-7",
            "Conseils source-backed.",
            "Conseils source-backed.",
            0.9,
            1,
            "Knowledge",
            "fr",
            "fr",
            "native_text",
            (JsonElement?)null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-colon-label",
            "Donne l'element documente.",
            new[] { evidence },
            Array.Empty<SourceBackedTraceEvent>());
        var draft = new WriterDraft(
            "| Slot | Option |\n| --- | --- |\n| A | Conseils : [E7] |",
            new[] { "E7" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            enforceRequestedShape: false);

        Assert.DoesNotContain(
            verification.Errors,
            static item => item.Code == "citation_only_structured_item");
    }

    [Fact]
    public void Verify_rejects_citation_complete_prose_when_the_llm_requested_a_table()
    {
        var intake = BuildStructuredIntake();
        var bundle = BuildBundle(4);
        var draft = new WriterDraft(
            "Alpha current [E1], Alpha target [E2], Beta current [E3], Beta target [E4].",
            new[] { "E1", "E2", "E3", "E4" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1", "E2", "E3", "E4" },
            enforceRequestedShape: true,
            requireEveryAllowedEvidenceIdExactlyOnce: true);

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Errors,
            static error => error.Code == "requested_structure_not_realized");
    }

    [Fact]
    public void Verify_rejects_a_requested_table_with_one_empty_cell()
    {
        var intake = BuildStructuredIntake();
        var bundle = BuildBundle(3);
        var draft = new WriterDraft(
            "| Item | Current | Target |\n"
            + "| --- | --- | --- |\n"
            + "| Alpha | Value 1 [E1] | Value 2 [E2] |\n"
            + "| Beta | Value 3 [E3] | |",
            new[] { "E1", "E2", "E3" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1", "E2", "E3" },
            enforceRequestedShape: true,
            requireEveryAllowedEvidenceIdExactlyOnce: true);

        var error = Assert.Single(
            verification.Errors,
            static item => item.Code == "missing_structured_cell");
        Assert.Contains("Beta/Target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_does_not_semantically_reject_valid_one_word_named_items()
    {
        var intake = BuildStructuredIntake();
        var bundle = BuildBundle(4);
        var draft = new WriterDraft(
            "| Item | Current | Target |\n"
            + "| --- | --- | --- |\n"
            + "| Alpha | Tartiflette [E1] | Veloute [E2] |\n"
            + "| Beta | Couscous [E3] | Tiramisu [E4] |",
            new[] { "E1", "E2", "E3", "E4" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1", "E2", "E3", "E4" },
            enforceRequestedShape: true,
            requireEveryAllowedEvidenceIdExactlyOnce: true);

        Assert.DoesNotContain(
            verification.Errors,
            static error => error.Code == "thin_structured_cell");
        Assert.True(
            verification.IsValid,
            string.Join(", ", verification.Errors.Select(static error => error.Code)));
    }

    [Fact]
    public void Verify_defers_axis_named_canonical_cards_to_the_llm_semantic_review()
    {
        var intake = BuildStructuredIntake();
        var bundle = BuildBundle(4);
        var draft = new WriterDraft(
            "| Item | Current | Target |\n"
            + "| --- | --- | --- |\n"
            + "| Alpha | Current [E1] | Target [E2] |\n"
            + "| Beta | Current [E3] | Target [E4] |",
            new[] { "E1", "E2", "E3", "E4" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1", "E2", "E3", "E4" },
            enforceRequestedShape: true,
            requireEveryAllowedEvidenceIdExactlyOnce: true);

        Assert.DoesNotContain(
            verification.Errors,
            static error => error.Code == "thin_structured_cell");
    }

    [Fact]
    public void Verify_rejects_clustered_citations_when_atomic_claim_locality_is_required()
    {
        var bundle = BuildBundle(2);
        var draft = new WriterDraft(
            "First documented claim and second documented claim [E1][E2].",
            new[] { "E1", "E2" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            allowedEvidenceIds: new[] { "E1", "E2" },
            enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true,
            requireSeparateAtomicClaims: true);

        Assert.False(verification.IsValid);
        Assert.Contains(
            verification.Errors,
            static error => error.Code == "atomic_claim_citations_not_localized");
    }

    [Fact]
    public void Verify_accepts_separate_atomic_claims_in_one_paragraph()
    {
        var bundle = BuildBundle(2);
        var draft = new WriterDraft(
            "First documented claim [E1]. Second documented claim [E2].",
            new[] { "E1", "E2" });

        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            allowedEvidenceIds: new[] { "E1", "E2" },
            enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true,
            requireSeparateAtomicClaims: true);

        Assert.True(
            verification.IsValid,
            string.Join(", ", verification.Errors.Select(static error => error.Code)));
    }

    [Fact]
    public void Verify_accepts_one_exact_citation_from_each_selected_evidence_group()
    {
        var bundle = BuildBundle(3);
        var verification = SourceContractVerifier.Verify(
            new WriterDraft(
                "First selected item uses its exact neighboring proof [E2]. "
                + "Second selected item uses its proof [E3].",
                new[] { "E2", "E3" }),
            bundle,
            allowedEvidenceIds: new[] { "E1", "E2", "E3" },
            enforceRequestedShape: false,
            requiredEvidenceIdGroups: new IReadOnlyList<string>[]
            {
                new[] { "E1", "E2" },
                new[] { "E3" }
            },
            requireCitedAllowedEvidenceIdsAtMostOnce: true);

        Assert.True(
            verification.IsValid,
            string.Join(", ", verification.Errors.Select(static error => error.Code)));
    }

    [Fact]
    public void Verify_rejects_when_a_selected_evidence_group_has_no_citation()
    {
        var bundle = BuildBundle(3);
        var verification = SourceContractVerifier.Verify(
            new WriterDraft(
                "Only the first selected item is realized [E2].",
                new[] { "E2" }),
            bundle,
            allowedEvidenceIds: new[] { "E1", "E2", "E3" },
            enforceRequestedShape: false,
            requiredEvidenceIdGroups: new IReadOnlyList<string>[]
            {
                new[] { "E1", "E2" },
                new[] { "E3" }
            },
            requireCitedAllowedEvidenceIdsAtMostOnce: true);

        var error = Assert.Single(
            verification.Errors,
            static item => item.Code
                == "semantic_selection_group_not_realized");
        Assert.Contains("group 2 (E3)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_rejects_a_repeated_named_item_evidence_marker()
    {
        var bundle = BuildBundle(1);
        var verification = SourceContractVerifier.Verify(
            new WriterDraft(
                "First fact [E1]. Second repetition [E1].",
                new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false,
            requiredEvidenceIdGroups: new IReadOnlyList<string>[]
            {
                new[] { "E1" }
            },
            requireCitedAllowedEvidenceIdsAtMostOnce: true);

        Assert.Contains(
            verification.Errors,
            static item => item.Code
                == "semantic_selection_citation_repeated");
    }

    [Fact]
    public void Verify_does_not_count_a_plain_evidence_label_as_a_second_marker()
    {
        var bundle = BuildBundle(1);
        var verification = SourceContractVerifier.Verify(
            new WriterDraft(
                "The synthetic E1 label identifies the documented fact [E1].",
                new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false,
            requiredEvidenceIdGroups: new IReadOnlyList<string>[]
            {
                new[] { "E1" }
            },
            requireCitedAllowedEvidenceIdsAtMostOnce: true);

        Assert.DoesNotContain(
            verification.Errors,
            static item => item.Code
                == "semantic_selection_citation_repeated");
        Assert.True(
            verification.IsValid,
            string.Join(", ", verification.Errors.Select(static error => error.Code)));
    }

    [Fact]
    public void Verify_accepts_an_explicit_document_reference_at_a_filename_boundary()
    {
        var intake = new SourceBackedIntake(
            "Summarize document-1 in one point.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: "document-1");
        var bundle = BuildBundle(1);

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("Documented value [E1].", new[] { "E1" }),
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.DoesNotContain(
            verification.Errors,
            static error => error.Code == "requested_document_source_mismatch");
    }

    [Fact]
    public void Verify_rejects_same_filename_when_resolved_doc_id_differs()
    {
        var bundle = BuildBundle(1);
        var item = Assert.Single(bundle.Items);
        var intake = new SourceBackedIntake(
            "Summarize document-1.pdf.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: "document-1.pdf",
            RequestedDocumentResolution:
                new SourceBackedDocumentResolutionObservation(
                    "document-1.pdf",
                    SourceBackedDocumentResolutionStatus.Resolved,
                    CatalogObservationComplete: true,
                    new[]
                    {
                        new SourceBackedDocumentResolutionCandidate(
                            "different-doc-id",
                            item.DocPath!,
                            item.DocName!,
                            item.RevisionId,
                            item.SourceHash)
                    },
                    "resolved_exact_current_catalog",
                    1,
                    1,
                    false,
                    1));

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("Documented value [E1].", new[] { "E1" }),
            bundle,
            intake,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.Contains(
            verification.Errors,
            static error => error.Code
                == "requested_document_source_mismatch");
    }

    [Fact]
    public void Verify_requires_exact_disclosure_for_alternative_source_scope()
    {
        var intake = new SourceBackedIntake(
            "Summarize a named file or use alternatives.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: "missing.pdf",
            DocumentScope: SourceBackedDocumentScope.AlternativeSources,
            DocumentScopeDisclosure:
                "The requested file was not found; this uses another source.");

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("Documented value [E1].", new[] { "E1" }),
            BuildBundle(1),
            intake,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.Contains(
            verification.Errors,
            static error => error.Code
                == "alternative_source_scope_disclosure_missing");
    }

    [Fact]
    public void Verify_accepts_real_alternative_identity_with_exact_disclosure()
    {
        const string disclosure =
            "The requested file was not found; this uses another source.";
        var intake = new SourceBackedIntake(
            "Summarize a named file or use alternatives.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: "missing.pdf",
            DocumentScope: SourceBackedDocumentScope.AlternativeSources,
            DocumentScopeDisclosure: disclosure);

        var verification = SourceContractVerifier.Verify(
            new WriterDraft(
                disclosure + " Documented value [E1].",
                new[] { "E1" }),
            BuildBundle(1),
            intake,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.DoesNotContain(
            verification.Errors,
            static error => error.Code
                is "requested_document_source_mismatch"
                or "alternative_source_scope_disclosure_missing");
    }

    private static SourceBackedIntake BuildStructuredIntake()
        => new(
            "Compare the current and target values for Alpha and Beta.",
            "rag.structured",
            Array.Empty<string>(),
            new[]
            {
                "row:Alpha",
                "row:Beta",
                "column:Current",
                "column:Target"
            },
            AllowsPartialAnswer: false,
            Language: "en",
            RowHeaderLabel: "Item");

    private static EvidenceBundle BuildBundle(int count)
        => new(
            "source-contract-verifier-tests",
            "Compare documented values.",
            Enumerable.Range(1, count)
                .Select(static index => new EvidenceItem(
                    "E" + index,
                    "canonical_content_card",
                    "documents.content_cards",
                    "documented value",
                    "doc-" + index,
                    "document-" + index + ".pdf",
                    "Knowledge/document-" + index + ".pdf",
                    new string('a', 63) + index,
                    "revision-active",
                    index,
                    index,
                    null,
                    "Documented value " + index,
                    "Documented value " + index,
                    1,
                    index,
                    "Knowledge",
                    "en",
                    "en",
                    "native",
                    JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            contentCardId = "card-" + index,
                            title = "Documented value " + index,
                            evidence = new
                            {
                                sourceText = "Documented value " + index
                            }
                        }
                    }),
                    new Dictionary<string, string>
                    {
                        ["hasGroundedEvidence"] = "true"
                    },
                    new Dictionary<string, string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>())
                {
                    ContentCardId = "card-" + index
                })
                .ToArray(),
            Array.Empty<SourceBackedTraceEvent>());
}
