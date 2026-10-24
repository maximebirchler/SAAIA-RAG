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
    public void TitleAnchorNormalizer_folds_latin_ligatures_for_title_lookup()
    {
        var normalized = TitleAnchorNormalizer.NormalizeTitle("Bœuf à l'aïoli épicé et crème brûlée");
        var tokens = TitleAnchorNormalizer.BuildTitleTokens("Bœuf à l'aïoli épicé et crème brûlée");

        Assert.Equal("boeuf a l aioli epice et creme brulee", normalized);
        Assert.Contains("boeuf", tokens);
        Assert.Contains("aioli", tokens);
        Assert.Contains("creme", tokens);
        Assert.DoesNotContain("bœuf", tokens);
    }

    [Fact]
    public void ShouldUseBareVsComparativeOperandRoute_allows_version_like_operands_without_concept_phrase()
    {
        Assert.True(RetrievalQueryPolicy.ShouldUseBareVsComparativeOperandRoute("X r1 vs r2"));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_uses_parameter_target_after_for_as_precise_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Quels reglages de rotissage pour le rumsteck aux oignons grilles ?");

        Assert.Contains("rumsteck aux oignons grilles", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_ignores_secondary_qualifiers()
    {
        var tokens = RetrievalTitleSelection.ExtractFocusedLookupSelectionAnchorTokens(
            "Quelle recette utilise du miso blanc ?");

        Assert.Equal(["miso"], tokens);
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_prefers_associated_topic_over_table_facets()
    {
        var tokens = RetrievalTitleSelection.ExtractFocusedLookupSelectionAnchorTokens(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a conflit d interets.");

        Assert.Contains("conflit", tokens);
        Assert.Contains("interets", tokens);
        Assert.DoesNotContain("tableaux", tokens);
        Assert.DoesNotContain("criteres", tokens);
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_prefers_backtick_associated_topic_over_table_facets()
    {
        var tokens = RetrievalTitleSelection.ExtractFocusedLookupSelectionAnchorTokens(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a `conditions de paiement`.");

        Assert.Contains("conditions", tokens);
        Assert.Contains("paiement", tokens);
        Assert.DoesNotContain("tableaux", tokens);
        Assert.DoesNotContain("valeurs", tokens);
        Assert.DoesNotContain("criteres", tokens);
        Assert.DoesNotContain("listes", tokens);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_treats_direct_object_request_as_precise_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Donne-moi la charlotte aux p\u00eaches.");

        Assert.Contains("charlotte aux p\u00eaches", phrases);
        Assert.Contains("charlotte aux peches", phrases);
    }

    [Theory]
    [InlineData("Alpha Beta materials", "alpha beta", "materials")]
    [InlineData("Alpha Beta etapes", "alpha beta", "etapes")]
    [InlineData("Alpha Beta temps", "alpha beta", "temps")]
    [InlineData("Alpha Beta duration", "alpha beta", "duration")]
    [InlineData("Alpha Beta pages", "alpha beta", "pages")]
    public void ExtractTitleLookupPhrases_trims_trailing_lookup_facets_from_direct_title_expansions(
        string query,
        string expectedPhrase,
        string forbiddenFacet)
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);
        var tokens = RetrievalLexicalExpansion.BuildTitleLookupSqlTokens(phrases);

        Assert.Contains(expectedPhrase, phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains(forbiddenFacet, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(forbiddenFacet, tokens);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_does_not_trim_non_terminal_lookup_words()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery("source code transfer agreement");
        var titlePhrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases("source code transfer agreement");

        Assert.NotEqual("source code transfer", focused);
        Assert.DoesNotContain("source code transfer", titlePhrases);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_ignores_broad_document_overview_requests()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Retrouve les documents qui parlent de sonde de rotissage.");

        Assert.Empty(phrases);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_extracts_explicit_file_mentions()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Fais un resume prudent de ANSI Z535.2.pdf en distinguant ce qui depend de la qualite OCR.");
        var multi = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Comment citer correctement ANSI Z535.1.pdf et ANSI Z535.4.pdf si le texte est mal extrait ?");

        Assert.Contains("ansi z535 2 pdf", phrases);
        Assert.Contains("ansi z535 2", phrases);
        Assert.Contains("ansi z535 1 pdf", multi);
        Assert.Contains("ansi z535 4 pdf", multi);

        var encoded = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Dans `Pol%C3%ADtica%20de%20Privacidade_Fornecedores_14112023%20%281%29_eng-GB.pdf`, retrouve les passages utiles.");
        var lowercase = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Peux-tu me resumer `document_inexistant.pdf` et l'utiliser comme source principale ?");
        const string predicateAfterFileQuery =
            "Comment verifier qu'une valeur extraite de ISO 13849-1 2023 Safety of machinery - Safety-related parts of control systems - Part 1 General principles for design.pdf n'est pas issue d'une ancienne version ?";
        const string unicodePredicateAfterFileQuery =
            "Comment v\u00e9rifier qu\u2019une valeur extraite de ISO 13849-1 2023 Safety of machinery - Safety-related parts of control systems - Part 1 General principles for design.pdf n\u2019est pas issue d\u2019une ancienne version ?";
        var predicateAfterFile = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(predicateAfterFileQuery);
        var explicitPredicateAfterFile = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(predicateAfterFileQuery);
        var explicitUnicodePredicateAfterFile = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(unicodePredicateAfterFileQuery);

        Assert.Contains("pol c3 adtica 20de 20privacidade fornecedores 14112023 20 281 29 eng gb pdf", encoded);
        Assert.Contains("document inexistant pdf", lowercase);
        Assert.Contains(predicateAfterFile, phrase =>
            phrase.StartsWith("iso 13849 1 2023", StringComparison.Ordinal)
            && phrase.EndsWith("pdf", StringComparison.Ordinal));
        Assert.Contains(explicitPredicateAfterFile, phrase =>
            phrase.StartsWith("iso 13849 1 2023", StringComparison.Ordinal)
            && phrase.EndsWith("pdf", StringComparison.Ordinal));
        Assert.Contains(explicitUnicodePredicateAfterFile, phrase =>
            phrase.StartsWith("iso 13849 1 2023", StringComparison.Ordinal)
            && phrase.EndsWith("pdf", StringComparison.Ordinal));
        Assert.DoesNotContain(explicitPredicateAfterFile, phrase =>
            phrase.StartsWith("comment verifier", StringComparison.Ordinal));
        Assert.True(RetrievalQueryPolicy.ShouldUseExplicitDocumentScopedBackfillRoute(
            predicateAfterFileQuery,
            "valeur extraite ancienne version"));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_builds_numeric_variants_for_natural_language_dates()
    {
        var french = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Dans les minutes reunion FOMC des 17-18 septembre 2024, quels sujets sont traites ?");
        var english = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "How do the FOMC Sep 17-18 2024 minutes describe inflation?");

        Assert.Contains("fomc 2024 09 18", french);
        Assert.Contains("fomc minutes 2024 09 18", french);
        Assert.Contains("fomc 2024 09 18", english);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_prioritizes_near_date_title_anchor_over_instruction_words()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "J'ai besoin d'une réponse claire : dans les minutes réunion FOMC des 17-18 septembre 2024, quelle est la structure ?");

        Assert.True(Array.IndexOf(phrases.ToArray(), "fomc 2024 09 18") >= 0);
        Assert.True(Array.IndexOf(phrases.ToArray(), "fomc 2024 09 18") < 8);
        Assert.DoesNotContain("claire 2024 09 18", phrases.Take(6));
    }

    [Fact]
    public void ExtractDateAnchoredTitleLookupPhrases_reads_dated_document_reference_after_preamble()
    {
        var phrases = RetrievalLexicalExpansion.ExtractDateAnchoredTitleLookupPhrases(
            "J'ai besoin d'une r\u00e9ponse claire : dans les minutes r\u00e9union FOMC des 17-18 septembre 2024, quelle est la structure ?");

        Assert.Contains("fomc 2024 09 18", phrases);
        Assert.Contains("fomc minutes 2024 09 18", phrases);
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_keeps_short_numeric_date_parts()
    {
        var tokens = RetrievalLexicalExpansion.BuildTitleLookupSqlTokens(["fomc 2024 09 18"]);

        Assert.Contains("2024", tokens);
        Assert.Contains("09", tokens);
        Assert.Contains("18", tokens);
        Assert.Contains("fomc", tokens);
    }

    [Fact]
    public void SelectionsCoverDateAnchoredDocument_detects_date_in_document_name()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                "Minutes content.",
                docPath: "Emails/FOMC_Minutes_2024_09_18.pdf",
                chunkId: "fomc-date")
        };

        Assert.True(RetrievalLexicalExpansion.SelectionsCoverDateAnchoredDocument(
            "Dans les minutes réunion FOMC des 17-18 septembre 2024, quels sujets sont traités ?",
            selected));
        Assert.False(RetrievalLexicalExpansion.SelectionsCoverDateAnchoredDocument(
            "Dans les minutes réunion FOMC des 6-7 novembre 2024, quels sujets sont traités ?",
            selected));
    }

    [Fact]
    public void SelectionsCoverDateAnchoredDocument_ignores_body_only_date_mentions()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                "This body mentions FOMC minutes from 17-18 September 2024, but it is not the dated source document.",
                docPath: "Emails/Meeting_Notes_2025_01_29.pdf",
                chunkId: "wrong-date-body")
        };

        Assert.False(RetrievalLexicalExpansion.SelectionsCoverDateAnchoredDocument(
            "Dans les minutes reunion FOMC des 17-18 septembre 2024, quels sujets sont traites ?",
            selected));
    }

    [Fact]
    public void IsResolvedTitleOrNavigationRoute_treats_document_name_routes_as_resolved()
    {
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Document-name routed content.",
            embeddingBasis: "date_document_name_v1")));
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Reference document-name routed content.",
            embeddingBasis: "standard_reference_document_name_v1")));
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Reference operand routed content.",
            embeddingBasis: "reference_version_operand_document_name_v1")));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_uses_standard_reference_as_document_anchor()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Explique le role du fichier AC par rapport au document principal EN 13135-1.");

        Assert.Contains("en 13135 1", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains("role du fichier", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_keeps_compact_document_code_without_quotes()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Donne-moi une statistique precise du WG3 sans citation de tableau.");

        Assert.Contains("wg3", phrases);
        Assert.DoesNotContain("sans", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_expands_slash_separated_parts()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Quelles sources citer separement pour ISO 13849-1/2 ?");

        Assert.Contains("iso 13849 1", phrases);
        Assert.Contains("iso 13849 2", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_keeps_nist_and_bare_versioned_references()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Compare les procedures NIST 800-53A avec les appendices 800-53r5.");

        Assert.Contains("nist 800 53a", phrases);
        Assert.Contains("800 53a", phrases);
        Assert.Contains("800 53r5", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_keeps_accented_comparison_surface()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Compare les procédures d’évaluation NIST 800-53A avec les appendices 800-53r5 : quel est le lien entre contrôle et assessment ?");

        Assert.Contains("nist 800 53a", phrases);
        Assert.Contains("800 53a", phrases);
        Assert.Contains("800 53r5", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_expands_family_references_to_parts()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?");

        Assert.Contains("iso 13849 1", phrases);
        Assert.Contains("iso 13849 2", phrases);
        Assert.Contains("iso 13849", phrases);
    }

    [Fact]
    public void ComputeReferenceVersionOperandTitleSignal_matches_compact_and_spaced_variants()
    {
        const string query = "Compare la version 2002, le prA1 et la version 2008+A1.";

        Assert.True(RetrievalQueryPolicy.ComputeReferenceVersionOperandTitleSignal(
            query,
            "EN 1005-3 2002 prA1.pdf") > 0);
        Assert.True(RetrievalQueryPolicy.ComputeReferenceVersionOperandTitleSignal(
            query,
            "EN 1005-3 2002+A1 2008 Safety of machinery.pdf") > 0);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_accepts_ul_references()
    {
        var phrases = RetrievalLexicalExpansion.ExtractStandardReferenceTitleLookupPhrases(
            "Peux-tu donner une exigence UL 508A sans citer la page parce que le PDF est scanne ?");

        Assert.Contains("ul 508a", phrases);
    }

    [Fact]
    public void BuildComparativeOperandTitleBackfillQueries_expands_cross_language_technical_phrases()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeOperandTitleBackfillQueries(
            "Si une valeur est illisible dans salle propre vs documentation technique, peux-tu la deduire depuis l autre document ?");

        Assert.Contains("cleanroom", queries);
        Assert.Contains("documentation technique", queries);
    }

    [Fact]
    public void ShouldSkipPreciseTitleLookupForDocumentOverview_keeps_quoted_overview_on_profile_path()
    {
        Assert.True(RetrievalQueryPolicy.ShouldSkipPreciseTitleLookupForDocumentOverview(
            "Quelles sources parlent de \"corrosion interne\" ?",
            hasDocScope: false,
            mode: "balanced"));
        Assert.False(RetrievalQueryPolicy.ShouldSkipPreciseTitleLookupForDocumentOverview(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_keeps_surface_and_folded_diacritic_forms()
    {
        var tokens = RetrievalLexicalExpansion.BuildTitleLookupSqlTokens(["charlotte aux p\u00eaches"]);

        Assert.Contains("p\u00eaches", tokens);
        Assert.Contains("peches", tokens);
        Assert.Contains("charlotte", tokens);
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_extracts_quoted_multi_token_recipe_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");
        var tokens = RetrievalLexicalExpansion.BuildTitleLookupSqlTokens(phrases);

        Assert.Contains("escalope", tokens);
        Assert.Contains("volaille", tokens);
        Assert.Contains("champignons", tokens);
        Assert.Contains("pommes", tokens);
        Assert.Contains("darphin", tokens);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_extracts_short_quoted_structured_sheet_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce moutarde \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("sauce moutarde", phrases);
    }

    [Fact]
    public void ShouldSkipQuotedTitleFullScan_skips_unquoted_precise_titles_without_doc_scope()
    {
        Assert.True(RetrievalNavigation.ShouldSkipQuotedTitleFullScan(
            "Resume-moi le cassoulet toulousain sans oublier les temps.",
            hasDocScope: false));
        Assert.False(RetrievalNavigation.ShouldSkipQuotedTitleFullScan(
            "Resume-moi le \"cassoulet toulousain\" sans oublier les temps.",
            hasDocScope: false));
        Assert.False(RetrievalNavigation.ShouldSkipQuotedTitleFullScan(
            "Resume-moi le cassoulet toulousain sans oublier les temps.",
            hasDocScope: true));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public void ResolveLocalTitleTokenMinimumHits_requires_multiple_tokens_for_precise_titles(
        int tokenCount,
        int expected)
    {
        Assert.Equal(expected, RetrievalChannels.ResolveLocalTitleTokenMinimumHits(tokenCount));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_late_body_mentions()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?");
        var bodyMention = string.Join(
            ' ',
            Enumerable.Repeat("Preparation etapes ingredients cuisson", 18))
            + " Servez avec une sauce bolognaise ou une autre sauce.";

        Assert.False(RetrievalChannels.HasLocalTitleTokenLeadEvidence(bodyMention, null, null, phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_footer_page_titles()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Donne-moi la charlotte aux peches.");
        var footerTitle = string.Join(
            ' ',
            Enumerable.Repeat("Ingredients preparation materiel technique fruits sucre moule dessert", 24))
            + " Copyright 2003 Charlotte aux peches 330011 fiche dessert Page 1";

        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(footerTitle, null, null, phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_footer_title_followed_by_page_marker()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Boulettes de viande suedoises accompagnees de sauce \u00bb : ingredients, etapes, temps et source ?");
        var footerTitle = string.Join(
            ' ',
            Enumerable.Repeat("Ingredients preparation sauce viande hachee cuisson poele fond boeuf creme", 28))
            + " Boulettes de viande suedoises accompagnees de sauce12 Scandinavie 75g de pain blanc";

        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(footerTitle, null, null, phrases));
        Assert.False(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            footerTitle.Replace(" sauce12 Scandinavie", " sauce250 g"),
            null,
            null,
            phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_lead_titles_and_ocr_joined_titles()
    {
        var shortTitle = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?");
        var joinedTitle = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");

        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "SAUCE BOLOGNAISE\n\nIngredients puis preparation detaillee.",
            null,
            null,
            shortTitle));
        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "FLORA LYCEE PROFESSIONNEL Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS POMMES DARPHIN50 min Ingredients.",
            null,
            null,
            joinedTitle));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_measured_ingredient_occurrence_in_lead()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Source pour \u00ab Bouillon de volaille \u00bb ?");

        Assert.False(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "Ingredients: 75 cl de bouillon de volaille, 2 carottes, thym. Preparation faire mijoter puis filtrer.",
            null,
            null,
            phrases));
        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "BOUILLON DE VOLAILLE\n\nIngredients: carcasse, legumes, eau. Preparation faire mijoter.",
            null,
            null,
            phrases));
    }

    [Fact]
    public void MatchedQuotedTitleHasTargetTitleEvidence_rejects_scattered_body_terms()
    {
        var scattered = TestMatch(
            text: "Clafoutis aux cerises. Astuce: pomme, fraise, framboise, mangue, noix de coco rapee.",
            embedText: "Matched quoted title: riz gluant coco mangue\nClafoutis aux cerises. Astuce: mangue et noix de coco rapee.",
            docPath: "Cuisine/Other.pdf");

        Assert.False(RetrievalCandidateSignals.MatchedQuotedTitleHasTargetTitleEvidence(scattered));
        Assert.False(RetrievalCandidateSignals.HasProfileTitleHint(scattered));
    }

    [Fact]
    public void MatchedQuotedTitleHasTargetTitleEvidence_accepts_real_lead_titles()
    {
        var lead = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients puis preparation detaillee.",
            embedText: "Matched quoted title: sauce bolognaise; sauce bolognaise au soja texture\nSAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients puis preparation detaillee.",
            docPath: "Cuisine/Recipe.pdf");

        Assert.True(RetrievalCandidateSignals.MatchedQuotedTitleHasTargetTitleEvidence(lead));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(lead));
    }

    [Fact]
    public void BuildTitleTokens_adds_singular_variants_for_plural_lookup_terms()
    {
        var tokens = TitleAnchorNormalizer.BuildTitleTokens("Terrines de legumes", maxTokens: 12);

        Assert.Contains("terrines", tokens);
        Assert.Contains("terrine", tokens);
        Assert.Contains("legumes", tokens);
    }

    [Theory]
    [InlineData(8, false, 32)]
    [InlineData(20, false, 80)]
    [InlineData(64, false, 128)]
    [InlineData(20, true, 20)]
    public void ResolveLocalTitleTokenCandidateLimit_overfetches_global_title_lookups(int topK, bool hasDocScope, int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveLocalTitleTokenCandidateLimit(topK, hasDocScope));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(5, 15)]
    [InlineData(16, 48)]
    [InlineData(20, 48)]
    [InlineData(80, 48)]
    public void ResolveTitleAnchorNavigationCatalogLimit_caps_large_internal_recovery_windows(
        int topK,
        int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveTitleAnchorNavigationCatalogLimit(topK));
    }

    [Theory]
    [InlineData(20, true, false, false, 320)]
    [InlineData(80, false, true, false, 320)]
    [InlineData(80, false, true, true, 1024)]
    [InlineData(80, false, false, false, 1024)]
    [InlineData(128, false, false, true, 1024)]
    public void ResolveLocalTitleTokenSqlCandidateLimit_uses_scoped_first_pass_with_full_fallback(
        int routeLimit,
        bool hasDocScope,
        bool hasCategoryScope,
        bool fullFallback,
        int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveLocalTitleTokenSqlCandidateLimit(
            routeLimit,
            hasDocScope,
            hasCategoryScope,
            fullFallback));
    }

    [Theory]
    [InlineData(320, 320, 1024, 12, 20, false, true)]
    [InlineData(320, 320, 1024, 12, 20, true, false)]
    [InlineData(320, 320, 1024, 0, 20, true, true)]
    [InlineData(319, 320, 1024, 12, 20, false, false)]
    [InlineData(320, 320, 1024, 20, 20, false, false)]
    [InlineData(320, 320, 320, 12, 20, false, false)]
    public void ShouldRetryLocalTitleTokenSqlCoverage_retries_only_when_initial_page_saturated_and_underfilled(
        int returnedRowCount,
        int initialCandidateLimit,
        int fullCandidateLimit,
        int matchCount,
        int topK,
        bool hasCategoryScope,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldRetryLocalTitleTokenSqlCoverage(
            returnedRowCount,
            initialCandidateLimit,
            fullCandidateLimit,
            matchCount,
            topK,
            hasCategoryScope));
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_title_subject_ahead_of_facets()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Compare les deux quiches lorraines du corpus : diff\u00e9rences d\u2019ingr\u00e9dients, m\u00e9thode et style.");

        Assert.Contains("quiches lorraines", queries);
        Assert.Contains("quiches lorraine", queries);
        Assert.DoesNotContain("quiches lorraines ingredient", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_can_split_compared_segments_when_profile_assist_is_active()
    {
        const string query = "Compare les recettes \u00e9tudiantes et les recettes d\u00e9jeuner au travail : objectif budget et \u00e9quilibre.";

        Assert.Empty(RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(query));

        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            query,
            allowProfileAssistBackfill: true);

        Assert.Contains("recettes etudiantes", queries);
        Assert.Contains(queries, static q => q.StartsWith("recettes dejeuner", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_focuses_bare_vs_operands_on_concept_phrases()
    {
        const string query = "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?";

        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            query,
            allowProfileAssistBackfill: true);

        Assert.Contains("code conduct", queries);
        Assert.Contains("uk code conduct", queries);
        Assert.Contains("edp code conduct", queries);
        Assert.DoesNotContain(queries, static q => q.Contains("exigences de conduite fournisseur uk vs edp", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_adds_profile_variants_for_bare_vs_operands()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("hazardous locations", queries);
        Assert.Contains("machines electriques", queries);
        Assert.Contains("machinery electrical", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_shared_suffix_operands()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("performance audit", queries);
        Assert.Contains("compliance audit", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_translates_label_safety_operands()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Si une valeur est illisible dans couleurs vs labels de securite, peux-tu la deduire depuis l'autre document ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("safety labels", queries);
        Assert.Contains("safety colors", queries);
        Assert.True(queries.Count <= 6);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_compact_document_codes()
    {
        var standardQueries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Compare SSDF et control baselines : est-ce la meme granularite d'exigence ?",
            allowProfileAssistBackfill: true);
        var reportQueries = RetrievalQueryPolicy.BuildComparativeTitleBackfillQueries(
            "Compare les annexes IPCC WG3 et le rapport SDG : quelles informations sont chiffrees ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("ssdf", standardQueries);
        Assert.Contains("control baselines", standardQueries);
        Assert.Contains("wg3", reportQueries);
        Assert.Contains("sdg", reportQueries);
    }

    [Fact]
    public void ComputeComparativeOperandTitleSignal_requires_operand_and_concept()
    {
        const string query = "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?";

        Assert.True(RetrievalQueryPolicy.ComputeComparativeOperandTitleSignal(query, "GOVUK Supplier Code of Conduct v3") > 0);
        Assert.True(RetrievalQueryPolicy.ComputeComparativeOperandTitleSignal(query, "EDP Supplier Code of Conduct 2025") > 0);
        Assert.Equal(0, RetrievalQueryPolicy.ComputeComparativeOperandTitleSignal(query, "US FAR acquisition requirements"));
        Assert.Equal(0, RetrievalQueryPolicy.ComputeComparativeOperandTitleSignal(query, "EDP Group Portugal General Conditions for the Supply of Goods and Services"));
    }

    [Fact]
    public void ShouldRunComparativeTitleBackfill_recovers_when_profile_assist_has_no_supported_profile()
    {
        const string query = "Compare les recettes \u00e9tudiantes et les recettes d\u00e9jeuner au travail : objectif budget et \u00e9quilibre.";
        var sparseOnly = new[]
        {
            TestMatch(
                "Budget and work notes without the student comparison target.",
                chunkId: "work-sparse",
                embeddingBasis: "sparse_bm25_v1",
                score: 0.88)
        };

        Assert.True(RetrievalQueryPolicy.ShouldRunComparativeTitleBackfill(query, sparseOnly));
    }

    [Fact]
    public void ShouldRunComparativeTitleBackfill_keeps_searching_when_profiles_have_no_cards()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var coveredProfileWithoutCards = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            chunkId: "profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.90);

        Assert.True(RetrievalQueryPolicy.ShouldRunComparativeTitleBackfill(query, [coveredProfileWithoutCards]));
    }

    [Fact]
    public void ShouldRunComparativeTitleBackfill_runs_for_bare_vs_operand_queries()
    {
        var selected = new[]
        {
            TestMatch(
                "Hazardous locations profile summary.",
                chunkId: "hazardous-profile",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile",
                score: 0.92)
        };

        Assert.True(RetrievalQueryPolicy.ShouldRunComparativeTitleBackfill(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            selected));
    }

    [Fact]
    public void BuildRouteTitleFallbackMatchedContentCards_exposes_concrete_title_route_as_card()
    {
        var route = TestMatch(
            text: "Churros avec sauce au chocolat. Ingredients, preparation, frying steps and serving notes.",
            embedText: "Matched title_anchor_route: Churros avec sauce au chocolat\nIngredients and preparation.",
            docPath: "Cuisine/international.pdf",
            page: 90,
            chunkId: "churros-route",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.84) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };

        var cards = RetrievalCandidateSignals.BuildRouteTitleFallbackMatchedContentCards(route);

        Assert.NotNull(cards);
        var card = Assert.Single(cards);
        Assert.Equal("Churros avec sauce au chocolat", card.Title);
        Assert.Equal(90, card.PageStart);
        Assert.Equal("title_anchor_route", card.Kind);
        Assert.Contains("route_title", card.Signals ?? []);
    }

    [Fact]
    public void BuildChunkSectionTitleFallbackMatchedContentCards_exposes_same_chunk_title_and_proof()
    {
        var content = TestMatch(
            text: "Donnez un air de fête à cette recette classique des petits-déjeuners et des goûters en y ajoutant un trait de crème fraîche.",
            docPath: "Cuisine/chefbot.pdf",
            page: 118,
            chunkId: "milk-shake-content",
            embeddingBasis: "contextual_text_v3",
            score: 0.84) with
        {
            SectionTitle = "MILK-SHAKE CHOCOLAT ET CRÈME FRAÎCHE",
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };

        var cards = RetrievalCandidateSignals.BuildChunkSectionTitleFallbackMatchedContentCards(content);

        Assert.NotNull(cards);
        var card = Assert.Single(cards);
        Assert.Equal("MILK-SHAKE CHOCOLAT ET CRÈME FRAÎCHE", card.Title);
        Assert.Equal(118, card.PageStart);
        Assert.Equal("chunk_section_title", card.Kind);
        Assert.Contains("same_chunk_source_text", card.Signals ?? []);
        Assert.NotNull(card.Evidence);
        Assert.Contains(
            "petits-déjeuners",
            card.Evidence!.Value.GetProperty("sourceText").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildChunkSectionTitleFallbackMatchedContentCards_rejects_navigation_or_generic_heading()
    {
        var navigation = TestMatch(
            text: "Table des matières présentant les chapitres du document et leurs pages.",
            page: 2) with
        {
            SectionTitle = "TABLE DES MATIÈRES",
            ContentRole = RetrievalContentClassifier.NavigationRole
        };
        var generic = TestMatch(
            text: "Ajouter les ingrédients, mélanger, puis cuire pendant vingt minutes.",
            page: 3) with
        {
            SectionTitle = "PRÉPARATION",
            ContentRole = RetrievalContentClassifier.ContentRole
        };

        Assert.Null(RetrievalCandidateSignals.BuildChunkSectionTitleFallbackMatchedContentCards(navigation));
        Assert.Null(RetrievalCandidateSignals.BuildChunkSectionTitleFallbackMatchedContentCards(generic));
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_catalog_overview_profiles()
    {
        const string query = "Quels documents contient cette categorie et a quoi servent-ils chacun ?";
        var selected = new List<RagMatch>
        {
            TestMatch(
                "Procurement profile summary with useful document scope and limits.",
                docPath: "Achats/A.pdf",
                chunkId: "profile-a",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile",
                score: 0.46)
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections(query, selected);

        Assert.Single(selected);
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_compact_structured_title_between_time_and_measure()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?");

        Assert.True(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "SAUCE AU POIVRE\n\n226227 Temps total : 12 min Temps total : 17 min 1 c. a c. de poivre concasse 1 cl de cognac 10 cl de creme liquide 1 c. a c. de fond de veau 1 c. a c. de farine 15 cl d'eau 1 Dans le robot muni du batteur, mettez le poivre, le cognac, la creme liquide, le fond de veau et la farine.",
            sectionTitle: "lc. ac. de farine",
            headingPath: "lc. ac. de farine",
            phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_measured_body_phrase_without_title_boundary()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?");

        Assert.False(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            "Ingredients 250 g de viande avec une sauce au poivre maison, sel et herbes. Preparation: saisir puis servir.",
            sectionTitle: null,
            headingPath: null,
            phrases));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_runs_when_precise_lookup_only_found_navigation()
    {
        var navigation = TestMatch(
            text: "Index des recettes Patatas Bravas 22 Poelee au riz 50.",
            embedText: "Index des recettes Patatas Bravas 22 Poelee au riz 50.",
            chunkType: "navigation_index_v1") with
        {
            ContentRole = "navigation",
            NavigationScore = 0.88
        };

        Assert.True(RetrievalCandidateSignals.ShouldBackfillFuzzyTitleLead("Donne la recette des patattas bravas.", [navigation]));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_skips_when_content_candidate_exists()
    {
        var content = TestMatch(
            text: "Patatas Bravas ingredients pommes de terre preparation.",
            embedText: "Patatas Bravas ingredients pommes de terre preparation.");

        Assert.False(RetrievalCandidateSignals.ShouldBackfillFuzzyTitleLead("Donne la recette des patatas bravas.", [content]));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_skips_explicit_file_queries()
    {
        Assert.False(RetrievalCandidateSignals.ShouldBackfillFuzzyTitleLead(
            "Cite les conditions de paiement dans `Example_General_Conditions.pdf`.",
            []));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_skips_broad_comparative_synthesis()
    {
        Assert.False(RetrievalCandidateSignals.ShouldBackfillFuzzyTitleLead(
            "Peux-tu faire un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6 ?",
            []));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_runs_when_content_lacks_focused_title_coverage()
    {
        var weakContent = TestMatch(
            text: "Techniques de cuisson du boeuf hache et conservation.",
            embedText: "Techniques de cuisson du boeuf hache et conservation.");

        Assert.True(RetrievalCandidateSignals.ShouldBackfillFuzzyTitleLead("Tu as la recette du boeuf bourguingnon ?", [weakContent]));
    }

    [Fact]
    public void ShouldConstrainPreciseTitleLookup_does_not_limit_enumerative_recipe_searches()
    {
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?"));
    }

    [Fact]
    public void NavigationRouteMinimumConfidence_includes_projected_point_seven_values()
    {
        var projectedPointSeven = (double)0.70f;

        Assert.True(projectedPointSeven < 0.70d);
        Assert.True(projectedPointSeven >= RetrievalQueryPolicy.NavigationRouteMinimumConfidence);
    }

    [Fact]
    public void HasQuotedTitlePlacementEvidence_rejects_card_title_when_chunk_text_is_different_recipe()
    {
        var mexicanSalad = TestMatch(
            text: "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger. Salade mexicaine.",
            embedText: "document_name: si-on-cuisinait.pdf\nprevious_context:\nSalade de pates ingredients pates surimi feta olives poivron.\ncontext:\nIngredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre.",
            chunkId: "mexican-salad",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1") with
        {
            MatchedContentCards = [new RagMatchedContentCard("Salade de pates", Signals: ["structured_facts"])]
        };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?");

        Assert.False(RetrievalChannels.HasLocalTitleTokenLeadEvidence(
            mexicanSalad.Text,
            mexicanSalad.SectionTitle,
            mexicanSalad.HeadingPath,
            phrases));
        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, mexicanSalad));
    }

    [Fact]
    public void ShouldRunPreciseTitleLinkedBackfill_detects_context_only_title_match_in_top_results()
    {
        var contextOnlyTop = TestMatch(
            text: "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Salade mexicaine.",
            embedText: "context:\nIngredients haricots rouges ananas mais poivron olives tomates.\nprevious_context:\nSalade de pates ingredients pates surimi feta olives poivron.",
            chunkId: "context-only-top",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1") with
        {
            PrevChunkId = "previous-recipe"
        };
        var directTitleTop = contextOnlyTop with
        {
            ChunkId = "direct-title-top",
            Text = "Salade de pates. Ingredients pates surimi feta olives poivron. Technique cuire et refroidir.",
            EmbedText = "Salade de pates. Ingredients pates surimi feta olives poivron. Technique cuire et refroidir."
        };

        Assert.True(RetrievalSelection.ShouldRunPreciseTitleLinkedBackfill(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [contextOnlyTop]));
        Assert.False(RetrievalSelection.ShouldRunPreciseTitleLinkedBackfill(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [directTitleTop]));
    }

    [Fact]
    public void PrioritizePreciseTitleBackfillSelections_keeps_exact_backfill_before_same_document_context()
    {
        var contextOnlyTop = TestMatch(
            text: "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Salade mexicaine.",
            embedText: "context:\nIngredients haricots rouges ananas mais poivron olives tomates.\nprevious_context:\nSalade de pates ingredients pates surimi feta olives poivron.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            chunkId: "context-only-top",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1") with
        {
            PageStart = 30,
            PageEnd = 30
        };
        var otherSameDocument = contextOnlyTop with
        {
            ChunkId = "other-same-document",
            PageStart = 27,
            PageEnd = 27,
            Text = "Ingredients lentilles artichauts olives vinaigrette. Salade de lentilles."
        };
        var exactBackfill = contextOnlyTop with
        {
            ChunkId = "exact-backfill",
            PageStart = 29,
            PageEnd = 29,
            Text = "Salade de pates. Ingredients 150 g de pates surimi feta olives poivron. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur.",
            EmbedText = "Salade de pates. Ingredients 150 g de pates surimi feta olives poivron. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur.",
            EmbeddingBasis = "local_title_token_route_v1"
        };
        var extendedTitleBackfill = contextOnlyTop with
        {
            ChunkId = "extended-title-backfill",
            PageStart = 15,
            PageEnd = 15,
            Text = "Menu Salade de pates, endives, tomates et thon. Ingredients pates endives tomates thon vinaigrette.",
            EmbedText = "Menu Salade de pates, endives, tomates et thon. Ingredients pates endives tomates thon vinaigrette.",
            EmbeddingBasis = "local_title_token_route_v1",
            Score = 1.02
        };

        var prioritized = RetrievalSelection.PrioritizePreciseTitleBackfillSelections(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            [extendedTitleBackfill, contextOnlyTop, otherSameDocument, exactBackfill],
            [extendedTitleBackfill, exactBackfill]);

        Assert.Equal("exact-backfill", prioritized[0].ChunkId);
    }

    [Fact]
    public void PrioritizePreciseTitleBackfillSelections_keeps_global_local_title_above_generic_selected_results()
    {
        var genericSauce = TestMatch(
            text: "Sauce au poivre vert. Ingredients creme poivre vert cognac. Preparation: melanger et servir chaud.",
            embedText: "Sauce au poivre vert. Ingredients creme poivre vert cognac. Preparation: melanger et servir chaud.",
            docPath: "Cuisine/generic-sauces.pdf",
            chunkId: "generic-sauce",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1") with
        {
            Score = 1.02,
            PageStart = 12,
            PageEnd = 12
        };
        var otherGeneric = genericSauce with
        {
            ChunkId = "other-generic",
            DocPath = "Cuisine/other-sauces.pdf",
            Text = "Sauce au poivre rapide avec fond de veau et creme. Servir avec une viande grillee.",
            EmbedText = "Sauce au poivre rapide avec fond de veau et creme. Servir avec une viande grillee.",
            PageStart = 34,
            PageEnd = 34
        };
        var exactLocalTitle = genericSauce with
        {
            ChunkId = "exact-local-title",
            DocPath = "Cuisine/device-manual.pdf",
            PageStart = 121,
            PageEnd = 121,
            Score = 0.918,
            Text = "SAUCE AU POIVRE\n\n226227 Temps total : 12 min Temps total : 17 min 1 c. a c. de poivre concasse 1 cl de cognac 10 cl de creme liquide 1 c. a c. de fond de veau 1 c. a c. de farine 15 cl d'eau 1 Dans le robot muni du batteur, mettez le poivre, le cognac, la creme liquide, le fond de veau et la farine.",
            EmbedText = "SAUCE AU POIVRE\n\n226227 Temps total : 12 min Temps total : 17 min 1 c. a c. de poivre concasse 1 cl de cognac 10 cl de creme liquide 1 c. a c. de fond de veau 1 c. a c. de farine 15 cl d'eau 1 Dans le robot muni du batteur, mettez le poivre, le cognac, la creme liquide, le fond de veau et la farine.",
            EmbeddingBasis = "local_title_token_route_v1",
            ChunkType = "footer_titled_item_window_v1",
            ContentRole = "content",
            ContentDensityScore = 0.86
        };

        var prioritized = RetrievalSelection.PrioritizePreciseTitleBackfillSelections(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?",
            [genericSauce, otherGeneric, exactLocalTitle],
            [exactLocalTitle]);

        Assert.Equal("exact-local-title", prioritized[0].ChunkId);
    }

    [Fact]
    public void PrunePreciseTitleTailSelections_keeps_quoted_title_anchor_with_digit()
    {
        var profileNoise = new RagMatch(
            1.02,
            "doc-noise",
            "Cuisine/facilitemps.pdf",
            "facilitemps.pdf",
            56,
            56,
            "profile-noise",
            69,
            "SAUCE TOMATE DE BASE Ingredients: tomates, oignon, ail. Preparation: mijoter.",
            1,
            "hash-noise",
            "Matched profile title: Sauce au fromage\nDocument: facilitemps.pdf\nContext: sauce tomate.",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var quotedAnchor = profileNoise with
        {
            DocId = "doc-target",
            DocPath = "Cuisine/robot.pdf",
            DocName = "robot.pdf",
            PageStart = 121,
            PageEnd = 121,
            ChunkId = "quoted-anchor",
            ChunkIndex = 306,
            Text = "Temps total : 17 min. Vin blanc, comte, gorgonzola, parmesan et poivre.",
            EmbedText = "Matched quoted title: SAUCE AUX 4 FROMAGES\nTemps total : 17 min. Vin blanc, comte, gorgonzola, parmesan et poivre.",
            ChunkType = "section_window_v1"
        };

        var selected = new List<RagMatch> { profileNoise, quotedAnchor };

        RetrievalSelection.PrioritizeQuotedTitleSelections("\"Sauce aux 4 fromages\"", selected);
        RetrievalTitleSelection.PrunePreciseTitleTailSelections("\"Sauce aux 4 fromages\"", selected);

        Assert.Single(selected);
        Assert.Equal("quoted-anchor", selected[0].ChunkId);
    }

    [Fact]
    public void ShouldAllowNavigationCatalogRouteForBroadExploration_requires_scope_and_broad_request()
    {
        Assert.True(RetrievalQueryPolicy.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Je cherche a avoir un plan pour la semaine avec des options variees.",
            hasScope: true));
        Assert.True(RetrievalQueryPolicy.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Donne-moi juste une liste d'elements disponibles dans ce dossier.",
            hasScope: true));

        Assert.False(RetrievalQueryPolicy.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Je cherche a avoir un plan pour la semaine avec des options variees.",
            hasScope: false));
        Assert.False(RetrievalQueryPolicy.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Combien de pieces faut-il pour cet assemblage ?",
            hasScope: true));
    }

    [Fact]
    public void PruneWeakTitleExpansionSelections_removes_adjacent_neighbor_without_title_anchors()
    {
        var exactTitle = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            33,
            33,
            "exact-title",
            53,
            "Materials: 2 concombres, creme, moutarde. Procedure: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            1,
            "hash-exact",
            "Materials: 2 concombres, creme, moutarde. Procedure: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var adjacentNeighbor = exactTitle with
        {
            Score = 0.91,
            ChunkId = "adjacent-neighbor",
            ChunkIndex = 54,
            PageStart = 34,
            PageEnd = 34,
            Text = "Materials: 6 tomates, 4 oeufs, huile, vinaigre. Suggestions: remplacer les rondelles d'oeuf par des rondelles de concombre. TOMATES a la printaniere3300110088",
            EmbedText = "Materials: 6 tomates, 4 oeufs, huile, vinaigre. Suggestions: remplacer les rondelles d'oeuf par des rondelles de concombre. TOMATES a la printaniere3300110088",
            EmbeddingBasis = "sparse_bm25_v1"
        };

        var selected = new List<RagMatch> { exactTitle, adjacentNeighbor };

        RetrievalTitleSelection.PruneWeakTitleExpansionSelections("Concombres a la romaine", selected);

        Assert.Single(selected);
        Assert.Equal("exact-title", selected[0].ChunkId);
    }

    [Fact]
    public void PruneWeakTitleExpansionSelections_keeps_specific_section_continuation()
    {
        var exactTitle = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            33,
            33,
            "exact-title",
            53,
            "Ingredients: 2 concombres, creme, moutarde. Preparation: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            1,
            "hash-exact",
            "Ingredients: 2 concombres, creme, moutarde. Preparation: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            "sparse_bm25_v1",
            1,
            1,
            "Concombres a la romaine",
            "Concombres a la romaine",
            "unit_exact_v1",
            null,
            null,
            null);
        var continuation = exactTitle with
        {
            Score = 0.88,
            ChunkId = "continuation",
            ChunkIndex = 54,
            PageStart = 34,
            PageEnd = 34,
            Text = "Suite: reserver au frais, rectifier l'assaisonnement et servir.",
            EmbedText = "Suite: reserver au frais, rectifier l'assaisonnement et servir.",
            EmbeddingBasis = "linked_context_v1",
            ChunkType = "section_window_v1"
        };

        var selected = new List<RagMatch> { exactTitle, continuation };

        RetrievalTitleSelection.PruneWeakTitleExpansionSelections("Concombres a la romaine", selected);

        Assert.Equal(2, selected.Count);
        Assert.Equal("exact-title", selected[0].ChunkId);
        Assert.Equal("continuation", selected[1].ChunkId);
    }

    [Fact]
    public void ShouldConstrainPreciseTitleLookup_detects_title_like_queries_but_not_explanatory_requests()
    {
        Assert.True(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Concombres a la romaine"));
        Assert.True(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("je veux une recette de concombres romaine"));
        Assert.True(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Saumon avec sauce yaourt-menthe"));

        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Une procedure enfant."));
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("comment preparer des concombres pour la semaine"));
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("compare concombres romaine et tomates printanieres"));
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Quelles recettes avec des lentilles corail existent dans les PDF ?"));
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Quelles recettes sont les plus adaptées pour un déjeuner de semaine rapide ?"));
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup("Quel dessert français choisir pour un repas chic ?"));
    }

    [Theory]
    [InlineData("Quelles normes de securite instrumentee sont citees autour de l'inertage dans ce document ?")]
    [InlineData("J'ai un client qui me demande si le projet respecte la norme xxx sur l'inertage. Tu peux m'aider ?")]
    [InlineData("Avant de dire au client que son inertage respecte EN 15281, qu'est-ce qu'il faut lui demander ?")]
    public void ShouldConstrainPreciseTitleLookup_keeps_runtime_content_questions_open(string query)
    {
        Assert.False(RetrievalQueryPolicy.ShouldConstrainPreciseTitleLookup(query));
    }

    [Fact]
    public void PrunePreciseTitleTailSelections_removes_low_confidence_tail_after_strong_title_answer()
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
            "Ingredients: concombres, miel, menthe. Preparation: melanger. CONCOMBRES\u00e0 LA ROMAINE110077",
            1,
            "hash-exact",
            "Ingredients: concombres, miel, menthe. Preparation: melanger. CONCOMBRES\u00e0 LA ROMAINE110077",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var weakTail = anchor with
        {
            Score = 1.02,
            DocId = "doc-weak",
            DocPath = "Cuisine/Other.pdf",
            DocName = "Other.pdf",
            ChunkId = "weak",
            PageStart = 5,
            PageEnd = 5,
            Text = "General seasonal product list with cucumbers and vegetables.",
            EmbedText = "General seasonal product list with cucumbers and vegetables."
        };
        var lexicalTail = anchor with
        {
            Score = 1.02,
            DocId = "doc-lexical",
            DocPath = "Cuisine/Tzatziki.pdf",
            DocName = "Tzatziki.pdf",
            ChunkId = "lexical-tail",
            PageStart = 12,
            PageEnd = 12,
            Text = "Tzatziki: ingredients: 3 concombres moyens, yaourt grec, ail et menthe. Servir avec une salade de laitue romaine.",
            EmbedText = "Matched profile title: Entre deux tranches de pain complet\nDocument: Tzatziki.pdf\nContext: Tzatziki: ingredients: 3 concombres moyens, yaourt grec, ail et menthe. Servir avec une salade de laitue romaine.\nPreviousContext: CONCOMBRES A LA ROMAINE"
        };
        var sameTitleElsewhere = anchor with
        {
            Score = 0.88,
            DocId = "doc-title",
            DocPath = "Cuisine/Variant.pdf",
            DocName = "Variant.pdf",
            ChunkId = "variant",
            Text = "CONCOMBRES A LA ROMAINE Ingredients: concombres, vinaigre, menthe.",
            EmbedText = "CONCOMBRES A LA ROMAINE Ingredients: concombres, vinaigre, menthe."
        };
        var seasonalListTail = anchor with
        {
            Score = 0.72,
            DocId = "doc-seasonal",
            DocPath = "Cuisine/Seasonal.pdf",
            DocName = "Seasonal.pdf",
            ChunkId = "seasonal-list",
            PageStart = 30,
            PageEnd = 30,
            Text = "Legumes de printemps: artichaut, asperge, aubergine, carotte, chou-fleur, concombre, courgette, cresson, epinard, salade frisee, laitue ou romaine, tomate.",
            EmbedText = "Legumes de printemps: artichaut, asperge, aubergine, carotte, chou-fleur, concombre, courgette, cresson, epinard, salade frisee, laitue ou romaine, tomate."
        };

        var selected = new List<RagMatch> { lexicalTail, anchor, weakTail, seasonalListTail, sameTitleElsewhere };

        RetrievalTitleSelection.PrunePreciseTitleTailSelections("Concombres a la romaine", selected);

        Assert.Equal(2, selected.Count);
        Assert.DoesNotContain(selected, match => string.Equals(match.ChunkId, "weak", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, match => string.Equals(match.ChunkId, "lexical-tail", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, match => string.Equals(match.ChunkId, "seasonal-list", StringComparison.Ordinal));
        Assert.Contains(selected, match => string.Equals(match.ChunkId, "variant", StringComparison.Ordinal));
    }

    [Fact]
    public void PrunePreciseTitleTailSelections_removes_cross_document_lexical_overlap_for_two_word_titles()
    {
        var anchor = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/Sauces.pdf",
            "Sauces.pdf",
            122,
            122,
            "anchor",
            122,
            "SAUCE B\u00c9ARNAISE Ingredients: echalotes, estragon, beurre, jaunes d'oeufs. Preparation: monter la sauce.",
            1,
            "hash-exact",
            "SAUCE B\u00c9ARNAISE Ingredients: echalotes, estragon, beurre, jaunes d'oeufs. Preparation: monter la sauce.",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var lexicalTail = anchor with
        {
            Score = 1.02,
            DocId = "doc-tail",
            DocPath = "Cuisine/Menu.pdf",
            DocName = "Menu.pdf",
            ChunkId = "lexical-tail",
            PageStart = 8,
            PageEnd = 8,
            Text = "Sauce froide au yaourt pour crudites. Variante: ajouter une note d'estragon pour un esprit bearnaise.",
            EmbedText = "Matched profile title: Sauce bechamel\nDocument: Menu.pdf\nContext: Sauce froide au yaourt pour crudites. Variante: ajouter une note d'estragon pour un esprit bearnaise.\nPreviousContext: SAUCE BEARNAISE"
        };
        var sameTitleElsewhere = anchor with
        {
            Score = 0.90,
            DocId = "doc-title",
            DocPath = "Cuisine/SauceVariant.pdf",
            DocName = "SauceVariant.pdf",
            ChunkId = "variant",
            Text = "Sauce bearnaise express: ingredients et preparation rapide.",
            EmbedText = "Sauce bearnaise express: ingredients et preparation rapide."
        };

        var selected = new List<RagMatch> { lexicalTail, anchor, sameTitleElsewhere };

        RetrievalTitleSelection.PrunePreciseTitleTailSelections("sauce bearnaise", selected);

        Assert.Equal(2, selected.Count);
        Assert.DoesNotContain(selected, match => string.Equals(match.ChunkId, "lexical-tail", StringComparison.Ordinal));
        Assert.Contains(selected, match => string.Equals(match.ChunkId, "variant", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeExactTitleCandidateScore_accepts_connector_only_title_gaps()
    {
        var titleCandidate = new RagMatch(
            0.9,
            "doc",
            "Docs/Guide.pdf",
            "Guide.pdf",
            4,
            4,
            "title",
            0,
            "XR 200 fieldbus commissioning\nProcedure and checks.",
            1,
            "hash",
            "XR 200 fieldbus commissioning\nProcedure and checks.",
            "sparse_bm25_v1",
            1,
            1,
            "XR 200 fieldbus commissioning",
            "XR 200 fieldbus commissioning",
            "unit_exact_v1",
            null,
            null,
            null);
        var looseCandidate = titleCandidate with
        {
            ChunkId = "loose",
            Text = "The XR 200 terminal uses several safety checks. A later fieldbus section describes commissioning.",
            EmbedText = "The XR 200 terminal uses several safety checks. A later fieldbus section describes commissioning.",
            SectionTitle = "Overview",
            HeadingPath = "Overview"
        };

        var titleScore = RetrievalCandidateSignals.ComputeExactTitleCandidateScore("XR 200 fieldbus commissioning", titleCandidate);
        var looseScore = RetrievalCandidateSignals.ComputeExactTitleCandidateScore("XR 200 fieldbus commissioning", looseCandidate);

        Assert.True(titleScore > 0);
        Assert.True(titleScore > looseScore);
    }

    [Fact]
    public void Title_pruning_keeps_nearby_linked_context_companion_chunk()
    {
        var anchor = new RagMatch(
            1.02,
            "doc",
            "Cuisine/Recipes.pdf",
            "Recipes.pdf",
            90,
            90,
            "anchor",
            131,
            "Pour 20 churros. Churros avec sauce au chocolat et au piment. Faites frire la pate puis preparez la sauce.",
            1,
            "hash",
            "Matched profile title: Churros avec sauce au chocolat et au piment\nPour 20 churros. Churros avec sauce au chocolat et au piment. Faites frire la pate puis preparez la sauce.",
            "sparse_bm25_v1",
            1,
            1,
            "Churros avec sauce au chocolat et au piment",
            "Churros avec sauce au chocolat et au piment",
            "unit_exact_v1",
            null,
            "linked",
            "linked");
        var linkedCompanion = new RagMatch(
            0.995,
            "doc",
            "Cuisine/Recipes.pdf",
            "Recipes.pdf",
            90,
            92,
            "linked",
            132,
            "INGREDIENTS Pour la pate 25 g de beurre 200 g de farine 50 g de sucre. Pour la sauce au chocolat et au piment 50 g de chocolat noir 150 g de creme liquide.",
            1,
            "hash",
            "INGREDIENTS Pour la pate 25 g de beurre 200 g de farine 50 g de sucre. Pour la sauce au chocolat et au piment 50 g de chocolat noir 150 g de creme liquide.",
            "linked_context_v1",
            1,
            2,
            "Document",
            "Document",
            "section_window_v1",
            "anchor",
            null,
            null);
        var selected = new List<RagMatch> { anchor, linkedCompanion };

        RetrievalTitleSelection.PruneWeakTitleExpansionSelections("Churros sauce chocolat ingredients", selected);
        RetrievalTitleSelection.PruneWeakAdjacentSiblingSelections("Churros sauce chocolat ingredients", selected);
        RetrievalTitleSelection.PrunePreciseTitleTailSelections("Churros sauce chocolat ingredients", selected);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, match => string.Equals(match.ChunkId, "linked", StringComparison.Ordinal));
    }

    [Fact]
    public void Title_pruning_removes_linked_context_with_only_profile_title_hint()
    {
        var anchor = new RagMatch(
            1.02,
            "doc",
            "Operations/Manual.pdf",
            "Manual.pdf",
            6,
            6,
            "anchor",
            60,
            "Gratin dauphinois Ingredients pommes de terre creme ail. Preparation: cuire doucement.",
            1,
            "hash",
            "Matched profile title: Gratin dauphinois\nGratin dauphinois Ingredients pommes de terre creme ail. Preparation: cuire doucement.",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "section_window_v1",
            null,
            "tail",
            "tail");
        var linkedTail = anchor with
        {
            Score = 0.995,
            ChunkId = "tail",
            ChunkIndex = 61,
            PageStart = 7,
            PageEnd = 7,
            Text = "Quant a adapter cette preparation, ajoutez une note personnelle selon les stocks disponibles.",
            EmbedText = "Matched profile title: Gratin dauphinois\nContext: Quant a adapter cette preparation, ajoutez une note personnelle selon les stocks disponibles.",
            EmbeddingBasis = "linked_context_v1",
            SectionTitle = "Document",
            HeadingPath = "Document",
            ChunkType = "section_window_v1",
            PrevChunkId = "anchor",
            NextChunkId = null,
            SameSectionChunkId = null
        };
        var selected = new List<RagMatch> { anchor, linkedTail };

        RetrievalTitleSelection.PruneWeakTitleExpansionSelections("gratin dauphinois", selected);
        RetrievalTitleSelection.PruneWeakAdjacentSiblingSelections("gratin dauphinois", selected);
        RetrievalTitleSelection.PrunePreciseTitleTailSelections("gratin dauphinois", selected);

        var remaining = Assert.Single(selected);
        Assert.Equal("anchor", remaining.ChunkId);
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_prefers_single_strong_quoted_content_hit()
    {
        var hit = TestMatch(
            text: "Gratin dauphinois ingredients potatoes cream garlic. Preparation steps and timing.",
            embedText: "Matched quoted title: Gratin dauphinois\nGratin dauphinois ingredients potatoes cream garlic.",
            docPath: "Cuisine/Top30.pdf",
            chunkId: "quoted",
            score: 0.92);

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Donne-moi \"Gratin dauphinois\".",
            [hit]));
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_keeps_search_open_for_structured_detail_request()
    {
        var hit = TestMatch(
            text: "Gratin dauphinois ingredients potatoes cream garlic. Preparation steps and timing.",
            embedText: "Matched quoted title: Gratin dauphinois\nGratin dauphinois ingredients potatoes cream garlic.",
            docPath: "Cuisine/Top30.pdf",
            chunkId: "quoted",
            score: 0.92);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Tu peux me faire une fiche claire pour \"Gratin dauphinois\" : ingredients, etapes, temps et source ?",
            [hit]));
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_keeps_search_open_for_comparative_or_navigation_hits()
    {
        var hit = TestMatch(
            text: "Quiche lorraine ingredients and preparation.",
            embedText: "Matched quoted title: Quiche lorraine\nQuiche lorraine ingredients and preparation.",
            docPath: "Cuisine/Top30.pdf",
            chunkId: "quoted",
            score: 0.96);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Compare « Quiche lorraine » avec une autre version du corpus.",
            [hit]));

        var navigation = TestMatch(
            text: "Index Gratin dauphinois 7 Quiche lorraine 8 Tarte tatin 9",
            embedText: "Matched quoted title: Gratin dauphinois\nIndex Gratin dauphinois 7 Quiche lorraine 8",
            docPath: "Cuisine/Index.pdf",
            chunkId: "index",
            score: 1.02) with
        {
            ContentRole = "navigation",
            NavigationScore = 0.82,
            ContentDensityScore = 0.2
        };

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Donne-moi « Gratin dauphinois ».",
            [navigation]));
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_keeps_search_open_for_near_tie_across_documents()
    {
        var first = TestMatch(
            text: "Sauce tomate ingredients and preparation.",
            embedText: "Matched quoted title: Sauce tomate\nSauce tomate ingredients and preparation.",
            docPath: "Cuisine/BookA.pdf",
            chunkId: "a",
            score: 0.94);
        var second = TestMatch(
            text: "Sauce tomate another version with preparation.",
            embedText: "Matched quoted title: Sauce tomate\nSauce tomate another version with preparation.",
            docPath: "Cuisine/BookB.pdf",
            chunkId: "b",
            score: 0.92) with
        {
            DocId = "doc-2"
        };

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Donne-moi « Sauce tomate ».",
            [first, second]));
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_keeps_search_open_for_context_only_title_hit()
    {
        var neighbor = TestMatch(
            text: "Ingredients haricots rouges ananas mais poivron tomates. Salade mexicaine preparation et suggestions.",
            embedText: "Matched quoted title: Salade de pates\nIngredients haricots rouges.\nprevious_context: Salade de pates ingredients preparation.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            chunkId: "neighbor",
            score: 0.918);
        var sameDocumentAlternative = TestMatch(
            text: "Salade de lentilles ingredients et preparation.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 27,
            chunkId: "same-doc",
            score: 0.91);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            [neighbor, sameDocumentAlternative]));
    }

    [Fact]
    public void ShouldShortCircuitAfterQuotedTitle_keeps_search_open_for_body_only_exact_phrase()
    {
        var bodyMention = TestMatch(
            text: "Vous pourrez aussi la servir dans une salade de pates froide, seule ou avec un jus de citron.",
            embedText: "Vous pourrez aussi la servir dans une salade de pates froide, seule ou avec un jus de citron.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "body-mention",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.97);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterQuotedTitle(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            [bodyMention]));
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_detects_precise_unquoted_title_without_broad_intent()
    {
        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Saumon avec sauce yaourt-menthe source",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Saumon avec sauce yaourt-menthe",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche « gateau chocolat courgette ».",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Quelles recettes avec du chocolat peux-tu proposer pour la semaine ?",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche des recettes pour organiser les repas de la semaine.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Une procedure enfant.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Quel document faut il citer au client pour parler d inerting et d integration plc ?",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_keeps_precise_title_before_situational_broad_shape()
    {
        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Je veux la creme au citron, avec les parametres robot.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));
    }

    [Theory]
    [InlineData("\"Alpha Beta Procedure\"", true, false, false, false, true)]
    [InlineData("Alpha Beta Procedure source", true, false, false, false, true)]
    [InlineData("Alpha Beta Procedure source", false, false, false, false, false)]
    [InlineData("Alpha Beta Procedure source", true, true, false, false, false)]
    [InlineData("Alpha Beta Procedure source", true, false, true, false, false)]
    [InlineData("Alpha Beta Procedure source", true, false, false, true, false)]
    public void ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors_prefers_indexed_anchor_route_for_category_scoped_precise_titles(
        string query,
        bool hasCategoryScope,
        bool hasDocScope,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback,
        bool expected)
    {
        Assert.Equal(expected, RetrievalNavigation.ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(
            query,
            hasCategoryScope,
            hasDocScope,
            skipChunkRetrieversForDocumentOverview,
            useScopedProfileFallback));
    }

    [Fact]
    public void ShouldShortCircuitAfterTitleAnchorRoute_prefers_single_strong_unquoted_title_route()
    {
        var hit = TestMatch(
            text: "Gateau chocolat courgette. Ingredients chocolat, courgette, farine et oeufs. Preparation: melanger, verser dans le moule, cuire, puis laisser refroidir avant de servir avec la source du document.",
            embedText: "Matched title_anchor_route: Gateau chocolat courgette\nGateau chocolat courgette ingredients and preparation.",
            docPath: "Cuisine/Desserts.pdf",
            chunkId: "route",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.93);

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            [hit]));
    }

    [Fact]
    public void ShouldShortCircuitAfterTitleAnchorRoute_keeps_search_open_for_safety_data_sheet_type_mismatch()
    {
        var genericDataSheet = TestMatch(
            text: "PTFE technical data sheet. Product handling precautions, storage notes, processing recommendations and general instructions are described for ordinary data sheet use.",
            embedText: "Matched title_anchor_route: PTFE Technical Data Sheet\nPTFE technical data sheet handling precautions.",
            docPath: "Docs/FIT-PTFE Technical Data Sheet.pdf",
            chunkId: "generic-data-sheet-route",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.96);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "safety data sheet handling precautions",
            [genericDataSheet]));
    }

    [Fact]
    public void ShouldShortCircuitAfterTitleAnchorRoute_allows_direct_token_route_when_body_is_useful()
    {
        var hit = TestMatch(
            text: "MenuGATEAUCHOCOLAT-COURGETTE30 min 4 Ingredients 150 g de chocolat noir, 4 oeufs, 300 g de courgettes, farine, levure et sel. Preparation: melanger, incorporer les courgettes, enfourner puis verifier la cuisson.",
            embedText: "Matched direct_title_token_route: gateau chocolat courgett\nMenuGATEAUCHOCOLAT-COURGETTE ingredients and preparation.",
            docPath: "Cuisine/Desserts.pdf",
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.80);

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            [hit]));
    }

    [Fact]
    public void ShouldShortCircuitAfterTitleAnchorRoute_rejects_direct_token_route_when_title_is_only_ingredient()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.99);

        Assert.False(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(ingredientMention));
        Assert.True(RetrievalSelection.IsWeakResolvedRouteTarget(ingredientMention));
        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Bouillon de volaille source",
            [ingredientMention]));
    }

    [Fact]
    public void IsWeakResolvedRouteTarget_rejects_subjectless_instruction_fragment_title_even_when_body_has_content()
    {
        var instructionTitle = TestMatch(
            text: "VERIFIEZ LES RACCORDS. Materials: gasket, wrench and pressure gauge. Procedure: isolate the line, inspect every fitting, replace damaged seals, pressure test the assembly and record the result.",
            embedText: "Matched title_anchor_route: Verifiez les raccords\nVERIFIEZ LES RACCORDS. Materials and procedure.",
            docPath: "Technique/Maintenance.pdf",
            chunkId: "instruction-title",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.93,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.95);

        Assert.True(RetrievalSelection.IsWeakResolvedRouteTarget(instructionTitle));
    }

    [Fact]
    public void DirectTitleTokenRouteHasStrongLeadEvidence_accepts_card_title_that_confirms_target()
    {
        var cardBackedHit = TestMatch(
            text: "Grains de poivre. Ingredients: carcasse de volaille, legumes, aromates et eau froide. Preparation: couvrir, mijoter, filtrer puis refroidir.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nGrains de poivre and bouillon sauce content card.",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            chunkId: "card-backed-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.87) with
        {
            MatchedContentCards = [new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES")]
        };

        Assert.True(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(cardBackedHit));
        Assert.False(RetrievalSelection.IsWeakResolvedRouteTarget(cardBackedHit));
    }

    [Fact]
    public void DirectTitleTokenRouteHasStrongLeadEvidence_accepts_complete_title_after_short_lead_in()
    {
        var leadInTitle = TestMatch(
            text: "Un snack parfait en ete : merveilleusement rafraichissant et facile a preparer. Saumon avec sauce yaourt-menthe. Prechauffez le four, melangez la menthe avec le yaourt et posez le saumon en papillote.",
            embedText: "Matched direct_title_token_route: Saumon avec sauce yaourt menthe\nUn snack parfait en ete : merveilleusement rafraichissant et facile a preparer. Saumon avec sauce yaourt-menthe.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            chunkId: "lead-in-title",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.91);

        Assert.True(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(leadInTitle));
        Assert.False(RetrievalSelection.IsWeakResolvedRouteTarget(leadInTitle));
    }

    [Fact]
    public void HasResolvedPreciseTitleSelection_accepts_strong_direct_route_connector_variant()
    {
        var directRoute = TestMatch(
            text: "Menu Realiser la recette avec d'autres pates. NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients 250 g de nouilles chinoises, 400 g de crevettes, sauce soja et huile d'olive. Preparation: cuire, sauter et servir.",
            embedText: "Matched direct_title_token_route: nouilles sautees legumes crevettes\nMenu NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "nouilles-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.96);

        Assert.True(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(directRoute));
        Assert.True(RetrievalSelection.HasResolvedPreciseTitleSelection(
            ["nouilles sautees legumes-crevettes"],
            directRoute));
    }

    [Fact]
    public void BuildReusablePreciseTitleBackfillMatches_reuses_content_safe_title_route_before_search()
    {
        var query = "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.";
        var phrases = new[] { "nouilles sautees legumes-crevettes" };
        var directRoute = TestMatch(
            text: "Menu Realiser la recette avec d'autres pates. NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients 250 g de nouilles chinoises, 400 g de crevettes, sauce soja et huile d'olive. Preparation: cuire, sauter et servir.",
            embedText: "Matched direct_title_token_route: nouilles sautees legumes crevettes\nMenu NOUILLES SAUTEES AUX LEGUMES ET CREVETTES60 min 4 Ingredients.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "nouilles-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.96);
        var sparseNoise = TestMatch(
            text: "Cette page parle de legumes, de crevettes et de pates dans une liste generale.",
            chunkId: "sparse-noise",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var existingSelection = TestMatch(
            text: "Une autre recette mentionne des nouilles et des crevettes sans titre exploitable.",
            chunkId: "existing-selection",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91);

        var reusableRoutes = RetrievalSelection.BuildReusablePreciseTitleRouteMatches([sparseNoise, directRoute]);
        var reusableBackfill = RetrievalSelection.BuildReusablePreciseTitleBackfillMatches(
            query,
            phrases,
            reusableRoutes);

        var match = Assert.Single(reusableBackfill);
        Assert.Equal("nouilles-direct", match.ChunkId);
        Assert.True(RetrievalSelection.HasReusablePreciseTitleBackfillSelection(
            query,
            phrases,
            [existingSelection],
            reusableBackfill));
    }

    [Fact]
    public void BuildReusablePreciseTitleBackfillMatches_rejects_anchor_with_matching_route_title_but_wrong_chunk_text()
    {
        var staleAnchor = TestMatch(
            text: "Menu ESCALOPE DE VOLAILLE AUX CHAMPIGNONS, POMMES DARPHIN. Ingredients: escalopes, champignons, creme et pommes de terre. Preparation: cuire les escalopes puis realiser la sauce.",
            embedText: "Matched title_anchor_route: NOUILLES SAUTEES AUX LEGUMES ET CREVETTES\nMenu ESCALOPE DE VOLAILLE AUX CHAMPIGNONS.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "stale-anchor",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.96);

        var reusableRoutes = RetrievalSelection.BuildReusablePreciseTitleRouteMatches([staleAnchor]);
        var reusableBackfill = RetrievalSelection.BuildReusablePreciseTitleBackfillMatches(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            ["nouilles sautees legumes-crevettes"],
            reusableRoutes);

        Assert.Empty(reusableBackfill);
    }

    [Fact]
    public void BuildReusablePreciseTitleBackfillMatches_rejects_title_only_route_mentions()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, tomates, farine, vin blanc, bouquet garni, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.99);

        var reusableRoutes = RetrievalSelection.BuildReusablePreciseTitleRouteMatches([ingredientMention]);
        var reusableBackfill = RetrievalSelection.BuildReusablePreciseTitleBackfillMatches(
            "Donne-moi le bouillon de volaille.",
            ["bouillon de volaille"],
            reusableRoutes);

        Assert.Empty(reusableBackfill);
        Assert.False(RetrievalSelection.HasReusablePreciseTitleBackfillSelection(
            "Donne-moi le bouillon de volaille.",
            ["bouillon de volaille"],
            [],
            reusableBackfill));
    }

    [Fact]
    public void DirectTitleTokenRouteHasStrongLeadEvidence_rejects_prose_mentions_without_title_boundary()
    {
        var proseMention = TestMatch(
            text: "This section mentions Alpha Beta Procedure while comparing several maintenance topics and related warnings.",
            embedText: "Matched direct_title_token_route: Alpha Beta Procedure\nThis section mentions Alpha Beta Procedure.",
            docPath: "Docs/manual.pdf",
            chunkId: "prose-mention",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.92);

        Assert.False(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(proseMention));
        Assert.True(RetrievalSelection.IsWeakResolvedRouteTarget(proseMention));
    }

    [Fact]
    public void ShouldShortCircuitAfterTitleAnchorRoute_keeps_search_open_for_ambiguous_or_navigation_route()
    {
        var first = TestMatch(
            text: "Sauce tomate classique. Ingredients tomates, ail, oignon et herbes. Preparation detaillee avec cuisson lente et source documentaire pour distinguer cette version.",
            embedText: "Matched title_anchor_route: Sauce tomate\nSauce tomate classique ingredients and preparation.",
            docPath: "Cuisine/BookA.pdf",
            chunkId: "a",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.94);
        var second = TestMatch(
            text: "Sauce tomate rapide. Ingredients tomates, huile et basilic. Preparation courte avec une autre version clairement issue d'un document distinct.",
            embedText: "Matched title_anchor_route: Sauce tomate\nSauce tomate rapide ingredients and preparation.",
            docPath: "Cuisine/BookB.pdf",
            chunkId: "b",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.92) with
        {
            DocId = "doc-2"
        };

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche la sauce tomate.",
            [first, second]));

        var navigation = TestMatch(
            text: "Index Gateau chocolat courgette 12 Gateau citron 13 Gateau vanille 14",
            embedText: "Matched title_anchor_route: Gateau chocolat courgette\nIndex Gateau chocolat courgette 12 Gateau citron 13",
            docPath: "Cuisine/Index.pdf",
            chunkId: "index",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.98) with
        {
            ContentRole = "navigation",
            NavigationScore = 0.86,
            ContentDensityScore = 0.18
        };

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            [navigation]));

        var borderlineNavigation = TestMatch(
            text: "Les recettes Gateau chocolat courgette 23 Fondant chocolat 45 Entremets chocolat 47 autres entrees de sommaire et de liste.",
            embedText: "Matched direct_title_token_route: gateau chocolat courgett\nLes recettes Gateau chocolat courgette 23 Fondant chocolat 45.",
            docPath: "Cuisine/Table.pdf",
            chunkId: "toc-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.99) with
        {
            NavigationScore = 0.76,
            ContentDensityScore = 0.50
        };

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            [borderlineNavigation]));
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_removes_neighbor_when_primary_token_is_absent()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "Gratin suisse Ingredients pain de mie emmenthal jambon lait oeufs.",
                embedText: "Matched profile title: Gratin suisse\nContext: Gratin suisse Ingredients pain de mie emmenthal jambon lait oeufs.\nPreviousContext: Oeufs gratines avec du fromage a raclette.")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("raclette suisse", selected);

        Assert.Empty(selected);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_cross_document_source_selection_candidates()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "Inerting guidance covers oxygen monitoring and explosion prevention.",
                docPath: "ATEX/EN-15281.pdf",
                chunkId: "inerting"),
            TestMatch(
                text: "IND570 manual explains PLC integration and PROFINET.",
                docPath: "Controls/IND570.pdf",
                chunkId: "plc")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections(
            "Quel document faut il citer au client pour parler d inerting et d integration plc ?",
            selected);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_primary_title_match_and_drops_unanchored_tail()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "Gratin dauphinois Ingredients pommes de terre creme ail cuisson.",
                embedText: "Matched profile title: Gratin dauphinois\nGratin dauphinois Ingredients pommes de terre creme ail cuisson."),
            TestMatch(
                text: "Osso buco Pour 4 personnes jarret de veau tomates bouillon.",
                embedText: "Osso buco Pour 4 personnes jarret de veau tomates bouillon.",
                page: 8,
                chunkId: "chunk-tail")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("gratin dauphinois", selected);

        var remaining = Assert.Single(selected);
        Assert.Contains("dauphinois", remaining.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_minor_typo_title_match()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "Mixing valve Components pressure temperature calibration.",
                embedText: "Matched title_anchor_route: Mixing Valve\nMixing valve Components pressure temperature calibration."),
            TestMatch(
                text: "Safety valve unrelated pressure relief notes.",
                embedText: "Safety valve unrelated pressure relief notes.",
                page: 8,
                chunkId: "chunk-tail")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("fiche mixxing valve", selected);

        var remaining = Assert.Single(selected);
        Assert.Contains("Mixing valve", remaining.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_exact_short_recipe_title_tokens()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "One pot pasta with turkey and bacon. Ingredients pasta broccoli turkey bacon.",
                embedText: "Matched title_anchor_route: DINDE ET BACON\nOne pot pasta with turkey and bacon. Ingredients pasta broccoli turkey bacon."),
            TestMatch(
                text: "Broccoli noodles with sesame sauce and minced meat.",
                embedText: "Broccoli noodles with sesame sauce and minced meat.",
                page: 8,
                chunkId: "chunk-tail")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("one pot pasta brocoli dinde bacon", selected);

        Assert.Contains(selected, match => match.Text?.Contains("bacon", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_keeps_numeric_title_anchor()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "SAUCE AUX 4 FROMAGES Temps total 10 min creme fromages preparation.",
                embedText: "Matched profile title: SAUCE AUX 4 FROMAGES\nSAUCE AUX 4 FROMAGES Temps total 10 min creme fromages preparation."),
            TestMatch(
                text: "Sauce tomate fromages et creme, sans titre numerique.",
                embedText: "Sauce tomate fromages et creme.",
                page: 8,
                chunkId: "chunk-tail")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("SAUCE AUX 4 FROMAGES", selected);

        var remaining = Assert.Single(selected);
        Assert.Contains("4 FROMAGES", remaining.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PruneUnmatchedPreciseTitleSelections_does_not_apply_to_open_advice_queries()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "Sauce moutarde Ingredients moutarde vinaigre huile sel poivre.",
                embedText: "Sauce moutarde Ingredients moutarde vinaigre huile sel poivre.")
        };

        RetrievalTitleSelection.PruneUnmatchedPreciseTitleSelections("quelle sauce irait bien avec entrecote", selected);

        Assert.Single(selected);
    }

    [Fact]
    public void ComputeExactTitleCandidateScore_prefers_chunk_text_that_contains_requested_title()
    {
        var fullRecipe = TestMatch(
            text: "7Gratin dauphinoisPour 4 personnes Ingredients pommes de terre lait creme ail noix de muscade.",
            embedText: "Matched profile title: Gratin dauphinois\nContext: 7Gratin dauphinoisPour 4 personnes Ingredients pommes de terre lait creme ail noix de muscade.",
            page: 6,
            chunkId: "full-recipe",
            chunkType: "section_window_v1");
        var recipeTail = TestMatch(
            text: "Quant a twister la recette, il est autorise d'ajouter une touche personnelle au plat.",
            embedText: "Matched profile title: Gratin dauphinois\nContext: Quant a twister la recette, il est autorise d'ajouter une touche personnelle au plat.",
            page: 7,
            chunkId: "recipe-tail",
            chunkType: "unit_exact_v1");

        var fullRecipeScore = RetrievalCandidateSignals.ComputeExactTitleCandidateScore("gratin dauphinois", fullRecipe);
        var recipeTailScore = RetrievalCandidateSignals.ComputeExactTitleCandidateScore("gratin dauphinois", recipeTail);

        Assert.True(fullRecipeScore > recipeTailScore);
    }

    [Fact]
    public void PrioritizeExactTitleSelections_prefers_direct_chunk_title_over_title_hinted_tail()
    {
        var recipeTail = TestMatch(
            text: "Quant a twister la recette, il est autorise d'ajouter une touche personnelle au plat.",
            embedText: "Matched profile title: Gratin dauphinois\nContext: Quant a twister la recette, il est autorise d'ajouter une touche personnelle au plat.",
            page: 7,
            chunkId: "recipe-tail",
            chunkType: "unit_exact_v1");
        var fullRecipe = TestMatch(
            text: "Preparation 20 minutes. 7Gratin dauphinoisPour 4 personnes Ingredients pommes de terre lait creme ail.",
            embedText: "Matched profile title: Gratin dauphinois\nContext: Preparation 20 minutes. 7Gratin dauphinoisPour 4 personnes Ingredients pommes de terre lait creme ail.",
            page: 6,
            chunkId: "full-recipe",
            chunkType: "section_window_v1");
        var selected = new List<RagMatch> { recipeTail, fullRecipe };

        RetrievalTitleSelection.PrioritizeExactTitleSelections("gratin dauphinois", selected);

        Assert.Equal("full-recipe", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeExactTitleSelections_prefers_exact_phrase_over_loose_token_overlap()
    {
        var looseOverlap = TestMatch(
            text: "The alpha subsystem references a separate beta appendix without a combined title.",
            embedText: "The alpha subsystem references a separate beta appendix without a combined title.",
            page: 2,
            score: 1.02,
            chunkId: "loose-overlap",
            chunkType: "section_window_v1");
        var exactPhrase = TestMatch(
            text: "Procedure summary. ALPHA BETA setup requires a clean initialization.",
            embedText: "Procedure summary. ALPHA BETA setup requires a clean initialization.",
            page: 3,
            score: 0.91,
            chunkId: "exact-phrase",
            chunkType: "unit_exact_v1");
        var selected = new List<RagMatch> { looseOverlap, exactPhrase };

        RetrievalTitleSelection.PrioritizeExactTitleSelections("alpha beta", selected);

        Assert.Equal("exact-phrase", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeExactTitleSelections_prefers_dense_content_when_title_signals_tie()
    {
        var titleList = TestMatch(
            text: "Alpha Beta 12 Gamma Delta 18 Safety Reset 24 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            embedText: "Alpha Beta 12 Gamma Delta 18 Safety Reset 24 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            page: 2,
            score: 1.02,
            chunkId: "title-list",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.20
        };
        var actionablePage = TestMatch(
            text: "Alpha Beta. Materials: lock padlock warning tag. Procedure: 1. Isolate the machine. 2. Verify zero energy. 3. Record the result.",
            embedText: "Alpha Beta. Materials: lock padlock warning tag. Procedure: 1. Isolate the machine. 2. Verify zero energy. 3. Record the result.",
            page: 12,
            score: 0.98,
            chunkId: "actionable-page",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { titleList, actionablePage };

        RetrievalTitleSelection.PrioritizeExactTitleSelections("alpha beta", selected);

        Assert.Equal("actionable-page", selected[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.ComputeContentEvidencePriority(actionablePage)
            > RetrievalCandidateSignals.ComputeContentEvidencePriority(titleList));
    }

    [Fact]
    public void PrioritizeExactTitleSelections_prefers_trusted_route_over_card_only_header()
    {
        var cardOnlyHeader = TestMatch(
            text: "Controls Device modes Preparation categories For 4 items",
            embedText: "Controls Device modes Preparation categories For 4 items",
            page: 12,
            score: 1.02,
            chunkId: "card-only-header",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 1.0,
            MatchedContentCards = [new RagMatchedContentCard("Alpha Beta Procedure")]
        };
        var routedContent = TestMatch(
            text: "Preparation. Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            embedText: "Matched title_anchor_route: Alpha Beta Procedure\nPreparation. Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            page: 13,
            score: 0.91,
            chunkId: "routed-content",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.86
        };
        var selected = new List<RagMatch> { cardOnlyHeader, routedContent };

        RetrievalTitleSelection.PrioritizeExactTitleSelections("Alpha Beta Procedure", selected);

        Assert.Equal("routed-content", selected[0].ChunkId);
    }

    [Fact]
    public void ComputeExactTitleCandidateScore_uses_title_anchor_route_label()
    {
        var routeMatch = TestMatch(
            text: "The target page contains instructions but does not repeat the heading.",
            embedText: "Matched title_anchor_route: Beta Checklist\nThe target page contains instructions but does not repeat the heading.",
            chunkId: "route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.86);
        var looseOverlap = TestMatch(
            text: "The beta subsystem references a separate checklist appendix.",
            embedText: "The beta subsystem references a separate checklist appendix.",
            chunkId: "loose",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "section_window_v1",
            score: 0.94);

        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(routeMatch));
        Assert.True(RetrievalCandidateSignals.ComputeExactTitleCandidateScore("beta checklist", routeMatch)
            > RetrievalCandidateSignals.ComputeExactTitleCandidateScore("beta checklist", looseOverlap));
    }

    [Fact]
    public void ComputeExactTitleCandidateScore_ignores_unconfirmed_navigation_route_label()
    {
        var unconfirmedNavigationRoute = TestMatch(
            text: "This page has operational details but the requested title is only present in the source navigation.",
            embedText: "Matched navigation_route: Alpha Beta\nThis page has operational details but the requested title is only present in the source navigation.",
            chunkId: "navigation-route",
            embeddingBasis: "navigation_route_v1",
            chunkType: "section_window_v1",
            score: 0.86);
        var confirmedNavigationRoute = unconfirmedNavigationRoute with
        {
            ChunkId = "confirmed-navigation-route",
            Text = "Alpha Beta. This page has operational details.",
            EmbedText = "Matched navigation_route: Alpha Beta\nAlpha Beta. This page has operational details."
        };

        Assert.False(RetrievalCandidateSignals.HasProfileTitleHint(unconfirmedNavigationRoute));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(confirmedNavigationRoute));
        Assert.Equal(0.0, RetrievalCandidateSignals.ComputeExactTitleCandidateScore("alpha beta", unconfirmedNavigationRoute));
        Assert.True(RetrievalCandidateSignals.ComputeExactTitleCandidateScore("alpha beta", confirmedNavigationRoute) > 0.0);
    }

    [Fact]
    public void HasProfileTitleHint_ignores_direct_title_route_without_local_title_evidence()
    {
        var unconfirmedDirectRoute = TestMatch(
            text: "The alpha subsystem references beta as a separate appendix, but this is not the combined target.",
            embedText: "Matched direct_title_token_route: Alpha Beta\nThe alpha subsystem references beta as a separate appendix.",
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.88);
        var confirmedDirectRoute = unconfirmedDirectRoute with
        {
            ChunkId = "confirmed-direct-route",
            Text = "Alpha Beta. Materials: lock padlock warning tag. Procedure: isolate, verify, record.",
            EmbedText = "Matched direct_title_token_route: Alpha Beta\nAlpha Beta. Materials: lock padlock warning tag. Procedure: isolate, verify, record."
        };
        var stemmedDirectRoute = unconfirmedDirectRoute with
        {
            ChunkId = "stemmed-direct-route",
            Text = "Gateau chocolat courgette. Materials: chocolate, courgette and flour. Procedure: mix and bake.",
            EmbedText = "Matched direct_title_token_route: gateau chocolat courgett\nGateau chocolat courgette. Materials: chocolate, courgette and flour. Procedure: mix and bake."
        };

        Assert.False(RetrievalCandidateSignals.HasProfileTitleHint(unconfirmedDirectRoute));
        Assert.False(RetrievalSelection.IsResolvedTitleOrNavigationRoute(unconfirmedDirectRoute));
        Assert.False(RetrievalSelection.HasResolvedPreciseTitleSelection(["Alpha Beta"], unconfirmedDirectRoute));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(confirmedDirectRoute));
        Assert.True(RetrievalCandidateSignals.DirectTitleTokenRouteHasTargetTitleEvidence(confirmedDirectRoute));
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(confirmedDirectRoute));
        Assert.True(RetrievalSelection.HasResolvedPreciseTitleSelection(["Alpha Beta"], confirmedDirectRoute));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(stemmedDirectRoute));
        Assert.True(RetrievalCandidateSignals.DirectTitleTokenRouteHasTargetTitleEvidence(stemmedDirectRoute));
        Assert.True(RetrievalSelection.HasResolvedPreciseTitleSelection(["gateau chocolat courgett"], stemmedDirectRoute));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    public void ResolveDirectTitleTokenRouteMinimumOverlap_requires_strong_title_coverage(
        int tokenCount,
        int expected)
    {
        Assert.Equal(expected, RetrievalCandidateSignals.ResolveDirectTitleTokenRouteMinimumOverlap(tokenCount));
    }

    [Fact]
    public void PrioritizeExactTitleSelections_does_not_promote_weak_direct_title_route()
    {
        var weakDirectRoute = TestMatch(
            text: "Temps total : 30 min. Filets de truite saumonee avec une sauce au yaourt, citron, herbes et menthe. Preparation: cuire le poisson puis servir avec la sauce.",
            embedText: "Matched direct_title_token_route: Saumon avec sauce yaourt menthe\nTemps total : 30 min. Filets de truite saumonee avec une sauce au yaourt, citron, herbes et menthe.",
            docPath: "Cuisine/Moulinex.pdf",
            chunkId: "weak-direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.92);
        var exactTitleChunk = TestMatch(
            text: "Un snack parfait en ete. Saumon avec sauce yaourt-menthe. Prechauffez le four, melangez la menthe avec le yaourt, posez le saumon en papillote puis servez avec la sauce.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            chunkId: "exact-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90);
        var selected = new List<RagMatch> { weakDirectRoute, exactTitleChunk };

        RetrievalTitleSelection.PrioritizeExactTitleSelections("Saumon avec sauce yaourt-menthe", selected);

        Assert.False(RetrievalCandidateSignals.DirectTitleTokenRouteHasStrongLeadEvidence(weakDirectRoute));
        Assert.Equal("exact-title", selected[0].ChunkId);
    }

    [Fact]
    public void ShouldUseExplicitDocumentScopedBackfillRoute_detects_file_scoped_sourcing_query()
    {
        const string query = "Donne une reponse tres courte sur `conditions generales de fourniture EDP : commande, livraison, prix, paiement, responsabilite, resiliation` a partir de `EDP_Group_Portugal_ENG_General_Conditions_for_the_Supply_of_Goods_and_Services_Sept25.pdf`, mais avec une citation exploitable et une phrase indiquant la limite de la preuve.";
        var scopedQuery = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            query,
            "conditions generales de fourniture EDP commande livraison prix paiement responsabilite resiliation");

        Assert.False(string.IsNullOrWhiteSpace(scopedQuery));
        Assert.True(RetrievalQueryPolicy.ShouldUseExplicitDocumentScopedBackfillRoute(query, scopedQuery));
        var extensionlessScopedQuery = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            "retrouver annexes appendices");
        Assert.True(RetrievalQueryPolicy.ShouldUseExplicitDocumentScopedBackfillRoute(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            extensionlessScopedQuery));
        Assert.False(RetrievalQueryPolicy.ShouldUseExplicitDocumentScopedBackfillRoute(
            "Donne-moi la procedure Alpha Beta du guide Acme.",
            "Alpha Beta"));
    }

    [Fact]
    public void PruneQualifiedQuotedTitleCompetitorDocuments_removes_qualified_prefix_after_near_exact_structured_match()
    {
        const string query = "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?";
        var targetTitle = TestMatch(
            text: "SAUCE BOLOGNANE Accompagnements Viandes Boeuf Modes de preparation Frire Cuire Categories de recettes Pour 4 portions SAUCE BOLOGNANE",
            docPath: "Cuisine/target.pdf",
            page: 27,
            chunkId: "target-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918) with
        {
            ChunkIndex = 33,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.545
        };
        var targetContinuation = TestMatch(
            text: "P PREPARATION 1 INGREDIENTS : 1 oignon 2 gousses d'ail 1 carotte 300 g de viande de boeuf hachee. Preparation 1. Eplucher et hacher l'ail et l'oignon. 2. Faire chauffer la poele. Laisser mijoter env. 30 minutes.",
            docPath: "Cuisine/target.pdf",
            page: 28,
            chunkId: "target-continuation",
            embeddingBasis: "linked_context_v1",
            score: 0.8955) with
        {
            ChunkIndex = 5,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.895,
            SameSectionChunkId = "target-title"
        };
        var qualifiedPrefix = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients : soja texture, tomates, carottes. Preparation : hydrater le soja puis mijoter.",
            docPath: "Cuisine/qualified.pdf",
            page: 21,
            chunkId: "qualified-title",
            embeddingBasis: "local_title_token_route_v1",
            score: 1.02) with
        {
            ChunkIndex = 12,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.80,
            SectionTitle = "SAUCE BOLOGNAISE",
            HeadingPath = "SAUCE BOLOGNAISE"
        };
        var selected = new List<RagMatch> { targetTitle, targetContinuation, qualifiedPrefix };

        RetrievalTitleSelection.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);

        Assert.Contains(selected, match => match.ChunkId == "target-title");
        Assert.Contains(selected, match => match.ChunkId == "target-continuation");
        Assert.DoesNotContain(selected, match => string.Equals(match.DocPath, "Cuisine/qualified.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PruneQualifiedQuotedTitleCompetitorDocuments_keeps_qualified_prefix_without_near_exact_document()
    {
        const string query = "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?";
        var qualifiedPrefix = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients : soja texture, tomates, carottes. Preparation : hydrater le soja puis mijoter.",
            docPath: "Cuisine/qualified.pdf",
            page: 21,
            chunkId: "qualified-title",
            embeddingBasis: "local_title_token_route_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.80
        };
        var selected = new List<RagMatch> { qualifiedPrefix };

        RetrievalTitleSelection.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);

        Assert.Single(selected);
        Assert.Equal("qualified-title", selected[0].ChunkId);
    }

    [Fact]
    public void TitleAnchorRouteHasTargetTitleEvidence_accepts_only_exact_same_page_provenance_without_repeated_title()
    {
        var ordinaryAnchor = TestMatch(
            text: "Operating body and actionable steps without the heading repeated.",
            embedText: "Matched title_anchor_route: Alpha Beta Control Matrix\nOperating body and actionable steps without the heading repeated.",
            page: 2,
            chunkId: "ordinary-anchor",
            embeddingBasis: "title_anchor_route_v1");
        var exactPageAnchor = ordinaryAnchor with
        {
            ChunkId = "exact-page-anchor",
            EmbedText = "Matched title_anchor_exact_page_route: Alpha Beta Control Matrix\nOperating body and actionable steps without the heading repeated."
        };

        Assert.False(RetrievalCandidateSignals.TitleAnchorRouteHasTargetTitleEvidence(ordinaryAnchor));
        Assert.True(RetrievalCandidateSignals.TitleAnchorRouteHasTargetTitleEvidence(exactPageAnchor));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(exactPageAnchor));
    }

    [Fact]
    public void SelectStructuredContinuationAnchors_prefers_linked_mid_procedure_anchor_when_limited()
    {
        var titleId = Guid.NewGuid().ToString();
        var previousId = Guid.NewGuid().ToString();
        var continuationId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Alpha Beta\" : materials, etapes, temps et source ?";
        var directTitle = TestMatch(
            text: "Alpha Beta Ingredients: 250 g alpha, 100 ml beta, salt and pepper.",
            docPath: "Docs/alpha-guide.pdf",
            page: 4,
            chunkId: titleId,
            embeddingBasis: "local_title_token_route_v1",
            score: 1.02) with
        {
            ChunkIndex = 3,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var linkedContinuation = TestMatch(
            text: "Procedez de la meme facon avec les ingredients restants. 3. Recouvrez hermetiquement et laissez reposer pendant 5 heures.",
            docPath: "Docs/alpha-guide.pdf",
            page: 4,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.88,
            prevChunkId: previousId) with
        {
            ChunkIndex = 5,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };

        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(
            query,
            [directTitle, linkedContinuation],
            limit: 1);

        var anchor = Assert.Single(anchors);
        Assert.Equal(continuationId, anchor.ChunkId);
    }

    [Fact]
    public void PrunePreciseTitleLocalNeighborhoodSelections_removes_same_page_non_additive_neighbors_after_final_title_backfill()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "SAUCE AU POIVRE\n\n226227 Temps total : 12 min Temps total : 17 min 1 c. a c. de poivre concasse 1 cl de cognac 10 cl de creme liquide 1 c. a c. de fond de veau 1 c. a c. de farine 15 cl d'eau 1 Dans le robot muni du batteur, mettez le poivre, le cognac, la creme liquide, le fond de veau et la farine.",
            docPath: "Cuisine/moulinex.pdf",
            page: 121,
            chunkId: "anchor",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "footer_titled_item_window_v1",
            score: 0.918) with
        {
            ChunkIndex = 808,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.86
        };
        var samePageLinkedNoise = TestMatch(
            text: "INGREDIENTS voisins 50 g de pecorino 50 g de comte. Otez la croute des fromages. Dans 3 cl de cognac mettez le poivre, le cognac et la creme.",
            docPath: "Cuisine/moulinex.pdf",
            page: 121,
            chunkId: "same-page-linked-noise",
            embeddingBasis: "linked_context_v1",
            score: 0.8775) with
        {
            ChunkIndex = 807,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var samePageMixedTitle = TestMatch(
            text: "SAUCE AU POIVRE 50 g de parmesan 50 g de pecorino 50 g de comte 50 g de gorgonzola 2 jaunes d'oeufs 30 cl de creme liquide. SAUCE AUX 4 FROMAGES.",
            docPath: "Cuisine/moulinex.pdf",
            page: 121,
            chunkId: "same-page-mixed-title",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ChunkIndex = 498,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var otherDoc = TestMatch(
            text: "Sauce au poivre vert. Ingredients creme poivre vert cognac. Preparation: melanger et servir chaud.",
            docPath: "Cuisine/generic-sauces.pdf",
            page: 12,
            chunkId: "other-doc",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { anchor, samePageLinkedNoise, samePageMixedTitle, otherDoc };

        RetrievalTitleSelection.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "same-page-linked-noise");
        Assert.DoesNotContain(selected, match => match.ChunkId == "same-page-mixed-title");
        Assert.Contains(selected, match => match.ChunkId == "anchor");
        Assert.Contains(selected, match => match.ChunkId == "other-doc");
    }

    [Fact]
    public void PrunePreciseTitleLocalNeighborhoodSelections_keeps_same_page_companion_with_missing_requested_facet()
    {
        var anchorId = Guid.NewGuid().ToString();
        var stepsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Alpha Beta\" : ingredients, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "ALPHA BETA Temps total : 25 min INGREDIENTS 200 g alpha 50 ml beta sel et poivre.",
            docPath: "Docs/alpha.pdf",
            page: 4,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "footer_titled_item_window_v1",
            score: 0.918) with
        {
            ChunkIndex = 10,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.86,
            NextChunkId = stepsId
        };
        var steps = TestMatch(
            text: "PREPARATION 1 Ajouter alpha dans le bol. 2 Verser beta et melanger. 3 Cuire puis laisser reposer.",
            docPath: "Docs/alpha.pdf",
            page: 4,
            chunkId: stepsId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 11,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            PrevChunkId = anchorId
        };
        var selected = new List<RagMatch> { anchor, steps };

        RetrievalTitleSelection.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(stepsId, second.ChunkId));
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_detects_instructional_recipe_titles()
    {
        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Explique-moi les churros sauce chocolat au Companion.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RetrievalNavigation.ShouldProbeUnquotedTitleAnchorRoute(
            "Donne-moi les nouilles sautées légumes-crevettes du PDF santé travail.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));
    }
}
