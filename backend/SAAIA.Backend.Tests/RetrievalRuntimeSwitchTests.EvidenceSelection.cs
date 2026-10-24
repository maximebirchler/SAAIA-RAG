using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{
    [Fact]
    public void ResolveEmbeddingText_prefers_contextual_text_when_available()
    {
        var contextual = new[]
        {
            new ProjectedContextualTextEntry(0, 1, 2, 3, 4, 5, "Document: CEN.pdf\nExcerpt:\nContextualized", 38, 4, [1])
        };

        var map = IngestionWorker.BuildContextualTextMap(contextual);
        var resolved = IngestionWorker.ResolveEmbeddingText(3, "Raw chunk text", map);

        Assert.Equal("Document: CEN.pdf\nExcerpt:\nContextualized", resolved);
    }

    [Fact]
    public void ResolveEmbeddingText_falls_back_to_chunk_text_when_context_is_missing()
    {
        var resolved = IngestionWorker.ResolveEmbeddingText(
            7,
            "Raw chunk text",
            IngestionWorker.BuildContextualTextMap([]));

        Assert.Equal("Raw chunk text", resolved);
    }

    [Fact]
    public void Content_card_ids_fold_latin_ligatures_like_title_anchors()
    {
        var profileId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var ligatureId = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "Bœuf à l'aïoli");
        var asciiId = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "Boeuf a l'aioli");

        Assert.Equal(asciiId, ligatureId);
    }

    [Fact]
    public void TeiClient_formats_e5_query_and_passage_inputs()
    {
        Assert.Equal(
            "query: sonde de rotissage",
            TeiClient.FormatEmbeddingInput("intfloat/multilingual-e5-base", "sonde de rotissage", TeiClient.EmbeddingInputKind.Query));
        Assert.Equal(
            "passage: texte source",
            TeiClient.FormatEmbeddingInput("intfloat/multilingual-e5-base", "texte source", TeiClient.EmbeddingInputKind.Passage));
        Assert.Equal(
            "passage: already formatted",
            TeiClient.FormatEmbeddingInput("intfloat/multilingual-e5-base", "query: already formatted", TeiClient.EmbeddingInputKind.Passage));
        Assert.Equal(
            "query: already formatted",
            TeiClient.FormatEmbeddingInput("intfloat/multilingual-e5-base", "passage: already formatted", TeiClient.EmbeddingInputKind.Query));
        Assert.Equal(
            "texte source",
            TeiClient.FormatEmbeddingInput("sentence-transformers/all-MiniLM-L6-v2", "texte source", TeiClient.EmbeddingInputKind.Passage));
    }

    [Fact]
    public void Dense_matches_require_matching_e5_embedding_format()
    {
        var e5Match = BuildDenseMatch("intfloat/multilingual-e5-base", "e5_passage_v1");
        var rawMatch = BuildDenseMatch("sentence-transformers/all-MiniLM-L6-v2", "raw_passage_v1");
        var legacyMatch = BuildDenseMatch(null, null);

        Assert.True(RetrievalChannels.IsDenseMatchEmbeddingCompatible(e5Match, "intfloat/multilingual-e5-base"));
        Assert.False(RetrievalChannels.IsDenseMatchEmbeddingCompatible(rawMatch, "intfloat/multilingual-e5-base"));
        Assert.False(RetrievalChannels.IsDenseMatchEmbeddingCompatible(legacyMatch, "intfloat/multilingual-e5-base"));
        Assert.True(RetrievalChannels.IsDenseMatchEmbeddingCompatible(rawMatch, "sentence-transformers/all-MiniLM-L6-v2"));
        Assert.False(RetrievalChannels.IsDenseMatchEmbeddingCompatible(legacyMatch, "sentence-transformers/all-MiniLM-L6-v2"));
    }

    [Fact]
    public void Dense_matches_reject_navigation_chunks_even_when_embedding_format_matches()
    {
        var navigationByRole = BuildDenseMatch(
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            contentRole: RetrievalContentClassifier.NavigationRole);
        var navigationByChunkType = BuildDenseMatch(
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            chunkType: RetrievalContentClassifier.NavigationChunkType);
        var mixedContent = BuildDenseMatch(
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole);

        Assert.False(RetrievalChannels.IsDenseMatchEmbeddingCompatible(navigationByRole, "intfloat/multilingual-e5-base"));
        Assert.False(RetrievalChannels.IsDenseMatchEmbeddingCompatible(navigationByChunkType, "intfloat/multilingual-e5-base"));
        Assert.True(RetrievalChannels.IsDenseMatchEmbeddingCompatible(mixedContent, "intfloat/multilingual-e5-base"));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("source_exploration", null, true)]
    [InlineData("evidence_exploration", false, true)]
    [InlineData("research", false, true)]
    [InlineData("balanced", false, false)]
    [InlineData(null, null, false)]
    public void ShouldUseResearchSurfaces_respects_explicit_flag_and_exploration_modes(
        string? researchMode,
        bool? includeResearchSurfaces,
        bool expected)
    {
        var request = new RagSearchRequestDto(
            Query: "broad source-backed planning",
            ResearchMode: researchMode,
            IncludeResearchSurfaces: includeResearchSurfaces);

        Assert.Equal(expected, RetrievalQueryPolicy.ShouldUseResearchSurfaces(request));
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_keeps_short_acronym_operands()
    {
        var operands = RetrievalQueryPolicy.ExtractBareVsComparativeOperands(
            "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?");

        Assert.Contains("uk", operands);
        Assert.Contains("edp", operands);
        Assert.DoesNotContain("fournisseur", operands);
        Assert.DoesNotContain("conduite fournisseur uk", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_prefers_right_revision_label_after_concept_word()
    {
        var operands = RetrievalQueryPolicy.ExtractBareVsComparativeOperands(
            "Fais un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6.");

        Assert.Contains("ar6", operands);
        Assert.DoesNotContain("synthese", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_handles_conjunction_comparisons_with_short_codes()
    {
        var operands = RetrievalQueryPolicy.ExtractBareVsComparativeOperands(
            "Compare SSDF et control baselines : est-ce la meme granularite d'exigence ?");

        Assert.Contains("ssdf", operands);
        Assert.Contains("control baselines", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_skips_document_descriptor_before_short_code()
    {
        var operands = RetrievalQueryPolicy.ExtractBareVsComparativeOperands(
            "Compare les annexes IPCC WG3 et le rapport SDG : quelles informations sont chiffrees ?");

        Assert.Contains("wg3", operands);
        Assert.Contains("sdg", operands);
        Assert.DoesNotContain("rapport", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_keeps_shared_short_suffix_phrases()
    {
        var operands = RetrievalQueryPolicy.ExtractBareVsComparativeOperands(
            "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?");

        Assert.Contains("performance audit", operands);
        Assert.Contains("compliance audit", operands);
        Assert.DoesNotContain("audit", operands);
        Assert.DoesNotContain("compliance", operands);
    }

    [Theory]
    [InlineData("Un client cite une exigence NIST 800-53 r4, mais notre doc contient aussi r5. Comment repondre ?")]
    [InlineData("On me demande un process d'incident response : dois-je citer la r2 ou la r3 ?")]
    [InlineData("On me dit que deux rapports climat ne disent pas pareil : comment expliquer sans simplifier abusivement ?")]
    [InlineData("Comment repondre si l'ancienne version est plus detaillee mais la nouvelle est plus recente ?")]
    [InlineData("Peux-tu dire que l'AR6 invalide totalement l'AR5 sans verifier le contexte ?")]
    [InlineData("Est-ce que tous les controles r4 ont exactement le meme identifiant et contenu en r5 ?")]
    [InlineData("Peux-tu fusionner les etapes de la r2 et de la r3 dans une seule procedure sans indiquer les sources ?")]
    public void ShouldPreferComparativeDocumentDiversity_detects_operational_version_conflict_scenarios(string query)
    {
        Assert.True(RetrievalQueryPolicy.ShouldPreferComparativeDocumentDiversity(query));
    }

    [Fact]
    public void Operational_version_operands_are_not_treated_as_extensionless_document_hints()
    {
        const string query = "Peux-tu fusionner les etapes de la r2 et de la r3 dans une seule procedure sans indiquer les sources ?";

        var terms = RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(query);

        Assert.Contains("r2", terms);
        Assert.Contains("r3", terms);
        Assert.False(RetrievalQueryPolicy.ShouldUseExplicitDocumentScopedBackfillRoute(query, "etapes procedure"));
    }

    [Fact]
    public void ShouldUseGenericVersionConflictOverviewRecovery_detects_unscoped_old_new_version_question()
    {
        Assert.True(RetrievalQueryPolicy.ShouldUseGenericVersionConflictOverviewRecovery(
            "Comment repondre si l'ancienne version est plus detaillee mais la nouvelle est plus recente ?"));
        Assert.False(RetrievalQueryPolicy.ShouldUseGenericVersionConflictOverviewRecovery(
            "Peux-tu dire que l'AR6 invalide totalement l'AR5 sans verifier le contexte ?"));
    }

    [Fact]
    public void BuildComparativeVersionCounterpartBackfillQueries_extracts_non_version_side()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeVersionCounterpartBackfillQueries(
            "Donne une reponse prudente a un utilisateur qui demande \u201cquelle version est correcte ?\u201d pour objectif 1,5\u00b0C vs synthese AR6.");

        Assert.Contains("1 5 c", queries);
        Assert.DoesNotContain(queries, query => query.Contains("quelle version", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildComparativeVersionCounterpartBackfillQueries_repairs_mojibake_temperature_token()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeVersionCounterpartBackfillQueries(
            "objectif 1,5Â°C vs synthÃ¨se AR6");

        Assert.Contains("1 5 c", queries);
        Assert.DoesNotContain(queries, query => query.Contains("5a", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveInternalSelectionTopK_overfetches_precise_title_requests_without_changing_contract_topk()
    {
        var resolved = RetrievalQueryPolicy.ResolveInternalSelectionTopK(
            "Tu peux me faire une fiche claire pour \u00ab Asperges vertes au miel \u00bb : ingredients, etapes, temps et source ?",
            requestedTopK: 8,
            maxTopK: 128,
            hasDocScope: false,
            hasExplicitFileDocumentHint: false,
            mode: "balanced");

        Assert.Equal(16, resolved);
    }

    [Fact]
    public void ResolveInternalSelectionTopK_keeps_explicit_file_lookup_lean()
    {
        var resolved = RetrievalQueryPolicy.ResolveInternalSelectionTopK(
            "Reponds avec `Contract.pdf` comme source.",
            requestedTopK: 8,
            maxTopK: 128,
            hasDocScope: false,
            hasExplicitFileDocumentHint: true,
            mode: "balanced");

        Assert.Equal(8, resolved);
    }

    [Fact]
    public void PruneUnrelatedReferenceVersionGroupSelections_keeps_named_reference_family()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("new", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("old", docPath: "Docs/ISO 13849-1 2015 Safety.pdf", chunkId: "old"),
            TestMatch("noise", docPath: "Docs/FD CEN TR 15281 2023 Inerting.pdf", chunkId: "noise"),
            TestMatch("other", docPath: "Docs/ISO 14122-4 2010 AC.pdf", chunkId: "other")
        };

        RetrievalSelection.PruneUnrelatedReferenceVersionGroupSelections(
            "Compare ISO 13849-1:2015 et ISO 13849-1:2023.",
            selected);

        Assert.Equal(2, selected.Count);
        Assert.All(selected, match => Assert.Contains("ISO 13849-1", match.DocPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PruneUnrelatedReferenceVersionGroupSelections_keeps_parts_for_family_query()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("part one", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("part two", docPath: "Docs/ISO 13849-2 2012 Validation.pdf", chunkId: "part-two"),
            TestMatch("noise", docPath: "Docs/ISO 14122-4 2010 AC.pdf", chunkId: "noise")
        };

        RetrievalSelection.PruneUnrelatedReferenceVersionGroupSelections(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?",
            selected);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, match => match.DocPath!.Contains("ISO 13849-1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selected, match => match.DocPath!.Contains("ISO 13849-2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PruneUnrelatedReferenceVersionGroupSelections_prefers_dominant_version_family_when_query_names_only_operands()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("draft amendment", docPath: "Docs/EN 1005-3 2002 prA1.pdf"),
            TestMatch("amended", docPath: "Docs/EN 1005-3 2002 A1 2008.pdf", chunkId: "a1"),
            TestMatch("base", docPath: "Docs/EN 1005-3 2002 Base.pdf", chunkId: "base"),
            TestMatch("same year noise", docPath: "Docs/ISO 14159 2008 Hygiene.pdf", chunkId: "noise")
        };

        RetrievalSelection.PruneUnrelatedReferenceVersionGroupSelections(
            "Compare la version 2002, le prA1 et la version 2008+A1.",
            selected);

        Assert.Equal(3, selected.Count);
        Assert.All(selected, match => Assert.Contains("EN 1005-3", match.DocPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PruneUnrequestedReferenceVersionYearSelections_keeps_only_explicit_single_year()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("requested", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("old", docPath: "Docs/ISO 13849-1 2015 Safety.pdf", chunkId: "old")
        };

        RetrievalSelection.PruneUnrequestedReferenceVersionYearSelections(
            "Pour ISO 13849-1 2023, prouve que la reponse utilise bien cette version.",
            selected);

        Assert.Single(selected);
        Assert.Contains("2023", selected[0].DocPath, StringComparison.Ordinal);
    }

    [Fact]
    public void PruneUnrequestedReferenceVersionYearSelections_preserves_explicit_comparisons()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("new", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("old", docPath: "Docs/ISO 13849-1 2015 Safety.pdf", chunkId: "old")
        };

        RetrievalSelection.PruneUnrequestedReferenceVersionYearSelections(
            "Compare ISO 13849-1:2015 et ISO 13849-1:2023.",
            selected);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectionsCoverReferenceVersionOperandContent_requires_content_for_each_requested_year()
    {
        var onlyProfileForNewVersion = new List<RagMatch>
        {
            TestMatch(
                "new profile",
                docPath: "Docs/ISO 13849-1 2023 Safety.pdf",
                chunkType: "document_profile",
                embeddingBasis: "document_profile_v1"),
            TestMatch("old content", docPath: "Docs/ISO 13849-1 2015 Safety.pdf", chunkId: "old")
        };
        var contentForBothVersions = new List<RagMatch>
        {
            TestMatch("new content", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("old content", docPath: "Docs/ISO 13849-1 2015 Safety.pdf", chunkId: "old")
        };

        var query = "Compare ISO 13849-1:2015 et ISO 13849-1:2023.";

        Assert.False(RetrievalSelection.SelectionsCoverReferenceVersionOperandContent(query, onlyProfileForNewVersion));
        Assert.True(RetrievalSelection.SelectionsCoverReferenceVersionOperandContent(query, contentForBothVersions));
    }

    [Fact]
    public void ShouldRunScopedCatalogEmptySelectionRecovery_detects_broad_category_synthesis()
    {
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je veux expliquer a quelqu un ce qui s est passe entre septembre 2024 et octobre 2025. Fais un resume comprehensible et source.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux reunions ou le diagnostic semble evoluer ou se nuancer.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux réunions où le diagnostic semble évoluer ou se nuancer. Explique si c’est une contradiction réelle ou seulement un changement de contexte.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je crois que le corpus demontre toujours `mode d emploi detaille`. Verifie si c est prouve, limite, recommande ou non demontre.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je crois que le corpus d?montre toujours `mode d emploi d?taill?`. V?rifie si c?est prouv?, limit?, recommand? ou non d?montr?.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "I think the corpus always proves `detailed operating instructions`. Verify whether it is proven, limited, recommended, or not demonstrated.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.False(RetrievalQueryPolicy.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux reunions ou le diagnostic semble evoluer ou se nuancer.",
            hasCategoryFilter: false,
            mode: "balanced"));
    }

    [Fact]
    public void SelectionsCoverCompactDocumentCodePhrase_checks_document_title_signal()
    {
        var selected = new[]
        {
            TestMatch(
                "profile",
                docPath: "Docs/IPCC_AR6_WG3_Full_Report_Annexes.pdf",
                embeddingBasis: "document_profile_v1")
        };

        Assert.True(RetrievalLexicalExpansion.SelectionsCoverCompactDocumentCodePhrase("wg3", selected));
        Assert.False(RetrievalLexicalExpansion.SelectionsCoverCompactDocumentCodePhrase("sdg", selected));
    }

    [Fact]
    public void ShouldAllowCompactDocumentCodeEmptyRecovery_allows_implicit_document_hint_without_filename_surface()
    {
        Assert.True(RetrievalLexicalExpansion.ShouldAllowCompactDocumentCodeEmptyRecovery(
            "Donne-moi une statistique precise du WG3 sans citation de tableau.",
            hasExplicitFileDocumentHint: true));

        Assert.False(RetrievalLexicalExpansion.ShouldAllowCompactDocumentCodeEmptyRecovery(
            "Donne-moi une statistique precise dans `WG3.pdf`.",
            hasExplicitFileDocumentHint: true));
    }

    [Fact]
    public void SelectionsCoverStandardReferencePhrases_requires_each_requested_reference()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Compare les procedures NIST 800-53A avec les appendices 800-53r5.");
        var selected = new[]
        {
            BuildRuntimeSelectionMatch(
                0.92,
                "doc-r5",
                "Docs/NIST_SP_800_53r5_Appendices.pdf",
                0,
                "SP 800-53r5 risk assessment appendix.")
        };

        Assert.False(RetrievalLexicalExpansion.SelectionsCoverStandardReferencePhrases(phrases, selected));
    }

    [Fact]
    public void PruneUnrequestedStandardReferenceSelections_removes_other_standard_documents()
    {
        var selected = new List<RagMatch>
        {
            BuildRuntimeSelectionMatch(
                0.92,
                "doc-53a",
                "Docs/NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf",
                0,
                "Assessment procedures."),
            BuildRuntimeSelectionMatch(
                0.91,
                "doc-53r5",
                "Docs/NIST_SP_800_53r5_Appendices.pdf",
                1,
                "Controls appendix."),
            BuildRuntimeSelectionMatch(
                0.90,
                "doc-160",
                "Docs/NIST_SP_800_160v1r1_SSE_Appendices.pdf",
                2,
                "Different standard family.")
        };

        RetrievalLexicalExpansion.PruneUnrequestedStandardReferenceSelections(
            "Compare les procedures NIST 800-53A avec les appendices 800-53r5.",
            selected);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, match => match.DocName == "NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf");
        Assert.Contains(selected, match => match.DocName == "NIST_SP_800_53r5_Appendices.pdf");
        Assert.DoesNotContain(selected, match => match.DocName == "NIST_SP_800_160v1r1_SSE_Appendices.pdf");
    }

    [Fact]
    public void BuildDocumentStatusOperandCompanionBackfillQueries_adds_compared_code_operand()
    {
        var baseline = TestMatch(
            "Control baselines.",
            docPath: "Docs/NIST_SP_800_53B_Control_Baselines.pdf",
            score: 0.90);
        var queries = RetrievalQueryPolicy.BuildDocumentStatusOperandCompanionBackfillQueries(
            "Compare SSDF et control baselines : est-ce la meme granularite d'exigence ?",
            ["baseline", "baselines"],
            [baseline]);

        Assert.Contains("ssdf", queries);
        Assert.DoesNotContain("nist sp 800 53", queries);
    }

    [Fact]
    public void BuildDocumentStatusOperandCompanionBackfillQueries_adds_reference_family_for_status_document()
    {
        var baseline = TestMatch(
            "Control baselines.",
            docPath: "Docs/NIST_SP_800_53B_Control_Baselines.pdf",
            score: 0.90);
        var queries = RetrievalQueryPolicy.BuildDocumentStatusOperandCompanionBackfillQueries(
            "Je dois justifier une baseline de securite dans un dossier projet. Quels documents et annexes utiliser ?",
            ["baseline", "baselines"],
            [baseline]);

        Assert.Contains("nist sp 800 53", queries);
    }

    [Fact]
    public void BuildDocumentStatusOperandCompanionBackfillQueries_drops_year_for_unparted_standard_corrigendum()
    {
        var correction = TestMatch(
            "Berichtigung correction.",
            docPath: "Docs/ISO 14159 2009 Berichtigung 1.pdf",
            score: 0.90);
        var queries = RetrievalQueryPolicy.BuildDocumentStatusOperandCompanionBackfillQueries(
            "Le Berichtigung contient-il tout le contenu de la norme complete ?",
            ["berichtigung"],
            [correction]);

        Assert.Contains("iso 14159", queries);
    }

    [Fact]
    public void PrioritizeDocumentStatusOperandSelections_puts_status_document_before_family_document()
    {
        var matches = new List<RagMatch>
        {
            TestMatch("Main standard", docPath: "Docs/ISO 14159 2008 Main.pdf", score: 0.96),
            TestMatch("Correction", docPath: "Docs/ISO 14159 2009 Berichtigung 1.pdf", chunkId: "status", score: 0.70)
        };

        var ordered = RetrievalLexicalExpansion.PrioritizeDocumentStatusOperandSelections(
            "Le Berichtigung contient-il tout le contenu de la norme complete ?",
            ["berichtigung"],
            matches);

        Assert.Equal("ISO 14159 2009 Berichtigung 1.pdf", ordered[0].DocName);
    }

    [Fact]
    public void PrioritizeDocumentStatusOperandSelections_prefers_topic_matching_family()
    {
        var matches = new List<RagMatch>
        {
            TestMatch("Generic ladder requirements.", docPath: "Docs/ISO 14122-4 2004 Echelles fixes.pdf", score: 0.90),
            TestMatch("Electrotechnical lifting equipment requirements.", docPath: "Docs/EN 13135-1 2004 Appareils de levage equipement electrotechnique.pdf", chunkId: "main", score: 0.80),
            TestMatch("AC correction for EN 13135-1.", docPath: "Docs/EN 13135-1 2004 AC.pdf", chunkId: "ac", score: 0.78)
        };

        var ordered = RetrievalLexicalExpansion.PrioritizeDocumentStatusOperandSelections(
            "Je dois appliquer une exigence electrotechnique de levage : faut-il regarder le document principal ou l'AC ?",
            ["ac"],
            matches);

        Assert.Equal("EN 13135-1 2004 Appareils de levage equipement electrotechnique.pdf", ordered[0].DocName);
        Assert.Contains(ordered.Take(3), match => match.DocName == "EN 13135-1 2004 AC.pdf");
        Assert.DoesNotContain(ordered, match => (match.DocName ?? string.Empty).StartsWith("ISO 14122", StringComparison.Ordinal));
    }

    [Fact]
    public void PrioritizeDocumentStatusOperandSelections_matches_simple_plural_topic_variants()
    {
        var matches = new List<RagMatch>
        {
            TestMatch("AC for fixed ladders.", docPath: "Docs/ISO 14122-4 2010 AC Echelles fixes.pdf", score: 0.70),
            TestMatch("Generic lifting AC.", docPath: "Docs/EN 13135-2 2005 AC.pdf", chunkId: "lifting", score: 0.84),
            TestMatch("Hygiene correction.", docPath: "Docs/ISO 14159 2009 Berichtigung 1.pdf", chunkId: "hygiene", score: 0.82)
        };

        var ordered = RetrievalLexicalExpansion.PrioritizeDocumentStatusOperandSelections(
            "Je dois repondre sur une echelle fixe : comment signaler qu'il existe un corrigendum ?",
            ["corrigendum", "ac"],
            matches);

        Assert.Equal("ISO 14122-4 2010 AC Echelles fixes.pdf", ordered[0].DocName);
    }

    [Fact]
    public void PrioritizeDocumentStatusOperandSelections_prefers_status_companion_for_corrigendum_existence_question()
    {
        var matches = new List<RagMatch>
        {
            TestMatch("Main fixed ladder requirements.", docPath: "Docs/ISO 14122-4 2004 Echelles fixes.pdf", score: 0.90),
            TestMatch("Amendment status.", docPath: "Docs/ISO 14122-4 2010 AC.pdf", chunkId: "status", score: 0.70),
            TestMatch("Generic lifting AC.", docPath: "Docs/EN 13135-2 2005 AC.pdf", chunkId: "lifting", score: 0.84)
        };

        var ordered = RetrievalLexicalExpansion.PrioritizeDocumentStatusOperandSelections(
            "Je dois repondre sur une echelle fixe : comment signaler qu'il existe un corrigendum ?",
            ["corrigendum", "ac"],
            matches);

        Assert.Equal("ISO 14122-4 2010 AC.pdf", ordered[0].DocName);
        Assert.Contains(ordered.Take(2), match => match.DocName == "ISO 14122-4 2004 Echelles fixes.pdf");
        Assert.DoesNotContain(ordered, match => (match.DocName ?? string.Empty).StartsWith("EN 13135", StringComparison.Ordinal));
    }

    [Fact]
    public void ReattachDocumentStatusOperandCompanions_restores_status_document_for_selected_family()
    {
        var main = TestMatch("Main fixed ladder requirements.", docPath: "Docs/ISO 14122-4 2004 Moyens d'acces permanents Echelles fixes.pdf", score: 0.90);
        var status = TestMatch("Amendment status.", docPath: "Docs/ISO 14122-4 2010 AC.pdf", chunkId: "status", score: 0.70);
        var noise = TestMatch("Generic lifting AC.", docPath: "Docs/EN 13135-2 2005 AC.pdf", chunkId: "noise", score: 0.84);
        var selected = new List<RagMatch> { main };
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RetrievalSelection.BuildMatchDedupKey(main)
        };

        RetrievalLexicalExpansion.ReattachDocumentStatusOperandCompanions(
            "Je dois repondre sur une echelle fixe : comment signaler qu'il existe un corrigendum ?",
            ["corrigendum", "ac"],
            [status, main, noise],
            selected,
            selectedKeys,
            topK: 8);

        Assert.Equal("ISO 14122-4 2010 AC.pdf", selected[0].DocName);
        Assert.Equal("ISO 14122-4 2004 Moyens d'acces permanents Echelles fixes.pdf", selected[1].DocName);
        Assert.DoesNotContain(selected, match => match.DocName == "EN 13135-2 2005 AC.pdf");
    }

    [Fact]
    public void ShouldSuppressUnscopedAmbiguousDocumentReferenceSources_detects_bare_status_or_deictic_queries()
    {
        Assert.True(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu appliquer un AC tout seul sans citer la norme principale ?",
            docId: null,
            docPath: null));
        Assert.True(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu comparer ce document à une ancienne version absente du dossier ?",
            docId: null,
            docPath: null));
    }

    [Fact]
    public void ShouldSuppressUnscopedAmbiguousDocumentReferenceSources_preserves_scoped_or_anchored_queries()
    {
        Assert.False(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu comparer ce document à une ancienne version absente du dossier ?",
            docId: "doc-1",
            docPath: null));
        Assert.False(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Le prA1 remplace-t-il automatiquement le document 2008+A1 ?",
            docId: null,
            docPath: null));
        Assert.False(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Je dois appliquer une exigence electrotechnique de levage : faut-il regarder le document principal ou l'AC ?",
            docId: null,
            docPath: null));
        Assert.False(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Quelles sources citer separement pour ISO 13849-1/2 ?",
            docId: null,
            docPath: null));
    }

    [Theory]
    [InlineData("Quelles normes de securite instrumentee sont citees autour de l'inertage dans ce document ?")]
    [InlineData("Le client veut comprendre comment on surveille l'oxygene dans ce document ?")]
    public void ShouldSuppressUnscopedAmbiguousDocumentReferenceSources_preserves_substantive_document_questions(
        string query)
    {
        Assert.False(RetrievalLexicalExpansion.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            query,
            docId: null,
            docPath: null));
    }

    [Fact]
    public void SelectionsCoverStandardReferencePhrases_detects_missing_slash_part()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Quelles sources citer separement pour ISO 13849-1/2 ?");
        var onlyPartOne = new List<RagMatch>
        {
            TestMatch("Part 1", docPath: "Docs/ISO 13849-1 2023 Safety.pdf")
        };
        var bothParts = new List<RagMatch>
        {
            TestMatch("Part 1", docPath: "Docs/ISO 13849-1 2023 Safety.pdf"),
            TestMatch("Part 2", docPath: "Docs/ISO 13849-2 2012 Validation.pdf", chunkId: "part2")
        };

        Assert.False(RetrievalLexicalExpansion.SelectionsCoverStandardReferencePhrases(phrases, onlyPartOne));
        Assert.True(RetrievalLexicalExpansion.SelectionsCoverStandardReferencePhrases(phrases, bothParts));
    }

    [Fact]
    public void ExtractReferenceVersionComparisonOperands_reads_years_and_amendments()
    {
        var operands = RetrievalQueryPolicy.ExtractReferenceVersionComparisonOperands(
            "Compare la version 2002, le prA1 et la version 2008+A1 sans mélanger les textes.");

        Assert.Contains("2002", operands);
        Assert.Contains("pr a1", operands);
        Assert.Contains("2008", operands);
        Assert.Contains("2008 a1", operands);
    }

    [Fact]
    public void InferReferenceVersionOperandGroupKeys_uses_dominant_reference_family()
    {
        const string query = "Compare la version 2002, le prA1 et la version 2008+A1.";
        var matches = new List<RagMatch>
        {
            TestMatch(
                "Profile EN 1005-3 2002.",
                docPath: "Docs/EN 1005-3 2002.pdf",
                chunkId: "en-1005-3-2002",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile"),
            TestMatch(
                "Profile EN 1005-3 2002 prA1.",
                docPath: "Docs/EN 1005-3 2002 prA1.pdf",
                chunkId: "en-1005-3-pra1",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile"),
            TestMatch(
                "Distractor.",
                docPath: "Docs/EN 13135-1 2004.pdf",
                chunkId: "distractor")
        };

        var groupKeys = RetrievalQueryPolicy.InferReferenceVersionOperandGroupKeys(query, matches);

        Assert.Equal("en 1005 3", Assert.Single(groupKeys));
    }

    [Fact]
    public void InferReferenceVersionOperandGroupKeys_prefers_reference_named_in_query()
    {
        const string query = "Compare ISO 13849-1:2015 et ISO 13849-1:2023.";
        var matches = new List<RagMatch>
        {
            TestMatch(
                "Potentially explosive atmospheres 2023.",
                docPath: "Docs/FD CEN TR 15281 2023 Guidance.pdf",
                chunkId: "distractor-2023"),
            TestMatch(
                "ISO 13849-1 2023 general principles.",
                docPath: "Docs/ISO 13849-1 2023 General principles.pdf",
                chunkId: "iso-2023"),
            TestMatch(
                "ISO 13849-1 2015 general principles.",
                docPath: "Docs/ISO 13849-1 2015 General principles.pdf",
                chunkId: "iso-2015")
        };

        var groupKeys = RetrievalQueryPolicy.InferReferenceVersionOperandGroupKeys(query, matches);

        Assert.Equal("iso 13849 1", Assert.Single(groupKeys));
    }

    [Fact]
    public void PruneSupersededReferenceVersionSelections_prefers_latest_when_year_is_not_requested()
    {
        var oldPartOne = TestMatch(
            "ISO 13849-1 2015 general principles for design.",
            docPath: "Standards/ISO 13849-1 2015 General principles.pdf",
            chunkId: "old-part-one");
        var latestPartOne = TestMatch(
            "ISO 13849-1 2023 general principles for design.",
            docPath: "Standards/ISO 13849-1 2023 General principles.pdf",
            chunkId: "latest-part-one");
        var selected = new List<RagMatch> { oldPartOne, latestPartOne };

        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Est-ce que tu peux me donner la règle ISO 13849-1 sans dire de quelle année elle vient ?",
            selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "old-part-one");
        Assert.Contains(selected, match => match.ChunkId == "latest-part-one");
    }

    [Fact]
    public void PruneSupersededReferenceVersionSelections_preserves_separate_source_intent()
    {
        var oldPartOne = TestMatch(
            "ISO 13849-1 2015 general principles for design.",
            docPath: "Standards/ISO 13849-1 2015 General principles.pdf",
            chunkId: "old-part-one");
        var latestPartOne = TestMatch(
            "ISO 13849-1 2023 general principles for design.",
            docPath: "Standards/ISO 13849-1 2023 General principles.pdf",
            chunkId: "latest-part-one");
        var selected = new List<RagMatch> { oldPartOne, latestPartOne };

        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Quelles sources dois-tu citer séparément pour répondre correctement à cette question sur ISO 13849-1 ?",
            selected);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void PruneSupersededReferenceVersionSelections_keeps_latest_same_standard_part()
    {
        var oldPartOne = TestMatch(
            "ISO 13849-1 2015 general principles for design.",
            docPath: "Standards/ISO 13849-1 2015 General principles.pdf",
            chunkId: "old-part-one");
        var latestPartOne = TestMatch(
            "ISO 13849-1 2023 general principles for design.",
            docPath: "Standards/ISO 13849-1 2023 General principles.pdf",
            chunkId: "latest-part-one");
        var partTwo = TestMatch(
            "ISO 13849-2 2012 validation.",
            docPath: "Standards/ISO 13849-2 2012 Validation.pdf",
            chunkId: "part-two");
        var selected = new List<RagMatch> { oldPartOne, latestPartOne, partTwo };

        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?",
            selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "old-part-one");
        Assert.Contains(selected, match => match.ChunkId == "latest-part-one");
        Assert.Contains(selected, match => match.ChunkId == "part-two");
    }

    [Fact]
    public void PruneSupersededReferenceVersionSelections_keeps_requested_year()
    {
        var oldPartOne = TestMatch(
            "ISO 13849-1 2015 general principles for design.",
            docPath: "Standards/ISO 13849-1 2015 General principles.pdf",
            chunkId: "old-part-one");
        var latestPartOne = TestMatch(
            "ISO 13849-1 2023 general principles for design.",
            docPath: "Standards/ISO 13849-1 2023 General principles.pdf",
            chunkId: "latest-part-one");
        var selected = new List<RagMatch> { oldPartOne, latestPartOne };

        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Compare ISO 13849-1 2015 et ISO 13849-1 2023.",
            selected);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void PruneSupersededReferenceVersionSelections_prefers_latest_generic_same_title_without_year()
    {
        var archived = TestMatch(
            "Control Manual 2021 operating limit: 120 units.",
            docPath: "Guides/Archive/Control Manual 2021.pdf",
            chunkId: "archived");
        var current = TestMatch(
            "Control Manual 2024 operating limit: 150 units.",
            docPath: "Guides/Current/Control Manual 2024.pdf",
            chunkId: "current");
        var selected = new List<RagMatch> { archived, current };

        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Quelle limite appliquer dans Control Manual ?",
            selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "archived");
        Assert.Contains(selected, match => match.ChunkId == "current");
    }

    [Fact]
    public void PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested_keeps_generic_archive_request()
    {
        var archived = TestMatch(
            "Control Manual 2021 operating limit: 120 units.",
            docPath: "Guides/Archive/Control Manual 2021.pdf",
            chunkId: "archived");
        var current = TestMatch(
            "Control Manual 2024 operating limit: 150 units.",
            docPath: "Guides/Current/Control Manual 2024.pdf",
            chunkId: "current");
        var selected = new List<RagMatch> { current, archived };

        RetrievalSelection.PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(
            "Retrouve l'ancienne version archivee de Control Manual.",
            selected);
        RetrievalSelection.PruneSupersededReferenceVersionSelections(
            "Retrouve l'ancienne version archivee de Control Manual.",
            selected);

        Assert.Contains(selected, match => match.ChunkId == "archived");
        Assert.DoesNotContain(selected, match => match.ChunkId == "current");
    }

    [Fact]
    public void Reference_version_pruning_keeps_distinct_standard_parts_requested_together()
    {
        var assessment = TestMatch(
            text: "NIST SP 800-53A assessment procedures describe assessment objectives and methods.",
            docPath: "Docs/NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf",
            chunkId: "nist-53a",
            embeddingBasis: "standard_reference_document_name_v1");
        var appendices = TestMatch(
            text: "NIST SP 800-53r5 appendices describe controls, control enhancements and related assessment concepts.",
            docPath: "Docs/NIST_SP_800_53r5_Appendices.pdf",
            chunkId: "nist-53r5",
            embeddingBasis: "standard_reference_document_name_v1");
        var selected = new List<RagMatch> { assessment, appendices };
        var query = "Compare les procedures NIST 800-53A avec les appendices 800-53r5.";

        RetrievalSelection.PruneUnrelatedReferenceVersionGroupSelections(query, selected);
        RetrievalSelection.PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(query, selected);
        RetrievalSelection.PruneUnrequestedReferenceVersionYearSelections(query, selected);
        RetrievalSelection.PruneSupersededReferenceVersionSelections(query, selected);

        Assert.Contains(selected, match => match.ChunkId == "nist-53a");
        Assert.Contains(selected, match => match.ChunkId == "nist-53r5");
    }

    [Fact]
    public void Late_cleanup_keeps_standard_reference_document_name_backfills()
    {
        var assessment = TestMatch(
            text: "Thus, until each publication is revised.",
            embedText: "NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf\nThus, until each publication is revised.",
            docPath: "Docs/NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf",
            chunkId: "nist-53a",
            embeddingBasis: "standard_reference_document_name_v1",
            chunkType: "document_profile",
            score: 0.72);
        var appendices = TestMatch(
            text: "SP.800-53r5 risk assessment control appendix.",
            embedText: "NIST_SP_800_53r5_Appendices.pdf\nSP.800-53r5 risk assessment control appendix.",
            docPath: "Docs/NIST_SP_800_53r5_Appendices.pdf",
            chunkId: "nist-53r5",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.91);
        var selected = new List<RagMatch> { appendices, assessment };
        var query = "Compare les procedures NIST 800-53A avec les appendices 800-53r5.";

        RetrievalSelection.CleanupLateSelectionBackfills(query, selected);
        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Contains(selected, match => match.ChunkId == "nist-53a");
        Assert.Contains(selected, match => match.ChunkId == "nist-53r5");
    }

    [Theory]
    [InlineData(200, 20, 120)]
    [InlineData(300, 8, 64)]
    [InlineData(48, 8, 48)]
    [InlineData(0, 8, 0)]
    [InlineData(80, 0, 0)]
    public void ResolveSqlHeavyRetrieverCandidateLimit_bounds_expensive_sql_windows(
        int candidates,
        int topK,
        int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveSqlHeavyRetrieverCandidateLimit(candidates, topK));
    }

    [Theory]
    [InlineData(600, 100, 600)]
    [InlineData(1200, 100, 600)]
    public void ResolveSqlHeavyRetrieverCandidateLimit_uses_topk_scaled_window_for_large_requests(
        int candidates,
        int topK,
        int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveSqlHeavyRetrieverCandidateLimit(candidates, topK));
    }

    [Fact]
    public void ResolveDefaultCandidateCount_overfetches_balanced_comparisons()
    {
        Assert.Equal(210, RetrievalQueryPolicy.ResolveDefaultCandidateCount(
            mode: "balanced",
            preferComparativeDiversity: true,
            topK: 14));
    }

    [Theory]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", 7, 14, true)]
    [InlineData("Quel dessert francais choisir pour un repas chic ?", 7, 14, true)]
    [InlineData("Fais un diner international avec Scandinavie, Espagne, Italie et Autriche.", 7, 14, false)]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", 14, 14, false)]
    public void ShouldRunBroadDiversityCandidateBackfill_detects_underfilled_selection_queries(
        string query,
        int selectedCount,
        int topK,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldRunBroadDiversityCandidateBackfill(query, selectedCount, topK));
    }

    [Fact]
    public void ShouldRunBroadDiversityCandidateCoverage_keeps_comparative_subject_queries_diverse()
    {
        Assert.True(RetrievalQueryPolicy.ShouldRunBroadDiversityCandidateCoverage(
            "Compare les plats epices ou inspires du monde dans le corpus.",
            topK: 8));
    }

    [Fact]
    public void ShouldRunBroadDiversityCandidateCoverage_handles_bare_vs_operand_queries()
    {
        Assert.True(RetrievalQueryPolicy.ShouldRunBroadDiversityCandidateCoverage(
            "Si une valeur est illisible dans hazardous locations vs machines electriques, peux-tu la deduire depuis l'autre document ?",
            topK: 8));
    }

    [Fact]
    public void PrioritizeResponseDiversityCoverage_moves_tail_document_into_response_window()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("A content", docPath: "Docs/A.pdf", chunkId: "a1"),
            TestMatch("A content again", docPath: "Docs/A.pdf", chunkId: "a2", page: 2),
            TestMatch("B content", docPath: "Docs/B.pdf", chunkId: "b1"),
            TestMatch("B content again", docPath: "Docs/B.pdf", chunkId: "b2", page: 2),
            TestMatch("C content", docPath: "Docs/C.pdf", chunkId: "c1"),
            TestMatch("D content", docPath: "Docs/D.pdf", chunkId: "d1"),
            TestMatch("E content", docPath: "Docs/E.pdf", chunkId: "e1"),
            TestMatch("F content", docPath: "Docs/F.pdf", chunkId: "f1"),
            TestMatch("G content", docPath: "Docs/G.pdf", chunkId: "g1"),
        };

        RetrievalSelection.PrioritizeResponseDiversityCoverage(
            "Compare les plats epices ou inspires du monde dans le corpus.",
            selected,
            responseTopK: 8);

        Assert.Contains(selected.Take(8), match => match.DocPath == "Docs/G.pdf");
    }

    [Fact]
    public void PrioritizeResponseDiversityCoverage_replaces_weak_head_match_when_head_is_distinct()
    {
        var selected = new List<RagMatch>
        {
            TestMatch("A profile", docPath: "Docs/A.pdf", chunkId: "a1", embeddingBasis: "document_profile_v1", score: 0.47),
            TestMatch("Weak dense", docPath: "Docs/B.pdf", chunkId: "b1", embeddingBasis: "dense_qdrant_v1", score: 0.01),
            TestMatch("C content", docPath: "Docs/C.pdf", chunkId: "c1", score: 0.78),
            TestMatch("D content", docPath: "Docs/D.pdf", chunkId: "d1", score: 0.17),
            TestMatch("E content", docPath: "Docs/E.pdf", chunkId: "e1", score: 0.06),
            TestMatch("F content", docPath: "Docs/F.pdf", chunkId: "f1", score: 0.02),
            TestMatch("G content", docPath: "Docs/G.pdf", chunkId: "g1", score: 0.0),
            TestMatch("H content", docPath: "Docs/H.pdf", chunkId: "h1", score: 0.44),
            TestMatch("Strong tail", docPath: "Docs/I.pdf", chunkId: "i1", score: 0.78),
        };

        RetrievalSelection.PrioritizeResponseDiversityCoverage(
            "Compare les styles de recettes scandinaves, francaises et espagnoles presentes.",
            selected,
            responseTopK: 8);

        Assert.Contains(selected.Take(8), match => match.DocPath == "Docs/I.pdf");
    }

    [Theory]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", true)]
    [InlineData("Quel dessert chocolate ou cremeux est le plus simple ?", true)]
    [InlineData("Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?", true)]
    [InlineData("Compare les styles de trois procedures presentes.", false)]
    [InlineData("Compare les options.", false)]
    public void ShouldAllowSparseAssistForBroadDiversity_detects_concrete_thematic_constraints(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldAllowSparseAssistForBroadDiversity(query));
    }

    [Theory]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", true)]
    [InlineData("Compare les styles de trois procedures presentes.", false)]
    [InlineData("Compare les options.", false)]
    public void ShouldAllowMultipleChunksForComparativeSubject_keeps_evidence_from_same_document_when_subject_is_specific(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldAllowMultipleChunksForComparativeSubject(query));
    }

    [Theory]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", true)]
    [InlineData("Quel dessert francais choisir pour un repas chic ?", false)]
    [InlineData("Fais un diner international avec Scandinavie, Espagne, Italie et Autriche, et explique l'enchainement.", false)]
    public void ShouldRunFinalComparativeSparseDiversityBackfill_detects_sparse_assist_comparisons(
        string query,
        bool expected)
    {
        var selected = new[]
        {
            BuildRuntimeSelectionMatch(0.80, "doc-a", "Cuisine/A.pdf", 1, "General selected evidence.")
        };

        Assert.Equal(expected, RetrievalQueryPolicy.ShouldRunFinalComparativeSparseDiversityBackfill(query, selected));
    }

    [Fact]
    public void BuildComparativeSubjectBackfillQueries_prefers_strong_reordered_subject_phrases()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeSubjectBackfillQueries(
            "Compare trois plats mijot\u00e9s fran\u00e7ais et dis lequel choisir pour un repas de famille.");

        Assert.Equal("mijoter plat", queries[0]);
        Assert.Contains("mijoter plat", queries);
        Assert.Contains("mijoter", queries);
        Assert.DoesNotContain("repas famille", queries);
    }

    [Fact]
    public void EnsureBroadDiversityCandidateCoverage_replaces_duplicate_weak_choice_with_missing_concrete_doc()
    {
        const string query = "Si je veux un dessert chocolaté ou crémeux, lequel est le plus simple ?";
        var selected = new List<RagMatch>
        {
            TestMatch(
                "Chocolate brownies. Ingredients, preparation and serving notes.",
                docPath: "Docs/A.pdf",
                page: 1,
                chunkId: "a-strong",
                score: 0.92) with
            {
                ContentRole = RetrievalContentClassifier.ContentRole,
                ContentDensityScore = 1.0,
                MatchedContentCards =
                [
                    new RagMatchedContentCard(
                        "Chocolate brownies",
                        Signals: ["quantity_list", "structured_facts"])
                ]
            },
            TestMatch(
                "Cream dessert option. Ingredients, preparation and serving notes.",
                docPath: "Docs/B.pdf",
                page: 2,
                chunkId: "b-strong",
                score: 0.91) with
            {
                ContentRole = RetrievalContentClassifier.ContentRole,
                ContentDensityScore = 1.0,
                MatchedContentCards =
                [
                    new RagMatchedContentCard(
                        "Cream dessert option",
                        Signals: ["quantity_list", "structured_facts"])
                ]
            },
            TestMatch(
                "Let cool before unmolding. General serving note.",
                docPath: "Docs/A.pdf",
                page: 3,
                chunkId: "a-weak-duplicate",
                score: 0.90) with
            {
                ContentRole = RetrievalContentClassifier.ContentRole,
                ContentDensityScore = 0.3,
                MatchedContentCards =
                [
                    new RagMatchedContentCard(
                        "Let cool before unmolding",
                        Signals: ["cool", "serving"])
                ]
            },
            TestMatch(
                "Generic dessert selection. Ingredients, preparation and serving notes.",
                docPath: "Docs/C.pdf",
                page: 4,
                chunkId: "c-strong",
                score: 0.89) with
            {
                ContentRole = RetrievalContentClassifier.ContentRole,
                ContentDensityScore = 1.0,
                MatchedContentCards =
                [
                    new RagMatchedContentCard(
                        "Generic dessert selection",
                        Signals: ["quantity_list", "structured_facts"])
                ]
            }
        };
        var target = TestMatch(
            "Crème brûlée. Creamy dessert with ingredients, preparation and serving notes.",
            docPath: "Docs/D.pdf",
            page: 5,
            chunkId: "d-concrete",
            score: 0.99) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Crème brûlée",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePool = selected.Concat([target]).ToList();

        RetrievalSelection.EnsureBroadDiversityCandidateCoverage(
            selected,
            selectedKeys,
            candidatePool,
            topK: 4,
            minScore: 0.0,
            maxPerPage: 2,
            query);

        Assert.Equal(4, selected.Count);
        Assert.Contains(selected, match => match.ChunkId == "d-concrete");
        Assert.DoesNotContain(selected, match => match.ChunkId == "a-weak-duplicate");
        Assert.Equal(1, selected.Count(match => string.Equals(match.DocPath, "Docs/A.pdf", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_accented_query_terms()
    {
        var repaired = RetrievalAnswerGuidance.RepairLikelyMojibakeText(
            "Si je veux un dessert chocolat\u00c3\u0192\u00c2\u00a9 ou cr\u00c3\u0192\u00c2\u00a9meux, lequel est le plus simple ?");

        Assert.Equal("Si je veux un dessert chocolat\u00e9 ou cr\u00e9meux, lequel est le plus simple ?", repaired);
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_single_pass_french_punctuation()
    {
        var repaired = RetrievalAnswerGuidance.RepairLikelyMojibakeText(
            "On doit choisir entre deux fournisseurs : un tr\u00c3\u00a8s bon march\u00c3\u00a9 mais peu transparent. Comment les textes m\u00e2\u20ac\u2122aident ?");

        Assert.Equal("On doit choisir entre deux fournisseurs : un tr\u00e8s bon march\u00e9 mais peu transparent. Comment les textes m'aident ?", repaired);
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_common_french_comparison_terms()
    {
        var repaired = RetrievalAnswerGuidance.RepairLikelyMojibakeText(
            "Compare les styles de recettes scandinaves, franÃ§aises et espagnoles prÃ©sentes.");

        Assert.Equal("Compare les styles de recettes scandinaves, fran\u00e7aises et espagnoles pr\u00e9sentes.", repaired);
    }

    [Fact]
    public void MergeComparativeSubjectBackfillMatches_reselects_when_initial_selection_is_full()
    {
        var selected = new List<RagMatch>
        {
            BuildRuntimeSelectionMatch(0.61, "doc-a", "Cuisine/A.pdf", 1, "General vegetable notes and meal planning."),
            BuildRuntimeSelectionMatch(0.60, "doc-b", "Cuisine/B.pdf", 2, "Generic preparation guidance for weekly menus."),
            BuildRuntimeSelectionMatch(0.59, "doc-c", "Cuisine/C.pdf", 3, "Broad family serving ideas without the requested subject.")
        };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backfill = BuildRuntimeSelectionMatch(
            0.95,
            "doc-target",
            "Cuisine/Target.pdf",
            4,
            "Coq au vin. Portez le tout a ebullition, laissez mijoter, puis servez ce plat familial.");

        RetrievalSelection.MergeComparativeSubjectBackfillMatches(
            selected,
            selectedKeys,
            [backfill],
            topK: 3,
            minScore: 0.0,
            maxPerDoc: 1,
            maxPerPage: 1,
            prioritizeDocumentProfiles: false,
            query: "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.");

        Assert.Contains(selected, match => string.Equals(match.DocId, "doc-target", StringComparison.Ordinal));
        Assert.Equal(3, selected.Count);
        Assert.Equal(selected.Count, selectedKeys.Count);
    }

    [Fact]
    public void MergeComparativeSubjectBackfillMatches_keeps_missing_document_when_selection_has_room()
    {
        var selected = new List<RagMatch>
        {
            BuildRuntimeSelectionMatch(0.80, "doc-a", "Cuisine/A.pdf", 1, "General vegetable notes.")
        };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicate = BuildRuntimeSelectionMatch(
            0.99,
            "doc-a",
            "Cuisine/A.pdf",
            4,
            "Higher scored duplicate vegetable notes.");
        var missing = BuildRuntimeSelectionMatch(
            0.70,
            "doc-target",
            "Cuisine/Target.pdf",
            2,
            "Coq au vin. Laissez mijoter puis servez ce plat familial.");

        RetrievalSelection.MergeComparativeSubjectBackfillMatches(
            selected,
            selectedKeys,
            [duplicate, missing],
            topK: 2,
            minScore: 0.0,
            maxPerDoc: 1,
            maxPerPage: 1,
            prioritizeDocumentProfiles: false,
            query: "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.");

        Assert.Contains(selected, match => string.Equals(match.DocId, "doc-target", StringComparison.Ordinal));
        Assert.Equal(2, selected.Count);
        Assert.Equal(selected.Count, selectedKeys.Count);
    }

    [Fact]
    public void MergeComparativeSubjectBackfillMatches_reserves_lower_scored_missing_document_for_broad_comparison()
    {
        var selected = new List<RagMatch>
        {
            BuildRuntimeSelectionMatch(0.95, "doc-a", "Cuisine/A.pdf", 1, "General family dish notes."),
            BuildRuntimeSelectionMatch(0.94, "doc-b", "Cuisine/B.pdf", 2, "Generic dinner comparison notes."),
            BuildRuntimeSelectionMatch(0.93, "doc-c", "Cuisine/C.pdf", 3, "Broad menu guidance without the requested cooking subject.")
        };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateA = BuildRuntimeSelectionMatch(
            0.99,
            "doc-a",
            "Cuisine/A.pdf",
            4,
            "A higher scored backfill from an already selected document.");
        var duplicateB = BuildRuntimeSelectionMatch(
            0.98,
            "doc-b",
            "Cuisine/B.pdf",
            5,
            "Another higher scored backfill from an already selected document.");
        var missing = BuildRuntimeSelectionMatch(
            0.50,
            "doc-target",
            "Cuisine/Target.pdf",
            6,
            "Cassoulet toulousain. Laissez mijoter longtemps puis servez ce plat familial.");

        RetrievalSelection.MergeComparativeSubjectBackfillMatches(
            selected,
            selectedKeys,
            [duplicateA, duplicateB, missing],
            topK: 3,
            minScore: 0.0,
            maxPerDoc: 1,
            maxPerPage: 1,
            prioritizeDocumentProfiles: false,
            query: "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.");

        Assert.Contains(selected, match => string.Equals(match.DocId, "doc-target", StringComparison.Ordinal));
        Assert.Equal(3, selected.Count);
        Assert.Equal(selected.Count, selectedKeys.Count);
    }

    [Fact]
    public void MergeComparativeSubjectBackfillMatches_preserves_supported_profiles_when_prioritized()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var supportedProfile = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            chunkId: "supported-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.70);
        var selected = new List<RagMatch>
        {
            supportedProfile,
            BuildRuntimeSelectionMatch(0.92, "doc-a", "Docs/A.pdf", 1, "Generic setup notes with no audience coverage.")
        };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backfill = BuildRuntimeSelectionMatch(
            0.96,
            "doc-b",
            "Docs/B.pdf",
            2,
            "Procedure notes with setup details.");

        RetrievalSelection.MergeComparativeSubjectBackfillMatches(
            selected,
            selectedKeys,
            [backfill],
            topK: 2,
            minScore: 0.0,
            maxPerDoc: 1,
            maxPerPage: 1,
            prioritizeDocumentProfiles: true,
            query: query);

        Assert.Contains(selected, match => string.Equals(match.ChunkId, "supported-profile", StringComparison.Ordinal));
        Assert.Equal(2, selected.Count);
        Assert.Equal(selected.Count, selectedKeys.Count);
    }

    [Fact]
    public void MergeComparativeSubjectBackfillMatches_allows_profile_plus_actionable_chunk_from_same_doc()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var profile = new RagMatch(
            0.72,
            "doc-profile",
            "Docs/Profile.pdf",
            "Profile.pdf",
            null,
            null,
            "profile",
            null,
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            1,
            "hash-profile",
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            "document_profile_v1",
            null,
            null,
            null,
            null,
            "document_profile",
            null,
            null,
            null);
        var selected = new List<RagMatch> { profile };
        var selectedKeys = selected
            .Select(RetrievalSelection.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actionable = BuildRuntimeSelectionMatch(
            0.95,
            "doc-profile",
            "Docs/Profile.pdf",
            2,
            "Procedure for apprentices. Material checklist, failure risk notes and setup controls.");

        RetrievalSelection.MergeComparativeSubjectBackfillMatches(
            selected,
            selectedKeys,
            [actionable],
            topK: 2,
            minScore: 0.0,
            maxPerDoc: 1,
            maxPerPage: 1,
            prioritizeDocumentProfiles: true,
            query: query);

        Assert.Contains(selected, match => string.Equals(match.ChunkId, "profile", StringComparison.Ordinal));
        Assert.Contains(selected, match => string.Equals(match.ChunkId, "chunk-doc-profile", StringComparison.Ordinal));
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SafeRegexMatches_returns_empty_when_runtime_heuristic_times_out()
    {
        var input = new string('a', 50_000) + "!";

        var matches = RetrievalText.SafeRegexMatches(
            input,
            "^(a+)+$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(1));

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("Quels documents sont disponibles dans cette categorie ?", true)]
    [InlineData("Quels documents contient cette categorie et a quoi servent-ils chacun ?", true)]
    [InlineData("Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?", true)]
    [InlineData("Which documents are available in this category?", true)]
    [InlineData("Which documents mention nitrogen blanketing?", false)]
    [InlineData("Que documentos hablan de nitrogen blanketing?", false)]
    [InlineData("Quais documentos falam de nitrogen blanketing?", false)]
    [InlineData("Welche Dokumente sprechen ueber nitrogen blanketing?", false)]
    [InlineData("Ich suche Hinweise zu nitrogen blanketing. Welche Dokumente sprechen darueber?", false)]
    [InlineData("Quali documenti parlano di nitrogen blanketing?", false)]
    public void ShouldSkipChunkRetrieversForDocumentOverview_keeps_chunks_for_topical_source_lookup(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipChunkRetrieversForDocumentOverview(
            query,
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void DocumentOverviewIntent_detects_role_classification_inventory()
    {
        const string query = "Quels documents semblent être des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?";
        const string unaccentedQuery = "Quels documents semblent etre des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?";

        Assert.True(RetrievalQueryPolicy.ContainsDocumentOverviewIntent(query));
        Assert.True(RetrievalQueryPolicy.ContainsDocumentOverviewIntent(unaccentedQuery));
        Assert.True(RetrievalQueryPolicy.ContainsDocumentRoleClassificationSurface(unaccentedQuery));
        Assert.True(RetrievalQueryPolicy.ShouldSkipChunkRetrieversForDocumentOverview(
            query,
            hasDocScope: false,
            mode: "balanced"));
        Assert.True(RetrievalQueryPolicy.ShouldUseScopedProfileFallback(
            query,
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForLowCostBroadQuery(query, "balanced"));
    }

    [Fact]
    public void ShouldUseCatalogDocumentOverviewFallback_distinguishes_inventory_from_clarification()
    {
        Assert.True(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Quels documents contient cette categorie et a quoi servent-ils chacun ?"));
        Assert.True(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?"));
        Assert.True(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Kannst du die wichtigsten Dokumente dieser Kategorie nach Thema gruppieren und kurz erklaren, wofur sie nutzlich sind?"));
        Assert.False(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Je ne sais pas quel document utiliser pour ma question. Pose-moi une clarification courte ou propose un cadrage prudent."));
        Assert.False(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Which documents mention nitrogen blanketing?"));
        Assert.False(RetrievalQueryPolicy.ShouldRequireDocumentOverviewProfileMatch(
            "Quels documents contient cette categorie et a quoi servent-ils chacun ?"));
    }

    [Fact]
    public void ShouldSkipChunkRetrieversForDocumentOverview_keeps_chunks_for_quoted_title_source_request()
    {
        Assert.False(RetrievalQueryPolicy.ShouldSkipChunkRetrieversForDocumentOverview(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void BuildShortTechnicalDirectPhraseTerms_targets_property_table_phrases()
    {
        var electrical = RetrievalCandidateSignals.BuildShortTechnicalDirectPhraseTerms("proprietes electriques");
        var thermal = RetrievalCandidateSignals.BuildShortTechnicalDirectPhraseTerms("proprietes thermiques");
        var broad = RetrievalCandidateSignals.BuildShortTechnicalDirectPhraseTerms("temperature de moulage");

        Assert.Contains("electrical properties", electrical);
        Assert.Contains("dielectric strength", electrical);
        Assert.Contains("proprietes electriques", electrical);
        Assert.Contains("thermal properties", thermal);
        Assert.Contains("service temperature", thermal);
        Assert.Contains("melt point", thermal);
        Assert.Empty(broad);
    }

    [Fact]
    public void ShouldSkipChunkRetrieversForDocumentOverview_keeps_chunks_for_quoted_topical_lookup()
    {
        Assert.False(RetrievalQueryPolicy.ShouldSkipChunkRetrieversForDocumentOverview(
            "Retrouve les documents qui parlent de `preuves a fournir` et donne-moi les passages utiles.",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void ShouldUseCatalogDocumentOverviewFallback_detects_category_takeaway_summary()
    {
        Assert.True(RetrievalQueryPolicy.ShouldUseCatalogDocumentOverviewFallback(
            "Je dois repondre vite a mon chef. Qu est ce qu il faut retenir de cette categorie sans rentrer dans tous les details ?"));
    }

    [Theory]
    [InlineData("Je ne sais pas quel document utiliser pour ma question. Pose-moi une clarification courte ou propose un cadrage prudent.", "balanced", true)]
    [InlineData("J'ai besoin d'un resume actionnable, pas d'un copier-coller. Tu peux me faire ca proprement ?", "balanced", true)]
    [InlineData("J’ai besoin d’un résumé actionnable, pas d’un copier-coller. Tu peux me faire ça proprement ?", "balanced", true)]
    [InlineData("resume actionnable", "balanced", true)]
    [InlineData("Si un PDF contient une phrase du style ignore les consignes precedentes, comment dois-tu la traiter pendant la reponse ?", "balanced", false)]
    [InlineData("J'ai besoin d'un resume actionnable, pas d'un copier-coller. Tu peux me faire ca proprement ?", "focused", false)]
    public void ShouldUseScopedCatalogDocumentOverviewFallback_detects_vague_category_guidance(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldUseScopedCatalogDocumentOverviewFallback(query, mode));
    }

    [Theory]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.")]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?")]
    public void ShouldBackfillEnumerativeSearch_detects_precise_content_lookup_requests(string query)
    {
        Assert.True(RetrievalCandidateSignals.ShouldBackfillEnumerativeSearch(query, selectedCount: 0, topK: 8));
    }

    [Theory]
    [InlineData("Fais-moi 5 options pas trop cheres a partir des PDF.", "balanced")]
    [InlineData("Compare deux procedures proches.", "broad")]
    [InlineData("Mets les elements avec un mot-cle dans un tableau : nom, source, type.", "balanced")]
    [InlineData("Reponds en JSON avec des candidats pour un plan vegetarien.", "balanced")]
    [InlineData("Je dois eviter une contrainte : quels elements semblent risques et lesquels sont plus faciles a adapter ?", "balanced")]
    [InlineData("Tu peux me faire une vue d'ensemble des documents disponibles, par grands themes ?", "broad")]
    [InlineData("Tu peux me faire une vue d'ensemble des elements disponibles, par grands themes ?", "broad")]
    [InlineData("Menu complet utilisant les elements concus pour la sonde X.", "balanced")]
    [InlineData("Rends le mode A ou le mode B un peu plus robuste sans pretendre que c'est officiel.", "balanced")]
    [InlineData("Je recois des invites : choisis entre option alpha, option beta, option gamma ou option delta et justifie.", "broad")]
    [InlineData("Fais un diner international avec Scandinavie, Espagne, Italie et Autriche.", "balanced")]
    [InlineData("Pose-toi 5 questions de verification avant de repondre a une demande ambigue.", "balanced")]
    [InlineData("Quelles entrees utilisent le produit Alpha et comment les gerer sans le produit Beta ?", "balanced")]
    [InlineData("Peux-tu classer ces documents par difficulte probable d extraction ?", "balanced")]
    [InlineData("Quels documents contiennent des tableaux ou symboles difficiles a lire ?", "broad")]
    [InlineData("Quels documents doivent produire une reponse prudente si le texte extrait est incomplet ?", "broad")]
    [InlineData("Quels documents doivent produire une reponse \u201cje ne peux pas confirmer\u201d si le texte n\u2019est pas extrait ?", "broad")]
    [InlineData("Je dois verifier une couleur ou un pictogramme de securite dans un PDF scanne : comment repondre sans me tromper ?", "balanced")]
    [InlineData("Si le RAG n a que des images et pas de texte, quelle reponse honnete dois tu produire ?", "balanced")]
    [InlineData("Je suis perdu dans ce dossier, tu peux me guider comme si je decouvrais le sujet ?", "balanced")]
    [InlineData("Wenn zwei Dokumente unterschiedliche Aussagen liefern, wie soll ich die Antwort priorisieren?", "balanced")]
    [InlineData("Auf Deutsch: Wenn zwei Dokumente unterschiedliche Aussagen liefern, wie soll ich die Antwort priorisieren?", "balanced")]
    [InlineData("Compare les options.", "focused")]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "focused")]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", "focused")]
    [InlineData("Donne-moi le mode acces A.", "focused")]
    public void ResolveEffectiveSearchMode_promotes_focused_only_for_broad_intents(string query, string expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveEffectiveSearchMode("focused", query));
    }

    [Theory]
    [InlineData("Compare les styles de trois procedures presentes.", "broad", true)]
    [InlineData("Compare les sauces robotisees : lesquelles sont adaptees a un debutant ?", "broad", false)]
    [InlineData("Compare trois recettes sal\u00e9es pour enfants : temps, mat\u00e9riel, risque de ratage.", "balanced", false)]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", "balanced", false)]
    [InlineData("Compare les options.", "broad", false)]
    [InlineData("Quelle procedure choisir pour un deploiement pilote ?", "balanced", false)]
    [InlineData("Quelle procedure choisir pour l'erreur E42 ?", "balanced", false)]
    [InlineData("Which valve should I choose for pressure class PN16?", "balanced", false)]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "broad", false)]
    [InlineData("Donne-moi le mode acces A.", "focused", false)]
    public void ShouldSkipSparseRetrieverForBroadDiversity_only_skips_unquoted_diversity_queries(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipSparseRetrieverForBroadDiversity(query, mode));
    }

    [Fact]
    public void RebuildSelectedKeys_drops_pruned_matches_from_dedup_state()
    {
        var kept = TestMatch(
            text: "Primary retained content about Alpha.",
            embedText: "Primary retained content about Alpha.",
            page: 1) with
        {
            ChunkId = "kept"
        };
        var pruned = TestMatch(
            text: "Pruned weak navigation about Alpha.",
            embedText: "Pruned weak navigation about Alpha.",
            page: 2) with
        {
            ChunkId = "pruned"
        };
        var selected = new List<RagMatch> { kept };
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RetrievalSelection.BuildMatchDedupKey(kept),
            RetrievalSelection.BuildMatchDedupKey(pruned)
        };

        RetrievalSelection.RebuildSelectedKeys(selected, selectedKeys);

        Assert.Contains(RetrievalSelection.BuildMatchDedupKey(kept), selectedKeys);
        Assert.DoesNotContain(RetrievalSelection.BuildMatchDedupKey(pruned), selectedKeys);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_unquoted_short_title_words_in_focused_targets()
    {
        var terms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms(
            "Donne-moi la recette du coq au vin dans le livre international.");

        Assert.Contains("coq au vin", terms);
    }

    [Theory]
    [InlineData("Quels documents parlent d'inertage ?", true)]
    [InlineData("Which documents mention inerting?", true)]
    [InlineData("Explique l'inertage dans ce passage.", false)]
    public void ShouldBackfillEnumerativeSearch_detects_list_or_find_requests(string query, bool expected)
    {
        Assert.Equal(expected, RetrievalCandidateSignals.ShouldBackfillEnumerativeSearch(query, selectedCount: 1, topK: 8));
    }

    [Fact]
    public void BuildQdrantChunkPayload_keeps_chunk_text_and_tracks_contextual_embedding_basis()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var projectedChunk = new ProjectedRetrievalChunk(
            ChunkIndex: 4,
            SectionOrdinal: 2,
            UnitOrdinal: 9,
            PageStart: 3,
            PageEnd: 4,
            Text: "Chunk snippet",
            TokenCount: 2,
            Checksum: [5],
            ChunkType: "section_window_v1",
            OffsetStart: 120,
            OffsetEnd: 133,
            SourceUnitOrdinals: [9, 10],
            SourceUnitStartOrdinal: 9,
            SourceUnitEndOrdinal: 10,
            SourceUnitCount: 2,
            ChunkComposition: "multi_unit_window");

        var payload = IngestionWorker.BuildQdrantChunkPayload(
            tenantId,
            docId,
            "ATEX/CEN.pdf",
            "atex",
            "abc123",
            "2026-04-13T10:15:00.0000000Z",
            6,
            projectedChunk,
            "Document: CEN.pdf\nExcerpt:\nChunk snippet",
            "Introduction",
            "Chapter 1 > Introduction",
            new IngestionWorker.ChunkLinkInfo(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            "intfloat/multilingual-e5-base",
            "e5_passage_v1");

        Assert.Equal("Chunk snippet", payload["text"]);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", payload["embed_text"]);
        Assert.Equal("contextual_text_v3", payload["embedding_basis"]);
        Assert.Equal("contextual_text_v3", payload["context_schema_version"]);
        Assert.Equal("intfloat/multilingual-e5-base", payload["embedding_model"]);
        Assert.Equal("e5_passage_v1", payload["embedding_input_format"]);
        Assert.Equal(2, payload["section_ordinal"]);
        Assert.Equal(9, payload["unit_ordinal"]);
        Assert.Equal(new[] { 9, 10 }, Assert.IsAssignableFrom<IReadOnlyList<int>>(payload["source_unit_ordinals"]));
        Assert.Equal(9, payload["source_unit_start_ordinal"]);
        Assert.Equal(10, payload["source_unit_end_ordinal"]);
        Assert.Equal("9-10", payload["source_unit_span"]);
        Assert.Equal(2, payload["source_unit_count"]);
        Assert.Equal("multi_unit_window", payload["chunk_composition"]);
        Assert.Equal("3-4", payload["page_span"]);
        Assert.Equal(120, payload["offset_start"]);
        Assert.Equal(133, payload["offset_end"]);
        Assert.Equal("Introduction", payload["section_title"]);
        Assert.Equal("Chapter 1 > Introduction", payload["heading_path"]);
        Assert.Equal("section_window_v1", payload["chunk_type"]);
        Assert.Equal("content", payload["content_role"]);
        Assert.Null(payload["navigation_reason"]);
        Assert.Null(payload["original_chunk_type"]);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", payload["prev_chunk_id"]);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", payload["next_chunk_id"]);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", payload["same_section_chunk_id"]);
        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 4).ToString(),
            payload["chunk_id"]);
    }

    [Fact]
    public void ParseSearchResults_reads_enriched_payload_without_breaking_text_snippet()
    {
        using var doc = JsonDocument.Parse("""
        {
          "result": [
            {
              "score": 0.91,
              "payload": {
                "doc_id": "doc-1",
                "doc_path": "ATEX/CEN.pdf",
                "doc_name": "CEN.pdf",
                "page_start": 2,
                "page_end": 3,
                "chunk_id": "chunk-1",
                "chunk_index": 8,
                "text": "Chunk snippet",
                "embed_text": "Document: CEN.pdf\nExcerpt:\nChunk snippet",
                "embedding_basis": "contextual_text_v1",
                "section_ordinal": 1,
                "unit_ordinal": 5,
                "chunk_type": "section_window_v1",
                "section_title": "Introduction",
                "heading_path": "Chapter 1 > Introduction",
                "prev_chunk_id": "prev-1",
                "next_chunk_id": "next-1",
                "same_section_chunk_id": "same-1",
                "embedding_model": "intfloat/multilingual-e5-base",
                "embedding_input_format": "e5_passage_v1",
                "category": "canonical-safety",
                "ingestion_version": 4,
                "hash_doc": "deadbeef"
              }
            }
          ]
        }
        """);

        var match = Assert.Single(QdrantClient.ParseSearchResults(doc));

        Assert.Equal("Chunk snippet", match.Text);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", match.EmbedText);
        Assert.Equal("contextual_text_v1", match.EmbeddingBasis);
        Assert.Equal(1, match.SectionOrdinal);
        Assert.Equal(5, match.UnitOrdinal);
        Assert.Equal("Introduction", match.SectionTitle);
        Assert.Equal("Chapter 1 > Introduction", match.HeadingPath);
        Assert.Equal("section_window_v1", match.ChunkType);
        Assert.Equal("prev-1", match.PrevChunkId);
        Assert.Equal("next-1", match.NextChunkId);
        Assert.Equal("same-1", match.SameSectionChunkId);
        Assert.Equal("intfloat/multilingual-e5-base", match.EmbeddingModel);
        Assert.Equal("e5_passage_v1", match.EmbeddingInputFormat);
        Assert.Equal("canonical-safety", match.Category);
    }

    [Fact]
    public void ResolveMatchCategory_prefers_ingested_category_over_doc_path()
    {
        var match = new RagMatch(
            0.91,
            "doc-1",
            "LegacyPath/Guide.pdf",
            "Guide.pdf",
            1,
            1,
            "chunk-1",
            0,
            "Chunk snippet",
            IngestionVersion: 4,
            HashDoc: "deadbeef",
            EmbedText: "Chunk snippet",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Section",
            HeadingPath: "Section",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            Category: "Canonical-Safety");

        Assert.Equal("canonical-safety", RetrievalAnswerGuidance.ResolveMatchCategory(match, "fallback"));
    }

    [Fact]
    public void IsActiveDenseMatchForRevision_uses_revision_ingestion_version_not_indexed_version()
    {
        var match = new RagMatch(
            0.91,
            "doc-1",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            1,
            1,
            "chunk-1",
            0,
            "Chunk snippet",
            IngestionVersion: 9,
            HashDoc: "DEADBEEF",
            EmbedText: "Chunk snippet",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Section",
            HeadingPath: "Section",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        Assert.True(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 9, currentContentHashHex: "deadbeef"));
        Assert.False(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 7, currentContentHashHex: "deadbeef"));
        Assert.False(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 9, currentContentHashHex: "feedface"));
    }

    [Fact]
    public void IsActiveDenseMatchForRevision_keeps_legacy_hash_only_payloads()
    {
        var legacy = new RagMatch(
            0.91,
            "doc-1",
            "Docs/Legacy.pdf",
            "Legacy.pdf",
            1,
            1,
            "chunk-1",
            0,
            "Chunk snippet",
            IngestionVersion: null,
            HashDoc: "deadbeef",
            EmbedText: "Chunk snippet",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Section",
            HeadingPath: "Section",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        Assert.True(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(legacy, currentRevisionIngestionVersion: 9, currentContentHashHex: "DEADBEEF"));
        Assert.False(RetrievalCandidateSignals.IsActiveDenseMatchForRevision(legacy, currentRevisionIngestionVersion: 9, currentContentHashHex: "feedface"));
    }

    [Fact]
    public void BuildMatchDedupKey_deduplicates_exact_and_dense_results_for_same_excerpt()
    {
        var exact = new RagMatch(
            Score: 1.0,
            DocId: "doc-1",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "exact-1",
            ChunkIndex: 0,
            Text: "EN 15281",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "EN 15281",
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var dense = exact with
        {
            ChunkId = "dense-1",
            EmbeddingBasis = "contextual_text_v1"
        };

        Assert.Equal(
            RetrievalSelection.BuildMatchDedupKey(exact),
            RetrievalSelection.BuildMatchDedupKey(dense));
    }

    [Fact]
    public void BuildMatchDedupKey_deduplicates_same_content_hash_across_different_paths()
    {
        var stableHash = new string('a', 64);
        var first = new RagMatch(
            Score: 1.0,
            DocId: "doc-1",
            DocPath: "Knowledge/Guide.pdf",
            DocName: "Guide.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "chunk-1",
            ChunkIndex: 0,
            Text: "Shared excerpt",
            IngestionVersion: 1,
            HashDoc: stableHash,
            EmbedText: "Shared excerpt",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var duplicatePath = first with
        {
            DocId = "doc-2",
            DocPath = "Knowledge/Imported/Guide.pdf",
            ChunkId = "chunk-2",
            EmbeddingBasis = "sparse_bm25_v1"
        };

        Assert.Equal(
            RetrievalSelection.BuildMatchDedupKey(first),
            RetrievalSelection.BuildMatchDedupKey(duplicatePath));
    }

    [Fact]
    public void BuildMatchDedupKey_deduplicates_dense_and_linked_results_for_same_excerpt()
    {
        var dense = new RagMatch(
            Score: 0.82,
            DocId: "doc-1",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 4,
            PageEnd: 4,
            ChunkId: "dense-1",
            ChunkIndex: 3,
            Text: "Safety instructions",
            IngestionVersion: 2,
            HashDoc: "hash",
            EmbedText: "Safety instructions",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 5,
            SectionTitle: "Safety",
            HeadingPath: "Chapter 1 > Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var linked = dense with
        {
            ChunkId = "linked-1",
            EmbeddingBasis = "linked_context_v1"
        };

        Assert.Equal(
            RetrievalSelection.BuildMatchDedupKey(dense),
            RetrievalSelection.BuildMatchDedupKey(linked));
    }

    [Fact]
    public void BuildMatchDedupKey_keeps_long_text_keys_compact_and_stable()
    {
        var text = string.Join(" ", Enumerable.Range(0, 800).Select(i => $"section-{i}"));
        var first = new RagMatch(
            Score: 0.82,
            DocId: "doc-1",
            DocPath: "Docs/Long.pdf",
            DocName: "Long.pdf",
            PageStart: 4,
            PageEnd: 4,
            ChunkId: "dense-1",
            ChunkIndex: 3,
            Text: text,
            IngestionVersion: 2,
            HashDoc: "hash",
            EmbedText: text,
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 5,
            SectionTitle: "Long",
            HeadingPath: "Chapter 1 > Long",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var second = first with { ChunkId = "linked-1", EmbeddingBasis = "linked_context_v1" };

        var firstKey = RetrievalSelection.BuildMatchDedupKey(first);
        Assert.Equal(firstKey, RetrievalSelection.BuildMatchDedupKey(second));
        Assert.True(firstKey.Length < 180);
    }


    [Fact]
    public void BuildChunkLinkMap_returns_prev_next_and_same_section_links()
    {
        var docId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(0, 1, 0, 1, 1, "a", 1, [1], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 1, 1, 1, 1, "b", 1, [2], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 2, 2, 2, 2, "c", 1, [3], "unit_exact_v1")
        };

        var map = IngestionWorker.BuildChunkLinkMap(docId, 3, chunks);

        Assert.Null(map[0].PreviousChunkId);
        Assert.NotNull(map[0].NextChunkId);
        Assert.NotNull(map[0].SameSectionChunkId);
        Assert.NotNull(map[1].PreviousChunkId);
        Assert.NotNull(map[1].NextChunkId);
        Assert.Null(map[1].SameSectionChunkId);
    }

    [Fact]
    public void FuseWithRrf_prioritizes_matches_supported_by_multiple_retrievers()
    {
        var exact = new RagMatch(1.0, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "exact-a", 0, "EN 15281", 1, "hash-a", "EN 15281", "exact_match_v1", null, null, "Safety", "Safety", "exact_match_entry", null, null, null);
        var sparse = new RagMatch(0.70, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-a", 0, "EN 15281", 1, "hash-a", "Document: CEN.pdf\nExcerpt:\nEN 15281 guidance", "sparse_bm25_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var denseForA = new RagMatch(0.64, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "dense-a", 0, "EN 15281", 1, "hash-a", "Document: CEN.pdf\nExcerpt:\nEN 15281 guidance", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var denseForB = new RagMatch(0.80, "doc-b", "General/Other.pdf", "Other.pdf", 1, 1, "dense-b", 0, "General guidance", 1, "hash-b", "Document: Other.pdf\nExcerpt:\nGeneral guidance", "contextual_text_v1", 1, 1, "General", "General", "unit_exact_v1", null, null, null);

        var fused = RetrievalSelection.FuseWithRrf([exact], [sparse], [denseForB, denseForA]);

        Assert.Equal("doc-a", fused[0].DocId);
        Assert.Equal("exact_match_v1", fused[0].EmbeddingBasis);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void FuseWithRrf_prefers_dense_representative_over_weak_instruction_title_route_for_same_chunk()
    {
        const string text = "VERIFIEZ LES RACCORDS. Materials: gasket, wrench and pressure gauge. Procedure: isolate the line, inspect every fitting, replace damaged seals, pressure test the assembly and record the result.";
        var titleRoute = TestMatch(
            text: text,
            embedText: "Matched title_anchor_route: Verifiez les raccords\n" + text,
            docPath: "Technique/Maintenance.pdf",
            page: 4,
            chunkId: "same-chunk",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.92,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.95);
        var dense = titleRoute with
        {
            EmbedText = "excerpt:\n" + text,
            EmbeddingBasis = "contextual_text_v3",
            Score = 0.80
        };

        var fused = RetrievalSelection.FuseWithRrf([titleRoute], [], [dense]);

        Assert.Single(fused);
        Assert.Equal("contextual_text_v3", fused[0].EmbeddingBasis);
    }

    [Fact]
    public void LooksLikeNavigationalChunk_detects_recipe_index_pages()
    {
        var index = new RagMatch(
            0.98,
            "doc-index",
            "Cuisine/children.pdf",
            "children.pdf",
            13,
            13,
            "index",
            12,
            "Entrées•Salade de lentilles1•Salade de haricots verts à l'avocat2•Taboulé5•Quiche lorraine16Index•Clafoutis aux pommes3•Brownies21FicheFicheFichefiche-index Page 1",
            1,
            "hash-index",
            "Entrées•Salade de lentilles1•Salade de haricots verts à l'avocat2•Taboulé5•Quiche lorraine16Index•Clafoutis aux pommes3•Brownies21FicheFicheFichefiche-index Page 1",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RetrievalCandidateSignals.LooksLikeNavigationalChunk(index));

        var inlineIndex = index with
        {
            ChunkId = "inline-index",
            Text = "Index des recettesAAsperges vertes au miel, 16BBoulettes de viande hachee a la mozzarella, 30Brochettes de poisson mediterraneennes, 34 Rumsteck aux oignons grilles, 38 Quiche lorraine, 16 Brownies, 21",
            EmbedText = "Index des recettesAAsperges vertes au miel, 16BBoulettes de viande hachee a la mozzarella, 30Brochettes de poisson mediterraneennes, 34 Rumsteck aux oignons grilles, 38 Quiche lorraine, 16 Brownies, 21"
        };

        Assert.True(RetrievalCandidateSignals.LooksLikeNavigationalChunk(inlineIndex));
    }

    [Fact]
    public void LooksLikeSourceListChunk_detects_url_reference_lists()
    {
        var sourceList = new RagMatch(
            0.98,
            "doc-source",
            "Docs/Sources.pdf",
            "Sources.pdf",
            52,
            55,
            "sources",
            63,
            "Useful links http://example.com https://example.org www.example.net MAPAQ https://example.ca Metro https://metro.ca",
            1,
            "hash-source",
            "Useful links http://example.com https://example.org www.example.net MAPAQ https://example.ca Metro https://metro.ca",
            "sparse_bm25_v1",
            1,
            1,
            "Sources",
            "Sources",
            "section_window_v1",
            null,
            null,
            null);

        Assert.True(RetrievalCandidateSignals.LooksLikeSourceListChunk(sourceList));
    }

    [Fact]
    public void LooksLikeDocumentOverviewChunk_detects_introductory_document_marketing_text()
    {
        var overview = new RagMatch(
            0.98,
            "doc-overview",
            "Docs/Guide.pdf",
            "Guide.pdf",
            7,
            7,
            "overview",
            0,
            "Ce livre contient quelques exemples et vous permettra de preparer des soupes, des cremes ou une sauce bechamel.",
            1,
            "hash-overview",
            "Ce livre contient quelques exemples et vous permettra de preparer des soupes, des cremes ou une sauce bechamel.",
            "sparse_bm25_v1",
            1,
            1,
            "Introduction",
            "Introduction",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RetrievalCandidateSignals.LooksLikeDocumentOverviewChunk(overview));
    }

    [Fact]
    public void LooksLikeDocumentOverviewChunk_keeps_multilingual_actionable_profiles()
    {
        var profile = new RagMatch(
            0.82,
            "doc-profile",
            "Docs/Guide.pdf",
            "Guide.pdf",
            7,
            7,
            "profile",
            0,
            "Ce document presente des options pour apprentis avec materiel, etapes, risques et consignes.",
            1,
            "hash-profile",
            "Ce document presente des options pour apprentis avec materiel, etapes, risques et consignes.",
            "document_profile_v1",
            1,
            1,
            "Profil documentaire",
            "Profil documentaire",
            "document_profile",
            null,
            null,
            null);

        Assert.False(RetrievalCandidateSignals.LooksLikeDocumentOverviewChunk(profile));
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_quoted_titles_with_short_words_and_digits()
    {
        var terms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms(
            "Tu peux me faire une fiche claire pour « Sauce aux 4 fromages » : ingredients, etapes, temps et source ?");

        Assert.Contains("sauce aux 4 fromages", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_quoted_titles_for_exact_recipe_names()
    {
        var terms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms(
            "Tu peux me faire une fiche claire pour « Asperges vertes au miel » : ingredients, etapes, temps et source ?");

        Assert.Contains("asperges vertes au miel", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_ignores_comparison_quantity_words()
    {
        var terms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms(
            "Il y a plusieurs crèmes brûlées ? Compare-les si oui.");

        Assert.Contains("creme brulee", terms);
        Assert.DoesNotContain("plusieurs", terms);
    }

    [Fact]
    public void ShouldSupplementSparseWithLexicalFallback_keeps_long_quoted_title_queries()
    {
        Assert.True(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback(
            "cuisine",
            "Tu peux me faire une fiche claire pour « Asperges vertes au miel » : ingredients, etapes, temps et source ?"));
    }

    [Theory]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.")]
    [InlineData("Je dois animer un atelier avec 12 personnes : quelles fiches choisir ?")]
    [InlineData("Traduis en anglais les noms mais garde les parametres en francais.")]
    [InlineData("Compare les styles de trois procedures presentes dans la categorie.")]
    [InlineData("Combien de composants pour assembler le kit Alpha ?")]
    public void ShouldSupplementSparseWithLexicalFallback_skips_expensive_unanchored_queries(string query)
    {
        Assert.False(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback("generic", query));
    }

    [Fact]
    public void ShouldSupplementSparseWithLexicalFallback_allows_scoped_comparative_choice_queries()
    {
        Assert.True(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "Compare trois procedures stables et dis laquelle choisir pour une equipe."));
        Assert.True(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille."));

        Assert.False(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback(
            null,
            "Compare trois procedures stables et dis laquelle choisir pour une equipe."));
    }

    [Fact]
    public void BuildSupplementalSparseLexicalFallbackTerms_prefers_comparative_subject_over_choice_context()
    {
        var terms = RetrievalLexicalExpansion.BuildSupplementalSparseLexicalFallbackTerms(
            "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.");
        var operationalChoiceTerms = RetrievalLexicalExpansion.BuildSupplementalSparseLexicalFallbackTerms(
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?");

        Assert.Contains("mijote", terms);
        Assert.Contains("mijote plat", terms);
        Assert.Contains("mijoter plat", terms);
        Assert.Contains("francais", terms);
        Assert.DoesNotContain("repas", terms);
        Assert.DoesNotContain("famille", terms);
        Assert.Contains("supplier", operationalChoiceTerms);
        Assert.Contains("cheap", operationalChoiceTerms);
        Assert.Contains("transparency", operationalChoiceTerms);
        Assert.Contains("documented", operationalChoiceTerms);
        Assert.DoesNotContain("arbitrer", operationalChoiceTerms);
        Assert.True(RetrievalCandidateSignals.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RetrievalQueryPolicy.ShouldUseChoiceBetweenLexicalRoute(
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.Equal(
            "deux fournisseurs un tres bon marche mais peu transparent et un plus cher mais mieux documente",
            RetrievalQueryPolicy.BuildChoiceBetweenRetrievalQuery(
                "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "On doit choisir entre deux fournisseurs : un tr\u00e8s bon march\u00e9 mais peu transparent et un plus cher mais mieux document\u00e9. Comment les textes m\u2019aident \u00e0 arbitrer ?"));
        Assert.True(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Peux-tu faire un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6 ?"));
        Assert.True(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Build a table with old source, new source, change and impact for the 2030 baseline vs the updated synthesis."));
        Assert.False(RetrievalQueryPolicy.ShouldUseChoiceBetweenLexicalRoute(
            "On doit choisir entre US_FAR.pdf et WorldBank.pdf."));
        Assert.False(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?"));
    }

    [Fact]
    public void BuildSupplementalSparseLexicalFallbackTerms_preserves_accented_subject_terms()
    {
        var terms = RetrievalLexicalExpansion.BuildSupplementalSparseLexicalFallbackTerms(
            "Compare trois plats mijot\u00e9s fran\u00e7ais et dis lequel choisir pour un repas de famille.");

        Assert.Contains("mijote", terms);
        Assert.Contains("mijote plat", terms);
        Assert.Contains("mijoter plat", terms);
        Assert.Contains("mijot\u00e9s", terms);
        Assert.Contains("francais", terms);
        Assert.Contains("fran\u00e7ais", terms);
        Assert.DoesNotContain("repas", terms);
        Assert.DoesNotContain("famille", terms);
    }

    [Fact]
    public void ShouldSuppressUnanchoredSpecificResults_blocks_single_specific_dense_noise()
    {
        var denseNoise = new RagMatch(
            0.72,
            "doc-menu",
            "Cuisine/Menu.pdf",
            "Menu.pdf",
            4,
            4,
            "dense-noise",
            0,
            "Menus de semaine, legumes, desserts rapides et organisation des repas.",
            1,
            "hash-menu",
            "Menus de semaine, legumes, desserts rapides et organisation des repas.",
            "contextual_text_v1",
            1,
            1,
            "Menus",
            "Menus",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RetrievalSelection.ShouldSuppressUnanchoredSpecificResults("inertage", [denseNoise]));
    }

    [Fact]
    public void ShouldSuppressUnanchoredSpecificResults_keeps_specific_anchor_hits()
    {
        var anchored = new RagMatch(
            0.86,
            "doc-atex",
            "ATEX/Inerting.pdf",
            "Inerting.pdf",
            8,
            8,
            "anchor",
            0,
            "Inerting guidance for oxygen concentration and purge conditions.",
            1,
            "hash-atex",
            "Inerting guidance for oxygen concentration and purge conditions.",
            "sparse_bm25_v1",
            1,
            1,
            "Inerting",
            "Inerting",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.False(RetrievalSelection.ShouldSuppressUnanchoredSpecificResults("inerting", [anchored]));
    }

    [Fact]
    public void ShouldSuppressUnanchoredSpecificResults_keeps_multi_intent_semantic_queries()
    {
        var denseCandidate = new RagMatch(
            0.62,
            "doc-recipes",
            "Cuisine/Vegetarian.pdf",
            "Vegetarian.pdf",
            12,
            12,
            "semantic",
            0,
            "Recettes vegetariennes faciles avec legumes et cereales.",
            1,
            "hash-recipes",
            "Recettes vegetariennes faciles avec legumes et cereales.",
            "contextual_text_v1",
            1,
            1,
            "Recettes",
            "Recettes",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.False(RetrievalSelection.ShouldSuppressUnanchoredSpecificResults("quiero una receta vegetariana facil", [denseCandidate]));
    }

    [Fact]
    public void LooksLikeNavigationalChunk_detects_dense_recipe_index()
    {
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RetrievalCandidateSignals.LooksLikeNavigationalChunk(indexChunk));
    }

    [Fact]
    public void LooksLikeNavigationalChunk_detects_compact_pdf_index_extracted_from_live_corpus()
    {
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/nobilia-recettes-internationales-FR.pdf",
            "nobilia-recettes-internationales-FR.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Brochettes Raznjici et riz Djuvec 122C, DCanard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Chaussons de Cornouailles 22Churros avec sauce au chocolat et au piment 89Coq au vin 68Creme brulee 72Crepes polonais a la creme et aux pommes caramelisees 131Papas arrugadas 82158 | Index",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Brochettes Raznjici et riz Djuvec 122C, DCanard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Chaussons de Cornouailles 22Churros avec sauce au chocolat et au piment 89Coq au vin 68Creme brulee 72Crepes polonais a la creme et aux pommes caramelisees 131Papas arrugadas 82158 | Index",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RetrievalCandidateSignals.LooksLikeNavigationalChunk(indexChunk));
        Assert.True(RetrievalCandidateSignals.LooksLikeStrongNavigationalChunk(indexChunk));
    }

    [Fact]
    public void LooksLikeNavigationalChunk_keeps_structured_recipe_with_pdf_asset_index_marker()
    {
        var recipe = new RagMatch(
            1.02,
            "doc-neff",
            "Cuisine/14911887_9001116052_NFFS4I_fr_fm.pdf",
            "14911887_9001116052_NFFS4I_fr_fm.pdf",
            17,
            19,
            "asparagus",
            16,
            "16 Asperges vertes au miel [Index: ] MCRC01072833_BO_Gruener_Spargel_m_Honig-010MCRC01072992_SE_Gruener_Spargel_m_Honig-007 INGREDIENTS : 50 g de cerneaux de noix, 1 botte d'asperges vertes, 3 c. a s. de miel. PREPARATION 1. Faire chauffer la poele comme indique. 2. Faire griller les asperges.",
            1,
            "hash-neff",
            "16 Asperges vertes au miel [Index: ] MCRC01072833_BO_Gruener_Spargel_m_Honig-010MCRC01072992_SE_Gruener_Spargel_m_Honig-007 INGREDIENTS : 50 g de cerneaux de noix, 1 botte d'asperges vertes, 3 c. a s. de miel. PREPARATION 1. Faire chauffer la poele comme indique. 2. Faire griller les asperges.",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "section_window_v1",
            null,
            null,
            null);

        Assert.False(RetrievalCandidateSignals.LooksLikeNavigationalChunk(recipe));
    }

    [Fact]
    public void LooksLikeNavigationalChunk_keeps_measured_sequential_body_with_stale_navigation_score()
    {
        var content = TestMatch(
            text: """
ALPHA BETA MODULE
1 Mix the base with 150 g powder and 20 g binder for 12 min until the control value is stable.
2 Heat the carrier to 85 C for 12 min and record the pressure value before continuing.
3 Cut the inserts into equal pieces, add 50 cl carrier and keep the assembly moving for 20 s.
4 Place each insert in the fixture and hold it for 25 min while the surface cools.
5 Finish the assembly with 18 cl solution, verify the result and document the batch.
4 units 23 min 12 min 25 min.
""",
            chunkId: "measured-sequential",
            embeddingBasis: "local_title_token_route_v1") with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationReason = "inline_page_number_list",
            NavigationScore = 0.82,
            ContentDensityScore = 0.35
        };

        var context = RetrievalAnswerGuidance.BuildContextInfo(content);

        Assert.False(RetrievalCandidateSignals.LooksLikeNavigationalChunk(content));
        Assert.Equal(RetrievalContentClassifier.ContentRole, context.ContentRole);
        Assert.Null(context.NavigationReason);
        Assert.Equal(0.0, context.NavigationScore);
        Assert.True(context.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void LooksLikeStructuredQuantityList_detects_bulleted_measurements()
    {
        var text = "Boeuf bourguignon Pour 4 personnes • 1,2 kg de boeuf • 250 g de champignons • 100 g de lardons • 1,5 l de vin rouge • 2 c. a soupe d'huile.";

        Assert.True(RetrievalCandidateSignals.LooksLikeStructuredQuantityList(text));
    }

    [Fact]
    public void SuppressNavigationalNoise_removes_index_when_answer_chunks_exist()
    {
        var recipe = new RagMatch(
            0.76,
            "doc-recipe",
            "Cuisine/Classiques.pdf",
            "Classiques.pdf",
            5,
            5,
            "recipe",
            2,
            "Boeuf bourguignon Ingredients boeuf champignons vin rouge. Preparation faire revenir puis mijoter.",
            1,
            "hash-recipe",
            "Boeuf bourguignon Ingredients boeuf champignons vin rouge. Preparation faire revenir puis mijoter.",
            "sparse_bm25_v1",
            1,
            1,
            "Classiques",
            "Classiques > Boeuf bourguignon",
            "unit_exact_v1",
            null,
            null,
            null);
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        var suppressed = RetrievalSelection.SuppressNavigationalNoise("boeuf bourguignon grandes etapes", [indexChunk, recipe]);

        Assert.Equal("recipe", suppressed[0].ChunkId);
        Assert.DoesNotContain(suppressed, match => match.ChunkId == "index");
    }

    [Fact]
    public void SuppressNavigationalNoise_accepts_specific_anchor_as_answer_signal()
    {
        var recipe = new RagMatch(
            1.02,
            "doc-recipe",
            "Cuisine/Classiques.pdf",
            "Classiques.pdf",
            5,
            5,
            "recipe",
            2,
            "Boeuf bourguignon Pour 4 personnes. Degraisser la viande puis la tailler en morceaux. Faire revenir, ajouter le vin rouge et laisser mijoter.",
            1,
            "hash-recipe",
            "Boeuf bourguignon Pour 4 personnes. Degraisser la viande puis la tailler en morceaux. Faire revenir, ajouter le vin rouge et laisser mijoter.",
            "sparse_bm25_v1",
            1,
            1,
            "Classiques",
            "Classiques > Boeuf bourguignon",
            "unit_exact_v1",
            null,
            null,
            null);
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        var suppressed = RetrievalSelection.SuppressNavigationalNoise("C'est quoi les grandes etapes du boeuf bourguignon ?", [indexChunk, recipe]);

        Assert.Equal("recipe", Assert.Single(suppressed).ChunkId);
    }

    [Fact]
    public void PruneNavigationalSelections_removes_index_even_when_it_was_selected_early()
    {
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var recipe = indexChunk with
        {
            DocId = "doc-recipe",
            DocPath = "Cuisine/Classiques.pdf",
            DocName = "Classiques.pdf",
            PageStart = 71,
            PageEnd = 71,
            ChunkId = "recipe",
            ChunkIndex = 12,
            Text = "Boeuf bourguignon. Faites bien dorer la viande, ajoutez l'oignon, les carottes, le bouquet garni et le vin rouge, puis laissez mijoter.",
            EmbedText = "Boeuf bourguignon. Faites bien dorer la viande, ajoutez l'oignon, les carottes, le bouquet garni et le vin rouge, puis laissez mijoter."
        };
        var selected = new List<RagMatch> { indexChunk, recipe };

        RetrievalSelection.PruneNavigationalSelections("boeuf bourguignon grandes etapes", selected);

        Assert.Equal("recipe", Assert.Single(selected).ChunkId);
    }

    [Fact]
    public void CleanupLateSelectionBackfills_removes_late_navigation_when_content_exists()
    {
        var navigation = new RagMatch(
            0.68,
            "doc-a",
            "Technical/Certificate.pdf",
            "Certificate.pdf",
            null,
            null,
            "navigation",
            10,
            "Contents 1 Scope 2 References 3 Product table 4 Compliance summary",
            1,
            "hash-a",
            "Contents 1 Scope 2 References 3 Product table 4 Compliance summary",
            "sparse_bm25_v1",
            1,
            1,
            "Index",
            "Index",
            "unit_exact_v1",
            null,
            null,
            null) with
        {
            ContentRole = RetrievalContentClassifier.NavigationRole,
            NavigationScore = 0.82,
            ContentDensityScore = 0.35
        };
        var content = navigation with
        {
            Score = 0.62,
            ChunkId = "content",
            ChunkIndex = 11,
            Text = "The product certificate lists applicable substances, regulatory evidence, limits and compliance statements for the referenced material.",
            EmbedText = "The product certificate lists applicable substances, regulatory evidence, limits and compliance statements for the referenced material.",
            SectionTitle = "Compliance summary",
            HeadingPath = "Compliance summary",
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { navigation, content };

        RetrievalSelection.CleanupLateSelectionBackfills("which evidence documents regulatory compliance", selected);

        Assert.Equal("content", Assert.Single(selected).ChunkId);
    }

    [Fact]
    public void CleanupLateSelectionBackfills_removes_navigation_even_for_corpus_evidence_queries()
    {
        var navigation = TestMatch(
            text: "Contents 1 Scope 2 Substance list 3 Regulatory table 4 Source overview",
            docPath: "Docs/Certificate.pdf",
            page: 2,
            chunkId: "navigation",
            chunkType: "navigation_index_v1",
            score: 0.68,
            contentRole: RetrievalContentClassifier.NavigationRole,
            contentDensityScore: 0.35) with
        {
            NavigationScore = 0.82
        };
        var content = TestMatch(
            text: "The certificate documents regulated substances and includes the requested evidence with source references.",
            docPath: "Docs/Certificate.pdf",
            page: 3,
            chunkId: "content",
            score: 0.62,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { navigation, content };

        RetrievalSelection.CleanupLateSelectionBackfills(
            "Quels passages du corpus documentent les substances et dans quel type de document les trouve-t-on ?",
            selected);

        Assert.Equal("content", Assert.Single(selected).ChunkId);
    }

    [Fact]
    public void SuppressNavigationalNoise_keeps_resolved_title_route_when_target_chunk_looks_mixed_navigation()
    {
        var route = TestMatch(
            text: "P PREPARATION INGREDIENTS 1 botte d'asperges vertes miel. Faire chauffer la poele puis cuire.",
            embedText: "Matched title_anchor_route: Asperges vertes au miel\nP PREPARATION INGREDIENTS 1 botte d'asperges vertes miel. Faire chauffer la poele puis cuire.",
            docPath: "Cuisine/Robot.pdf",
            page: 18,
            chunkId: "route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 1.02) with
        {
            ContentRole = "mixed_navigation_content",
            NavigationScore = 0.69,
            ContentDensityScore = 0.42
        };
        var competingContent = TestMatch(
            text: "Terrine de legumes aux oeufs. Cassez les oeufs et enfournez.",
            docPath: "Cuisine/Other.pdf",
            page: 41,
            chunkId: "content",
            score: 0.92);

        var suppressed = RetrievalSelection.SuppressNavigationalNoise(
            "fiche Asperges vertes au miel ingredients etapes temps source",
            [route, competingContent]);

        Assert.Contains(suppressed, match => match.ChunkId == "route");
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(route));
    }

    [Fact]
    public void SuppressNavigationalNoise_removes_navigation_only_matches_for_content_lookup()
    {
        var indexOnly = TestMatch(
            text: "Index Alpha Beta Procedure, 42 Other Procedure, 44",
            embedText: "Index Alpha Beta Procedure, 42 Other Procedure, 44",
            docPath: "Ops/Manual.pdf",
            page: 99,
            chunkId: "index",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "navigation_index_v1",
            score: 0.72) with
        {
            ContentRole = "navigation",
            NavigationReason = "explicit_index_marker",
            NavigationScore = 0.88,
            ContentDensityScore = 0.20
        };

        var suppressed = RetrievalSelection.SuppressNavigationalNoise(
            "Donne la procedure Alpha Beta.",
            [indexOnly]);

        Assert.Empty(suppressed);
    }

    [Fact]
    public void PruneNavigationalSelections_keeps_resolved_navigation_route_after_selection()
    {
        var route = TestMatch(
            text: "Alpha Beta Procedure. Materials: lock, tag and gauge. Procedure: isolate the device, verify zero energy and record the result.",
            embedText: "Matched navigation_route: Alpha Beta Procedure\nAlpha Beta Procedure. Materials: lock, tag and gauge. Procedure: isolate the device, verify zero energy and record the result.",
            docPath: "Ops/Manual.pdf",
            page: 12,
            chunkId: "route",
            embeddingBasis: "navigation_route_v1",
            chunkType: "section_window_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.82
        };
        var content = TestMatch(
            text: "Other content candidate with enough body text to make pruning active.",
            docPath: "Ops/Other.pdf",
            page: 2,
            chunkId: "content",
            score: 0.80);
        var selected = new List<RagMatch> { route, content };

        RetrievalSelection.PruneNavigationalSelections("Alpha Beta Procedure", selected);

        Assert.Contains(selected, match => match.ChunkId == "route");
        Assert.Contains(selected, match => match.ChunkId == "content");
    }

    [Fact]
    public void PruneNavigationalSelections_removes_unconfirmed_navigation_route_after_selection()
    {
        var route = TestMatch(
            text: "The destination page contains operational details but not the requested title.",
            embedText: "Matched navigation_route: Alpha Beta Procedure\nThe destination page contains operational details but not the requested title.",
            docPath: "Ops/Manual.pdf",
            page: 12,
            chunkId: "route",
            embeddingBasis: "navigation_route_v1",
            chunkType: "navigation_index_v1",
            score: 1.02) with
        {
            ContentRole = "navigation",
            NavigationScore = 0.90,
            ContentDensityScore = 0.20
        };
        var content = TestMatch(
            text: "Other content candidate with enough body text to make pruning active.",
            docPath: "Ops/Other.pdf",
            page: 2,
            chunkId: "content",
            score: 0.80);
        var selected = new List<RagMatch> { route, content };

        RetrievalSelection.PruneNavigationalSelections("Alpha Beta Procedure", selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "route");
        Assert.Contains(selected, match => match.ChunkId == "content");
    }

    [Fact]
    public void PruneResidualNavigationalSelectionsWhenContentExists_preserves_resolved_status_route()
    {
        var statusRoute = TestMatch(
            text: "EN 13135-1 2004 AC. Correction text for the standard family.",
            docPath: "Docs/EN 13135-1 2004 AC.pdf",
            chunkId: "status",
            embeddingBasis: "document_status_operand_v1",
            chunkType: "navigation_index_v1",
            score: 0.92,
            contentRole: RetrievalContentClassifier.NavigationRole,
            contentDensityScore: 0.30) with
        {
            NavigationScore = 0.88
        };
        var content = TestMatch(
            text: "The main document contains substantive requirements for electrotechnical equipment.",
            docPath: "Docs/EN 13135-1 2004 Main.pdf",
            chunkId: "content",
            score: 0.80,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { statusRoute, content };

        RetrievalSelection.PruneResidualNavigationalSelectionsWhenContentExists("role du fichier AC", selected);

        Assert.Contains(selected, match => match.ChunkId == "status");
        Assert.Contains(selected, match => match.ChunkId == "content");
    }

    [Fact]
    public void SuppressNavigationalNoise_allows_inventory_queries()
    {
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var overview = indexChunk with { ChunkId = "overview", Score = 0.75, Text = "Ce document contient des recettes internationales.", EmbedText = "Ce document contient des recettes internationales." };

        var suppressed = RetrievalSelection.SuppressNavigationalNoise("Quels documents et quelles sources vois-tu dans le corpus ?", [indexChunk, overview]);

        Assert.Equal(1.02, suppressed[0].Score);
        Assert.Equal("index", suppressed[0].ChunkId);
    }

    [Fact]
    public void PruneWeakAdjacentSiblingSelections_removes_same_document_neighbor_with_low_query_coverage()
    {
        var anchor = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            33,
            33,
            "anchor",
            58,
            "Materials: concombres, miel, menthe. Procedure: melanger la sauce et servir frais.",
            1,
            "hash-exact",
            "Materials: concombres, miel, menthe. Procedure: melanger la sauce et servir frais.",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var neighbor = anchor with
        {
            ChunkId = "neighbor",
            PageStart = 34,
            PageEnd = 34,
            ChunkIndex = 59,
            Text = "Materials: tomates, oeufs, huile. Suggestion: quelques rondelles de concombre.",
            EmbedText = "Materials: tomates, oeufs, huile. Suggestion: quelques rondelles de concombre."
        };

        var selected = new List<RagMatch> { anchor, neighbor };

        RetrievalTitleSelection.PruneWeakAdjacentSiblingSelections("Concombres a la romaine", selected);

        Assert.Single(selected);
        Assert.Equal("anchor", selected[0].ChunkId);
    }

    [Fact]
    public void BuildSelectionHints_marks_low_quality_page_as_low_confidence()
    {
        var match = new RagMatch(
            0.86,
            "doc",
            "Knowledge/Procedure.pdf",
            "Procedure.pdf",
            4,
            4,
            "chunk",
            2,
            "Procedure: verify the sensor, adjust the threshold, record the result.",
            1,
            "hash",
            "Procedure: verify the sensor, adjust the threshold, record the result.",
            "sparse_bm25_v1",
            1,
            1,
            "Procedure",
            "Procedure",
            "unit_exact_v1",
            null,
            null,
            null);
        var quality = new RagItemExtractionQualityDto(
            PageQualityStatus: "manual_review_low_text",
            PageExtractionConfidence: 0.32,
            PageManualReviewRecommended: true,
            TextStatus: "low_confidence");

        var hints = RetrievalCandidateSignals.BuildSelectionHints(match, quality);

        Assert.Equal("low_confidence", hints.EvidenceRole);
        Assert.True(hints.QualityPenalty >= 10);
        Assert.Equal(0, hints.ActionabilityScore);
        Assert.True(hints.FragmentScore > 0);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_ignores_advice_filler_but_keeps_subject_terms()
    {
        var terms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms("Quelle sauce irait bien avec une entrecote ?");

        Assert.Contains("sauce", terms);
        Assert.Contains("entrecote", terms);
        Assert.DoesNotContain("irait", terms);
        Assert.DoesNotContain("bien", terms);
        Assert.DoesNotContain("irait bien", terms);
    }

    [Fact]
    public void RerankDenseMatches_prefers_structure_aware_chunks()
    {
        var broad = new RagMatch(0.50, "doc", "path", "doc.pdf", 1, 1, "a", 0, "text", 1, "hash", "embed", "chunk_text", 1, 1, "Intro", null, "legacy_word_window_v1", null, null, null);
        var precise = new RagMatch(0.49, "doc", "path", "doc.pdf", 1, 1, "b", 1, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Intro", "unit_exact_v1", null, null, null);

        var reranked = RetrievalCandidateSignals.RerankDenseMatches([broad, precise]);

        Assert.Equal("b", reranked[0].ChunkId);
    }

    [Fact]
    public void ResolveDenseContentCardAttachmentLimit_caps_large_candidate_sets()
    {
        Assert.Equal(0, RetrievalChannels.ResolveDenseContentCardAttachmentLimit(0));
        Assert.Equal(20, RetrievalChannels.ResolveDenseContentCardAttachmentLimit(20));
        Assert.Equal(64, RetrievalChannels.ResolveDenseContentCardAttachmentLimit(64));
        Assert.Equal(48, RetrievalChannels.ResolveDenseContentCardAttachmentLimit(200));
    }

    [Fact]
    public void ApplyAutocut_keeps_minimum_context_before_large_early_gap()
    {
        var matches = new List<RagMatch>
        {
            new(0.99, "doc-1", "Docs/A.pdf", "A.pdf", 1, 1, "a", 0, "alpha", 1, "hash-a", "alpha", "sparse_bm25_v1", 1, 1, "A", "A", "unit_exact_v1", null, null, null),
            new(0.98, "doc-2", "Docs/B.pdf", "B.pdf", 1, 1, "b", 0, "beta", 1, "hash-b", "beta", "sparse_bm25_v1", 1, 1, "B", "B", "unit_exact_v1", null, null, null),
            new(0.50, "doc-3", "Docs/C.pdf", "C.pdf", 1, 1, "c", 0, "gamma", 1, "hash-c", "gamma", "sparse_bm25_v1", 1, 1, "C", "C", "unit_exact_v1", null, null, null),
            new(0.49, "doc-4", "Docs/D.pdf", "D.pdf", 1, 1, "d", 0, "delta", 1, "hash-d", "delta", "sparse_bm25_v1", 1, 1, "D", "D", "unit_exact_v1", null, null, null),
            new(0.48, "doc-5", "Docs/E.pdf", "E.pdf", 1, 1, "e", 0, "epsilon", 1, "hash-e", "epsilon", "sparse_bm25_v1", 1, 1, "E", "E", "unit_exact_v1", null, null, null)
        };

        RetrievalSelection.ApplyAutocut(matches, absoluteMinScore: 0.25);

        Assert.Equal(5, matches.Count);
    }

    [Fact]
    public void ApplyAutocut_keeps_actionable_evidence_after_profile_only_comparison_lead()
    {
        var matches = Enumerable.Range(0, 8)
            .Select(index => new RagMatch(
                0.90 - (index * 0.01),
                $"profile-{index}",
                $"Docs/Profile-{index}.pdf",
                $"Profile-{index}.pdf",
                null,
                null,
                $"profile-{index}",
                null,
                "Document profile for apprentices with material checklist and failure risk notes.",
                1,
                $"hash-profile-{index}",
                "Document profile for apprentices with material checklist and failure risk notes.",
                "document_profile_v1",
                null,
                null,
                null,
                null,
                "document_profile",
                null,
                null,
                null))
            .ToList();
        matches.Add(new RagMatch(
            0.60,
            "doc-action",
            "Docs/Action.pdf",
            "Action.pdf",
            2,
            2,
            "action",
            2,
            "Procedure for apprentices. Material checklist, failure risk notes and setup controls.",
            1,
            "hash-action",
            "Procedure for apprentices. Material checklist, failure risk notes and setup controls.",
            "sparse_bm25_v1",
            2,
            2,
            "Procedure",
            "Procedure",
            "unit_exact_v1",
            null,
            null,
            null,
            ContentRole: RetrievalContentClassifier.ContentRole,
            ContentDensityScore: 1.0));

        RetrievalSelection.ApplyAutocut(
            matches,
            absoluteMinScore: 0.25,
            query: "Compare three procedures for apprentices: material, risks and setup notes.");

        Assert.Contains(matches, match => string.Equals(match.ChunkId, "action", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("same_section", 0.88)]
    [InlineData("next", 0.865)]
    [InlineData("prev", 0.86)]
    public void ComputeLinkedMatchScore_applies_expected_penalty(string linkType, double expected)
    {
        var score = RetrievalNavigation.ComputeLinkedMatchScore(0.90, linkType);

        Assert.Equal(expected, score, 3);
    }

    [Fact]
    public void ComputeLinkedMatchScore_applies_extra_penalty_when_expanding_from_linked_context()
    {
        var fromDense = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromLinked = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromLinked);
        Assert.Equal(0.865, fromLinked, 3);
    }

    [Fact]
    public void NormalizeSparseScore_returns_stable_monotonic_values()
    {
        Assert.Equal(0.0, RetrievalCandidateSignals.NormalizeSparseScore(0.0));

        var lower = RetrievalCandidateSignals.NormalizeSparseScore(0.01);
        var higher = RetrievalCandidateSignals.NormalizeSparseScore(0.25);

        Assert.InRange(lower, 0.45, 0.92);
        Assert.InRange(higher, 0.45, 0.92);
        Assert.True(higher > lower);
    }

    [Fact]
    public void ResolveProvenance_marks_sparse_matches_explicitly()
    {
        var sparse = new RagMatch(0.72, "doc", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "chunk", 0, "inerting guidance", 1, "hash", "Document: CEN.pdf", "sparse_bm25_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        Assert.Equal("retriever:sparse_bm25", RetrievalAnswerGuidance.ResolveProvenance(sparse));
        Assert.Equal("sparse_bm25", RetrievalAnswerGuidance.BuildProvenanceInfo(sparse).Channel);
    }

    [Fact]
    public void ApplyRerankScores_promotes_highest_reranked_candidate_and_sets_rerank_score()
    {
        var first = new RagMatch(0.90, "doc-a", "ATEX/A.pdf", "A.pdf", 1, 1, "a", 0, "alpha", 1, "hash-a", "alpha", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var second = new RagMatch(0.70, "doc-b", "ATEX/B.pdf", "B.pdf", 1, 1, "b", 0, "beta", 1, "hash-b", "beta", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var third = new RagMatch(0.60, "doc-c", "ATEX/C.pdf", "C.pdf", 1, 1, "c", 0, "gamma", 1, "hash-c", "gamma", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        var reranked = RetrievalCandidateSignals.ApplyRerankScores(
            [first, second, third],
            [
                new TeiClient.RerankItem(1, 0.91),
                new TeiClient.RerankItem(0, 0.22)
            ],
            rerankedPrefixCount: 2);

        Assert.Equal("b", reranked[0].ChunkId);
        Assert.Equal(0.91, reranked[0].RerankScore);
        Assert.Equal("c", reranked[^1].ChunkId);
    }

    [Fact]
    public void NormalizeRerankScore_maps_range_to_zero_one()
    {
        Assert.Equal(0.0, RetrievalCandidateSignals.NormalizeRerankScore(0.25, 0.25, 0.75), 3);
        Assert.Equal(1.0, RetrievalCandidateSignals.NormalizeRerankScore(0.75, 0.25, 0.75), 3);
        Assert.Equal(0.5, RetrievalCandidateSignals.NormalizeRerankScore(0.50, 0.25, 0.75), 3);
    }

    [Fact]
    public void ParseRerankResponse_supports_results_wrapper()
    {
        using var doc = JsonDocument.Parse("""
        {
          "results": [
            { "index": 1, "score": 0.91 },
            { "index": 0, "score": 0.22 }
          ]
        }
        """);

        var items = TeiClient.ParseRerankResponse(doc.RootElement);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Index);
        Assert.Equal(0.91, items[0].Score);
    }

    [Fact]
    public void ComputeLinkedMatchScore_keeps_exact_match_anchor_helpful_but_more_conservative_than_dense()
    {
        var fromDense = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromExact = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "exact_match");
        var fromSparse = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "sparse_bm25");
        var fromLinked = RetrievalNavigation.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromSparse);
        Assert.True(fromSparse > fromExact);
        Assert.True(fromDense > fromExact);
        Assert.True(fromExact > fromLinked);
        Assert.Equal(0.875, fromSparse, 3);
        Assert.Equal(0.87, fromExact, 3);
    }

    [Fact]
    public void ComputeMetadataReferenceScore_prefers_direct_term_overlap_over_numeric_key_overlap()
    {
        var exactReference = RetrievalMetadata.ComputeMetadataReferenceScore(
            exactReferenceMatches: 1,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var keyBacked = RetrievalMetadata.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var numericOnly = RetrievalMetadata.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 0,
            genericDirectMatches: 0,
            keyMatches: 1);

        Assert.True(exactReference > keyBacked);
        Assert.True(keyBacked > numericOnly);
        Assert.True(numericOnly >= 0.95);
    }

    [Fact]
    public void ContainsDocumentCompanionRelationIntent_does_not_treat_bare_ac_as_companion()
    {
        Assert.False(RetrievalNavigation.ContainsDocumentCompanionRelationIntent(
            "Cherche document AC dans le corpus."));
    }

    [Fact]
    public void HasSufficientPreciseContentSelection_accepts_strong_direct_route_with_unmatched_doc_alias()
    {
        var directRoute = TestMatch(
            text: "Menu Realiser la recette avec d'autres pates. NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients 250 g de nouilles chinoises, 400 g de crevettes, sauce soja et huile d'olive. Preparation: cuire, sauter et servir.",
            embedText: "Matched direct_title_token_route: nouilles sautees legumes crevettes\nMenu NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "nouilles-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.96);

        Assert.True(RetrievalSelection.HasSufficientPreciseContentSelection(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            ["nouilles sautees legumes-crevettes"],
            [directRoute]));

        var accentedQuery = "Donne-moi les nouilles sautées légumes-crevettes du PDF santé travail.";
        Assert.True(RetrievalSelection.HasSufficientPreciseContentSelection(
            accentedQuery,
            RetrievalLexicalExpansion.ExtractTitleLookupPhrases(accentedQuery),
            [directRoute]));
    }

    [Fact]
    public void HasSufficientPreciseContentSelection_accepts_focused_structured_content_titleish_lead()
    {
        var focusedFondue = TestMatch(
            text: "SAUCE CHOCOLAT Cette sauce est excellente avec de la glace, des fruits au sirop, en fondue. Materiel et ingredients pour un groupe.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            chunkId: "focused-fondue",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);

        Assert.True(RetrievalSelection.HasSufficientPreciseContentSelection(
            "Je cherche la fondue au chocolat pour un groupe d'enfants.",
            ["fondue au chocolat"],
            [focusedFondue]));
    }

    [Fact]
    public void HasSufficientPreciseContentSelection_rejects_title_only_ingredient_mentions()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.99);

        Assert.False(RetrievalSelection.HasSufficientPreciseContentSelection(
            "Donne-moi le bouillon de volaille.",
            ["bouillon de volaille"],
            [ingredientMention]));
    }

    [Fact]
    public void HasSufficientPreciseContentSelection_rejects_exact_title_without_requested_procedure_details()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Sauce moutarde\" : ingredients, etapes, temps et source ?";
        var weakExactTitle = TestMatch(
            text: "Slow cook Sauce SAUCE MOUTARDE SAUCE BEARNAISE 2 echalotes Dans le robot muni du couteau, mettez les 2 cl d'huile puis les 6 cl de vin blanc.",
            embedText: "Slow cook Sauce SAUCE MOUTARDE SAUCE BEARNAISE",
            docPath: "Cuisine/Robot.pdf",
            chunkId: "weak-exact-title",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.91,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.95);
        var structuredIngredientMention = TestMatch(
            text: "Salade composee. Ingredients: lentilles, citron, 1 cuillere a soupe de moutarde a l'ancienne, huile, sel, poivre. Technique: preparer la sauce en melangeant le citron et la moutarde.",
            docPath: "Cuisine/Salades.pdf",
            chunkId: "structured-ingredient-mention",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.88);
        var multiTitleExactTable = TestMatch(
            text: "Slow cook Sauce SAUCE MOUTARDE SAUCE BEARNAISE 2 echalotes Dans le robot muni du couteau 30 feuilles d'estragon hachoir ultrablade, mettez les 2 cl d'huile hachoir ultrablade, mettez les 6 cl de vin blanc.",
            embedText: "Slow cook Sauce SAUCE MOUTARDE SAUCE BEARNAISE",
            docPath: "Cuisine/Robot.pdf",
            chunkId: "multi-title-exact-table",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.873);

        Assert.False(RetrievalSelection.HasSufficientPreciseContentSelection(
            query,
            ["Sauce moutarde"],
            [weakExactTitle]));
        Assert.False(RetrievalSelection.HasSufficientPreciseContentSelection(
            query,
            ["Sauce moutarde"],
            [structuredIngredientMention]));
        Assert.False(RetrievalSelection.HasSufficientPreciseContentSelection(
            query,
            ["Sauce moutarde"],
            [multiTitleExactTable]));
    }

    [Fact]
    public void HasSufficientPreciseContentSelection_accepts_resolved_structured_title_lead()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Sauce chocolat\" : ingredients, etapes, temps et source ?";
        var standaloneTitleLead = TestMatch(
            text: "SAUCE CHOCOLAT\nIngredients: chocolat, lait, sucre. Preparation: melanger les ingredients, chauffer doucement, puis servir.",
            embedText: "Matched title_anchor_route: SAUCE CHOCOLAT\nSAUCE CHOCOLAT Ingredients: chocolat, lait, sucre. Preparation: melanger les ingredients.",
            docPath: "Cuisine/Sauces.pdf",
            chunkId: "standalone-title-lead",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.96);

        Assert.True(RetrievalSelection.HasSufficientPreciseContentSelection(
            query,
            ["Sauce chocolat"],
            [standaloneTitleLead]));
    }

    [Fact]
    public void ComputeDataHash_is_stable_and_sensitive_to_match_changes()
    {
        var left = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };
        var right = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };
        var changed = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };

        var leftHash = RetrievalAnswerGuidance.ComputeDataHash(left);
        var rightHash = RetrievalAnswerGuidance.ComputeDataHash(right);
        var changedHash = RetrievalAnswerGuidance.ComputeDataHash(changed);

        Assert.Equal(leftHash, rightHash);
        Assert.NotEqual(leftHash, changedHash);
    }

    [Fact]
    public void ResolveProvenance_returns_explicit_retriever_prefixed_label()
    {
        var match = new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null);

        var provenance = RetrievalAnswerGuidance.ResolveProvenance(match);

        Assert.Equal("retriever:linked_context", provenance);
    }

    [Fact]
    public void BuildProvenanceInfo_returns_structured_retrieval_metadata()
    {
        var match = new RagMatch(
            0.9,
            "doc",
            "path",
            "doc.pdf",
            4,
            5,
            "chunk-1",
            0,
            "text",
            1,
            "hash",
            "embed",
            "linked_context_v1",
            1,
            1,
            "Intro",
            "Chapter 1 > Intro",
            "unit_exact_v1",
            null,
            null,
            null,
            OffsetStart: 120,
            OffsetEnd: 133);

        var info = RetrievalAnswerGuidance.BuildProvenanceInfo(match);

        Assert.Equal("linked_context", info.Channel);
        Assert.Equal("retriever:linked_context", info.Label);
        Assert.Equal("hash", info.SourceHash);
        Assert.Equal("chunk-1", info.ChunkId);
        Assert.Equal(4, info.PageStart);
        Assert.Equal(5, info.PageEnd);
        Assert.Equal(120, info.OffsetStart);
        Assert.Equal(133, info.OffsetEnd);
    }

    [Fact]
    public void BuildProvenanceInfo_can_suppress_legacy_hash_fallback_for_public_search_contract()
    {
        var match = new RagMatch(
            0.9,
            "doc",
            "Knowledge/manual.pdf",
            "manual.pdf",
            1,
            1,
            "chunk-1",
            0,
            "text",
            1,
            "legacy-hash",
            "embed",
            "dense_qdrant",
            1,
            1,
            null,
            null,
            null,
            null,
            null,
            null);

        var info = RetrievalAnswerGuidance.BuildProvenanceInfo(match, sourceHash: null, allowLegacyHashFallback: false);

        Assert.Null(info.SourceHash);
    }

    [Fact]
    public void ResolveDocumentSourceIdentity_uses_doc_path_when_doc_id_is_not_usable()
    {
        var match = new RagMatch(
            0.9,
            "not-a-guid",
            "Knowledge\\manual.pdf",
            "manual.pdf",
            1,
            1,
            "chunk-1",
            0,
            "text",
            1,
            "legacy-hash",
            "embed",
            "dense_qdrant",
            1,
            1,
            null,
            null,
            null,
            null,
            null,
            null);
        var identities = new Dictionary<string, RagDocumentSourceIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Knowledge/manual.pdf"] = new("current-revision", "revision-aware-hash")
        };

        var resolved = RetrievalResultProjector.ResolveDocumentSourceIdentity(match, identities);

        Assert.NotNull(resolved);
        Assert.Equal("current-revision", resolved.RevisionId);
        Assert.Equal("revision-aware-hash", resolved.SourceHash);
    }

    [Fact]
    public void RagItemDto_marks_flat_provenance_as_legacy_backward_compat_field()
    {
        var property = typeof(RagItemDto).GetProperty("Provenance", BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);

        var obsolete = property!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.Contains("Use ProvenanceInfo instead", obsolete!.Message, StringComparison.Ordinal);

        var editorBrowsable = property.GetCustomAttribute<EditorBrowsableAttribute>();
        Assert.NotNull(editorBrowsable);
        Assert.Equal(EditorBrowsableState.Never, editorBrowsable!.State);
    }

    [Fact]
    public void ComputeHypQuestionsMatched_returns_true_when_query_overlaps_hypothetical_questions()
    {
        var matched = RetrievalNavigation.ComputeHypQuestionsMatched(
            "What does IND570 say about PLC integration?",
            [
                "What does MettlerToledo_IND570.pdf say about PLC integration?",
                "Which requirements from MettlerToledo_IND570.pdf apply to Shared data?"
            ]);

        Assert.True(matched);
    }

    [Fact]
    public void ComputeHypQuestionsMatched_returns_false_when_questions_exist_but_do_not_match_query()
    {
        var matched = RetrievalNavigation.ComputeHypQuestionsMatched(
            "Ou trouve-t-on EN 15281 ?",
            [
                "What does MettlerToledo_IND570.pdf say about PLC integration?",
                "Which requirements from MettlerToledo_IND570.pdf apply to Shared data?"
            ]);

        Assert.False(matched);
    }

    [Fact]
    public void ResolveHypQuestionsMatched_returns_non_null_when_hypothetical_questions_exist_for_item()
    {
        var byDocPath = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Programmation/Mettler/MettlerToledo_IND570.pdf"] = true
        };

        var matched = RetrievalNavigation.ResolveHypQuestionsMatched(
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            byDocPath);

        Assert.True(matched.HasValue);
        Assert.True(matched.Value);
    }

    [Fact]
    public void ResolveHypQuestionsMatched_normalizes_legacy_path_variants()
    {
        var byDocPath = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Programmation/Mettler/MettlerToledo_IND570.pdf"] = true
        };

        var matched = RetrievalNavigation.ResolveHypQuestionsMatched(
            "\\Programmation\\Mettler\\MettlerToledo_IND570.pdf",
            byDocPath);

        Assert.True(matched.HasValue);
        Assert.True(matched.Value);
    }

    [Fact]
    public void ResolveHypQuestionsMatched_returns_null_when_item_has_no_hypothetical_questions()
    {
        var byDocPath = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf"] = null
        };

        var matched = RetrievalNavigation.ResolveHypQuestionsMatched(
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            byDocPath);

        Assert.Null(matched);
    }

    [Fact]
    public void BuildDocumentCategoryPath_and_category_are_derived_from_doc_path()
    {
        Assert.Equal("ATEX/Guidance", RetrievalAnswerGuidance.BuildDocumentCategoryPath("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Equal("atex", RetrievalAnswerGuidance.BuildDocumentCategory("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Null(RetrievalAnswerGuidance.BuildDocumentCategoryPath("root-level.pdf"));
        Assert.Null(RetrievalAnswerGuidance.BuildDocumentCategory("RootLevel"));
    }

    [Fact]
    public void ResolveCategoryRef_uses_top_level_category_path()
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATEX"] = "cat_001",
            ["Programmation"] = "cat_002"
        };

        Assert.Equal("cat_001", RetrievalAnswerGuidance.ResolveCategoryRef("ATEX/Guidance", refs));
        Assert.Equal("cat_002", RetrievalAnswerGuidance.ResolveCategoryRef("Programmation/Mettler", refs));
        Assert.Null(RetrievalAnswerGuidance.ResolveCategoryRef("General", refs));
        Assert.Null(RetrievalAnswerGuidance.ResolveCategoryRef(null, refs));
    }

    [Fact]
    public void BuildContextInfo_returns_structured_chunk_context()
    {
        var match = new RagMatch(
            0.9,
            "doc",
            "path",
            "doc.pdf",
            1,
            1,
            "chunk-1",
            0,
            "text",
            1,
            "hash",
            "embed",
            "contextual_text_v1",
            1,
            1,
            "Safety",
            "Chapter 1 > Safety",
            "unit_exact_v1",
            "prev-1",
            "next-1",
            "same-1",
            SourceUnitOrdinals: [1],
            SourceUnitStartOrdinal: 1,
            SourceUnitEndOrdinal: 1,
            SourceUnitCount: 1,
            ChunkComposition: "single_unit");

        var context = RetrievalAnswerGuidance.BuildContextInfo(match);

        Assert.Equal("unit_exact_v1", context.ChunkType);
        Assert.Equal("Safety", context.SectionTitle);
        Assert.Equal("Chapter 1 > Safety", context.HeadingPath);
        Assert.Equal("prev-1", context.PrevChunkId);
        Assert.Equal("next-1", context.NextChunkId);
        Assert.Equal("same-1", context.SameSectionChunkId);
        Assert.Equal([1], context.SourceUnitOrdinals);
        Assert.Equal(1, context.SourceUnitStartOrdinal);
        Assert.Equal(1, context.SourceUnitEndOrdinal);
        Assert.Equal(1, context.SourceUnitCount);
        Assert.Equal("single_unit", context.ChunkComposition);
    }

    [Fact]
    public void ComputeContentEvidencePriority_does_not_boost_navigation_from_weak_cards()
    {
        var navigationWithoutCard = TestMatch(
            text: "Alpha Beta 12 Gamma Delta 18 Safety Reset 24 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            page: 2,
            chunkId: "navigation-no-card",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.20
        };
        var navigationWithWeakCard = navigationWithoutCard with
        {
            ChunkId = "navigation-weak-card",
            MatchedContentCards = [new RagMatchedContentCard("Alpha Beta Procedure", Signals: ["alpha", "beta"])]
        };
        var navigationWithStrongCard = navigationWithoutCard with
        {
            ChunkId = "navigation-strong-card",
            MatchedContentCards = [new RagMatchedContentCard("Alpha Beta Procedure", Signals: ["structured_facts"])]
        };

        Assert.Equal(
            RetrievalCandidateSignals.ComputeContentEvidencePriority(navigationWithoutCard),
            RetrievalCandidateSignals.ComputeContentEvidencePriority(navigationWithWeakCard),
            precision: 6);
        Assert.True(
            RetrievalCandidateSignals.ComputeContentEvidencePriority(navigationWithStrongCard)
            > RetrievalCandidateSignals.ComputeContentEvidencePriority(navigationWithoutCard));
    }

    [Fact]
    public void PromoteActionableSameDocumentEvidence_prefers_dense_same_doc_chunk_over_short_header_when_scores_tie()
    {
        var shortHeader = TestMatch(
            text: "Controls Device modes Preparation categories For 4 items",
            embedText: "Controls Device modes Preparation categories For 4 items",
            docPath: "Ops/Manual.pdf",
            page: 12,
            score: 0.918,
            chunkId: "short-header",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 1.0,
            MatchedContentCards = [new RagMatchedContentCard("Alpha Beta Procedure")]
        };
        var denseRoute = TestMatch(
            text: "Preparation. Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            embedText: "Matched title_anchor_route: Alpha Beta Procedure\nPreparation. Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            docPath: "Ops/Manual.pdf",
            page: 13,
            score: 0.918,
            chunkId: "dense-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.86
        };
        var selected = new List<RagMatch> { shortHeader, denseRoute };

        RetrievalSelection.PromoteActionableSameDocumentEvidence(selected);

        Assert.Equal("dense-route", selected[0].ChunkId);
        Assert.Equal("short-header", selected[1].ChunkId);
    }

    [Fact]
    public void PromoteActionableSameDocumentEvidence_does_not_promote_low_score_or_far_same_doc_tail()
    {
        var shortHeader = TestMatch(
            text: "Controls Device modes Preparation categories For 4 items",
            docPath: "Ops/Manual.pdf",
            page: 12,
            score: 1.02,
            chunkId: "short-header");
        var lowScoreDense = TestMatch(
            text: "Preparation. Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate, verify zero energy, replace, test and record the result.",
            docPath: "Ops/Manual.pdf",
            page: 13,
            score: 0.70,
            chunkId: "low-score-dense",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1");
        var farDense = lowScoreDense with
        {
            ChunkId = "far-dense",
            PageStart = 40,
            PageEnd = 40,
            Score = 1.01
        };
        var selected = new List<RagMatch> { shortHeader, lowScoreDense, farDense };

        RetrievalSelection.PromoteActionableSameDocumentEvidence(selected);

        Assert.Equal("short-header", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeDocumentHintSelections_preserves_document_hint_after_title_priorities()
    {
        var genericGuide = TestMatch(
            text: "Alpha procedure ingredients and steps from a generic guide.",
            docPath: "Docs/general-guide.pdf",
            chunkId: "generic-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.05);
        var hintedGuide = TestMatch(
            text: "Alpha procedure ingredients and steps from the named guide.",
            docPath: "Docs/acme-guide.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.01);
        var unrelated = TestMatch(
            text: "Alpha procedure ingredients and steps from another source.",
            docPath: "Docs/another-source.pdf",
            chunkId: "unrelated",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.03);
        var selected = new List<RagMatch> { genericGuide, unrelated, hintedGuide };

        RetrievalSelection.PrioritizeDocumentHintSelections("Comment appliquer alpha du guide Acme ?", selected);

        Assert.Equal("hinted-guide", selected[0].ChunkId);
        Assert.Equal("generic-guide", selected[1].ChunkId);
        Assert.Equal("unrelated", selected[2].ChunkId);
    }

    [Fact]
    public void PrioritizeDocumentHintSelections_does_not_promote_document_profile_over_content_chunk()
    {
        var contentChunk = TestMatch(
            text: "Alpha procedure ingredients and steps from a generic guide.",
            docPath: "Docs/general-guide.pdf",
            chunkId: "content",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 1.05);
        var hintedProfile = TestMatch(
            text: "Document profile for the named guide.",
            docPath: "Docs/acme-guide.pdf",
            chunkId: "profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 1.01);
        var selected = new List<RagMatch> { contentChunk, hintedProfile };

        RetrievalSelection.PrioritizeDocumentHintSelections("Comment appliquer alpha du guide Acme ?", selected);

        Assert.Equal("content", selected[0].ChunkId);
        Assert.Equal("profile", selected[1].ChunkId);
    }

    [Fact]
    public void ShouldRunDocumentHintScopedBackfill_runs_until_hinted_content_is_selected()
    {
        var genericContent = TestMatch(
            text: "Alpha Beta procedure from a general guide.",
            docPath: "Docs/general-guide.pdf",
            chunkId: "generic");
        var hintedProfile = TestMatch(
            text: "Document profile for the Acme guide.",
            docPath: "Docs/acme-guide.pdf",
            chunkId: "hinted-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");
        var hintedContent = TestMatch(
            text: "Alpha Beta procedure from the Acme guide.",
            docPath: "Docs/acme-guide.pdf",
            chunkId: "hinted-content");
        var query = "Donne-moi la procedure Alpha Beta du guide Acme.";
        var explicitFileQuery = "Dans ANSI Z535.1.pdf, trouve un tableau, une liste ou une section structuree.";
        var bareExplicitFileQuery = "Fais un resume prudent de ANSI Z535.1.pdf en distinguant ce qui depend de la qualite OCR.";
        var extensionlessExplicitFileQuery = "Dans NIST_SP_800_160v1r1_SSE_Appendices, retrouve les annexes importantes.";
        var explicitFileContent = TestMatch(
            text: "Structured section from the hinted document.",
            docPath: "Docs/ANSI Z535.1.pdf",
            chunkId: "explicit-file-content");
        var extensionlessExplicitFileContent = TestMatch(
            text: "Appendix A: Glossary. Appendix B: Acronyms.",
            docPath: "Docs/NIST_SP_800_160v1r1_SSE_Appendices.pdf",
            chunkId: "extensionless-explicit-file-content");
        var wrongSiblingFileContent = TestMatch(
            text: "Structured section from a sibling document.",
            docPath: "Docs/ANSI Z535.5.pdf",
            chunkId: "wrong-sibling-file-content");

        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(query, [genericContent]));
        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(query, [hintedProfile]));
        Assert.False(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(query, [genericContent, hintedContent]));
        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, []));
        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, [explicitFileContent]));
        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(bareExplicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(bareExplicitFileQuery, [explicitFileContent]));
        Assert.True(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(extensionlessExplicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RetrievalQueryPolicy.ShouldRunDocumentHintScopedBackfill(extensionlessExplicitFileQuery, [extensionlessExplicitFileContent]));
    }

    [Fact]
    public void DocumentOverview_precise_title_lookup_keeps_quoted_pdf_filename()
    {
        const string query = "Fais-moi une synthese utile de `ISO 19011 2011 Lignes directrices pour l'audit des systemes de management.pdf` : a quoi sert ce document ?";

        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.Contains(phrases, phrase =>
            phrase.Contains("19011", StringComparison.OrdinalIgnoreCase)
            && phrase.Contains("pdf", StringComparison.OrdinalIgnoreCase));
        Assert.False(RetrievalQueryPolicy.ShouldSkipPreciseTitleLookupForDocumentOverview(query, hasDocScope: false, mode: "balanced"));
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_near_exact_ocr_title_page_as_anchor()
    {
        var anchorId = Guid.NewGuid().ToString();
        var continuationId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "SAUCE BOLOGNANE Accompagnements Viandes Boeuf Modes de preparation Frire Cuire Categories de recettes Pour 4 portions SAUCE BOLOGNANE",
            docPath: "Cuisine/target.pdf",
            page: 27,
            chunkId: anchorId,
            embeddingBasis: "sparse_bm25_v1",
            score: 0.8775) with
        {
            ChunkIndex = 33,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.545,
            NextChunkId = continuationId
        };
        var continuation = TestMatch(
            text: "Ingredients : 1 oignon, 2 gousses d'ail, 1 carotte, 300 g de viande de boeuf hachee. Preparation 1. Eplucher et hacher l'ail et l'oignon. 2. Faire chauffer la poele. 3. Ajouter la viande hachee et cuire.",
            docPath: "Cuisine/target.pdf",
            page: 28,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.84) with
        {
            ChunkIndex = 34,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.88,
            PrevChunkId = anchorId
        };

        Assert.True(RetrievalTitleSelection.ShouldRunStructuredContinuationBackfill(query, [anchor]));

        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, [anchor], limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            [continuation],
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(continuationId, linked.ChunkId);
    }

    [Fact]
    public void StructuredContinuationBackfill_keeps_linked_preparation_after_incomplete_title_anchor()
    {
        var anchorId = Guid.NewGuid().ToString();
        var continuationId = Guid.NewGuid().ToString();
        var wrongPreviousId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour « Boulettes de poulet » : ingrédients, étapes, temps et source ?";
        var anchor = TestMatch(
            text: "DE POULET 102 BE A MASTER. BECOME A CHEF 50 minINGREDIENTSBOULETTES DE POULET. Pour les boulettes: 800 g de hauts de cuisses de poulet, chapelure, oeuf, sel.",
            docPath: "Cuisine/chefbot.pdf",
            page: 102,
            chunkId: anchorId,
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.94) with
        {
            SectionTitle = "BOULETTES DE POULET",
            HeadingPath = "BOULETTES DE POULET",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            NextChunkId = continuationId,
            SameSectionChunkId = continuationId
        };
        var unrelated = TestMatch(
            text: "SLOPPY JOES A LA DINDE. Ingredients: dinde, tomate. Preparation: cuire la viande.",
            docPath: "Cuisine/chefbot.pdf",
            page: 108,
            chunkId: Guid.NewGuid().ToString(),
            score: 0.91) with
        {
            SectionTitle = "SLOPPY JOES A LA DINDE",
            HeadingPath = "SLOPPY JOES A LA DINDE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var continuation = TestMatch(
            text: "103 BE A MASTER. BECOME A CHEF 50 min PREPARATION Placer l'accessoire lame dans le recipient. Ajouter l'oignon et l'ail. Programmer 10 secondes a vitesse 6 puis cuire la sauce tomate.",
            docPath: "Cuisine/chefbot.pdf",
            page: 103,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.82) with
        {
            SectionTitle = "BOULETTES DE POULET",
            HeadingPath = "BOULETTES DE POULET",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.92,
            PrevChunkId = anchorId
        };
        var wrongPrevious = TestMatch(
            text: "101 PREPARATION Former les boulettes de cabillaud et les disposer dans le panier vapeur.",
            docPath: "Cuisine/chefbot.pdf",
            page: 101,
            chunkId: wrongPreviousId,
            embeddingBasis: "linked_context_v1",
            score: 0.83) with
        {
            SectionTitle = "BOULETTES DE CABILLAUD",
            HeadingPath = "BOULETTES DE CABILLAUD",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.90,
            NextChunkId = anchorId
        };

        var selected = new List<RagMatch> { anchor, unrelated };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { wrongPrevious, continuation },
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(continuationId, linked.ChunkId);

        RetrievalTitleSelection.MergeStructuredContinuationSelections(query, selected, continuations, topK: 2);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(continuationId, second.ChunkId));
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_previous_chunk_for_split_direct_title_bridge()
    {
        var anchorId = Guid.NewGuid().ToString();
        var previousId = Guid.NewGuid().ToString();
        var earlyIngredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Comme un trifle aux fruits\" : ingredients, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "UN TRIFLE AUX FRUITS Rajoutez un peu de creme chantilly au dessus des fruits. 3 oeufs 150 g de sucre 20 g de Maizena 50 cl de lait demi-ecreme 18 cl de jus de citron 1 Dans le bol du robot muni du batteur, mettez les oeufs et le sucre.",
            embedText: "Matched direct_title_token_route: comme un trifle aux fruits\nUN TRIFLE AUX FRUITS Rajoutez un peu de creme chantilly au dessus des fruits.",
            docPath: "Cuisine/moulinex.pdf",
            page: 124,
            chunkId: anchorId,
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ChunkIndex = 493,
            SectionTitle = "CREME AU CITRON",
            HeadingPath = "CREME AU CITRON",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            PrevChunkId = previousId
        };
        var previous = TestMatch(
            text: "Temps total : 1 h 125 g de sucre 4 oeufs 125 g de farine 1 c. a c. de levure chimique 50 cl de lait 80 g de sucre en poudre 1 gousse de vanille 6 jaunes d'oeufs 3 kiwis 1 banane 100 g de fraises. 1 Pour la genoise : prechauffez le four et fouettez les oeufs avec le sucre. 2 Pour la creme : deposez les jaunes d'oeufs et le sucre, puis ajoutez le lait. 3 Pelez les kiwis et la banane, coupez les fraises, puis montez le trifle.",
            docPath: "Cuisine/moulinex.pdf",
            page: 124,
            chunkId: previousId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.873) with
        {
            ChunkIndex = 492,
            SectionTitle = "CREME AU CITRON",
            HeadingPath = "CREME AU CITRON",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            NextChunkId = anchorId
        };
        var earlyIngredients = TestMatch(
            text: "Temps total : 1 h 125 g de sucre 4 oeufs 125 g de farine 1 c. a c. de levure chimique 50 cl de lait 80 g de sucre en poudre 1 gousse de vanille 6 jaunes d'oeufs 3 kiwis 1 banane 100 g de fraises. 1 Pour la genoise : prechauffez le four.",
            docPath: "Cuisine/moulinex.pdf",
            page: 124,
            chunkId: earlyIngredientsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.861) with
        {
            ChunkIndex = 490,
            SectionTitle = "CREME AU CITRON",
            HeadingPath = "CREME AU CITRON",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous, earlyIngredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == previousId);
        Assert.Contains(continuations, match => match.ChunkId == earlyIngredientsId);

        RetrievalTitleSelection.MergeStructuredContinuationSelections(query, selected, continuations, topK: 3);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(previousId, second.ChunkId),
            third => Assert.Equal(earlyIngredientsId, third.ChunkId));
    }

    [Fact]
    public void StructuredContinuationBackfill_detects_precise_recipe_request_without_explicit_facets()
    {
        var anchorId = Guid.NewGuid().ToString();
        var continuationId = Guid.NewGuid().ToString();
        const string query = "Donne-moi la fiche du coq au vin dans le livre international.";
        var anchor = TestMatch(
            text: "Pour 4 personnes Cette recette francaise traditionnelle etait autrefois preparee avec un coq de plus de 18 mois. Prevoyez au moins douze heures de maceration.",
            embedText: "Matched linked_anchor_title: Coq au vin\nPour 4 personnes Cette recette francaise traditionnelle.",
            docPath: "Cuisine/nobilia.pdf",
            page: 69,
            chunkId: anchorId,
            embeddingBasis: "linked_context_v1",
            score: 0.918) with
        {
            ChunkIndex = 92,
            SectionTitle = "France",
            HeadingPath = "France",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = continuationId,
            SameSectionChunkId = continuationId,
            MatchedContentCards = [new RagMatchedContentCard("Coq au vin", Signals: ["structured_facts"])]
        };
        var continuation = TestMatch(
            text: "Coq au vin 1. Mettez les lamelles d'oignon, le celeri, la carotte, l'ail et le poivre dans une casserole avec le vin rouge. 2. Retirez les morceaux de poulet de la marinade. 3. Chauffez le beurre et l'huile dans une cocotte.",
            docPath: "Cuisine/nobilia.pdf",
            page: 69,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 93,
            SectionTitle = "France",
            HeadingPath = "France",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            PrevChunkId = anchorId
        };

        Assert.True(RetrievalTitleSelection.ShouldRunStructuredContinuationBackfill(query, [anchor]));

        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, [anchor], limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            [continuation],
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(continuationId, linked.ChunkId);
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_previous_chunks_when_title_route_starts_with_terminal_step()
    {
        var anchorId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        var stepTwoId = Guid.NewGuid().ToString();
        var stepThreeId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Sauce bearnaise\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "SAUCE BEARNAISE Lancez le programme sauce en vitesse 6 a 70 C pour 8 min avec le bouchon. 6 personnes 23 min 10 min SAUCE BEARNAISE Pour les detenteurs d'un Companion connecte bluetooth, vous pouvez remplacer le programme SAUCE par le mode manuel. 2 echalotes 2 cl d'huile 1 c. a s. de fond de veau 125 g de creme epaisse 1 c. a s. de moutarde Eau 1 Dans le robot muni du couteau hachoir ultrablade, mettez les echalotes epluchees puis mixez en vitesse 11 pendant 10 s.",
            embedText: "Matched local_title_token_route: Sauce bearnaise\nSAUCE BEARNAISE Lancez le programme sauce en vitesse 6 a 70 C.",
            docPath: "Cuisine/moulinex.pdf",
            page: 122,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ChunkIndex = 445,
            SectionTitle = "SAUCE BEARNAISE",
            HeadingPath = "SAUCE BEARNAISE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            PrevChunkId = stepThreeId
        };
        var ingredients = TestMatch(
            text: "Temps total : 31 min Temps total : 33 min 2 echalotes 30 feuilles d'estragon 6 cl de vin blanc 4 cl de vinaigre 6 cl d'eau 4 jaunes d'oeufs 170 g de beurre Sel Poivre 1 Dans le robot muni du couteau hachoir ultrablade, mettez les echalotes epluchees et les feuilles d'estragon puis mixez en Turbo pendant 10 s.",
            docPath: "Cuisine/moulinex.pdf",
            page: 122,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.873) with
        {
            ChunkIndex = 442,
            SectionTitle = "SAUCE BEARNAISE",
            HeadingPath = "SAUCE BEARNAISE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var stepTwo = TestMatch(
            text: "Remplacez le couteau hachoir ultrablade par le melangeur, ajoutez le vin blanc et le vinaigre puis lancez le robot en vitesse 3 a 95 C pour 15 min.",
            docPath: "Cuisine/moulinex.pdf",
            page: 122,
            chunkId: stepTwoId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.86) with
        {
            ChunkIndex = 443,
            SectionTitle = "SAUCE BEARNAISE",
            HeadingPath = "SAUCE BEARNAISE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var stepThree = TestMatch(
            text: "Quand les echalotes sont cuites, remplacez le melangeur par le batteur et ajoutez 6 cl d'eau, les jaunes d'oeufs et le beurre coupe en morceaux. Salez et poivrez puis lancez le programme sauce.",
            docPath: "Cuisine/moulinex.pdf",
            page: 122,
            chunkId: stepThreeId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.85) with
        {
            ChunkIndex = 444,
            SectionTitle = "SAUCE BEARNAISE",
            HeadingPath = "SAUCE BEARNAISE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            NextChunkId = anchorId
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { ingredients, stepTwo, stepThree },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);
        Assert.Contains(continuations, match => match.ChunkId == stepTwoId);
        Assert.Contains(continuations, match => match.ChunkId == stepThreeId);
    }

    [Fact]
    public void StructuredContinuationBackfill_keeps_same_page_recipe_chunks_after_title_only_anchor()
    {
        var anchorId = Guid.NewGuid().ToString();
        var preparationId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Brochettes de poulet grille a l'indonesienne. Pour 6 personnes. 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched direct_title_token_route: Brochettes de poulet grille a l'indonesienne\nBrochettes de poulet grille a l'indonesienne. Pour 6 personnes.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70
        };
        var preparation = TestMatch(
            text: "Mettez tous les ingredients necessaires a la marinade dans un grand saladier. Recouvrez et mettez au refrigerateur 3-12 heures. Faites tremper 18 pics en bois 30 minutes.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: preparationId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 52,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };
        var ingredients = TestMatch(
            text: "INGREDIENTS 1,5 kg de blancs de poulet. Pour la marinade 3 echalotes, 2 gousses d'ail, piment, coriandre, gingembre, sauce de soja, vinaigre et huile. Pour la sauce aux cacahouetes 175 g de graines de cacahouetes, oignon, ail, piment, sucre roux et jus de citron.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            score: 0.871) with
        {
            ChunkIndex = 54,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { preparation, ingredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == preparationId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);

        RetrievalTitleSelection.MergeStructuredContinuationSelections(query, selected, continuations, topK: 3);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(preparationId, second.ChunkId),
            third => Assert.Equal(ingredientsId, third.ChunkId));
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_high_confidence_local_title_route_without_readable_title()
    {
        var anchorId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Pour 6 personnes. 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched local title token route",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70
        };
        var ingredients = TestMatch(
            text: "INGREDIENTS 1,5 kg de blancs de poulet. Pour la marinade 3 echalotes, 2 gousses d'ail, gingembre, sauce de soja, vinaigre et huile.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 52,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { ingredients },
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(ingredientsId, linked.ChunkId);
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_same_page_steps_after_linked_title_only_anchor()
    {
        var anchorId = Guid.NewGuid().ToString();
        var stepsId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Trifle aux cerises\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Trifle aux cerises Une recette que l'on peut varier et preparer avec d'autres fruits, comme des fraises ou des peches.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 34,
            chunkId: anchorId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ChunkIndex = 41,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.60,
            NextChunkId = stepsId,
            SameSectionChunkId = stepsId
        };
        var steps = TestMatch(
            text: "Pour 4 personnes 1. Pour la creme anglaise : versez le lait dans une casserole. Fendez la gousse de vanille. Battez les jaunes d'oeufs puis incorporez le lait bouillant.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 34,
            chunkId: stepsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.90) with
        {
            ChunkIndex = 42,
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId,
            SameSectionChunkId = ingredientsId
        };
        var ingredients = TestMatch(
            text: "INGREDIENTS Pour la creme anglaise 600 ml de lait 1 gousse de vanille 2 c. a s. de sucre fin 4 jaunes d'oeufs. De plus 250 g de fond de genoise, confiture et framboises.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 34,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.8775) with
        {
            ChunkIndex = 44,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { steps, ingredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == stepsId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);
    }
}
