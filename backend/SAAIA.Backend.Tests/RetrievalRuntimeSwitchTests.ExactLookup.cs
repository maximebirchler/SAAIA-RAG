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
    public void ExtractFocusedLookupPhrases_keeps_full_context_after_trimmed_title_variant()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Explique-moi les churros sauce chocolat au Companion.");

        Assert.Collection(
            phrases.Take(2),
            first => Assert.Equal("churros sauce chocolat", first),
            second => Assert.Equal("churros sauce chocolat au companion", second));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_stops_accented_direct_object_before_structured_facets()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Il me faut la tartiflette, ingrédients + étapes en version claire.");

        Assert.Contains("tartiflette", phrases);
    }

    [Theory]
    [InlineData("Compare les deux quiches lorraines du corpus : differences ingredients, methode et style.", "quiches lorraines")]
    [InlineData("Compare les deux quiches lorraines du corpus : diff\u00e9rences d\u2019ingr\u00e9dients, m\u00e9thode et style.", "quiches lorraines")]
    [InlineData("Il y a plusieurs cremes brulees ? Compare-les si oui.", "cremes brulees")]
    [InlineData("Compare les sauces tomate des deux documents.", "sauces tomate")]
    [InlineData("Compare les clafoutis et dis ce qui change.", "clafoutis")]
    [InlineData("Si les deux documents ne disent pas exactement la meme chose sur approche americaine FAR vs Banque mondiale pour la mise en concurrence et l evaluation, comment expliquer la nuance ?", "approche americaine far vs banque mondiale")]
    public void ExtractComparativeLookupPhrases_extracts_subject_without_corpus_noise(string query, string expected)
    {
        var phrases = RetrievalCandidateSignals.ExtractComparativeLookupPhrases(query);

        Assert.Contains(expected, phrases);
    }

    [Fact]
    public void ExtractComparativeVersionOperandLookupTerms_reads_generic_revision_labels()
    {
        var terms = RetrievalQueryPolicy.ExtractComparativeVersionOperandLookupTerms(
            "Compare les controles RMF r1 vs r2, puis les controles r4 vs r5.");

        Assert.Contains("r1", terms);
        Assert.Contains("r2", terms);
        Assert.Contains("r4", terms);
        Assert.Contains("r5", terms);
        Assert.DoesNotContain("15", terms);
    }

    [Fact]
    public void ExtractComparativeVersionOperandLookupTerms_recovers_version_label_from_mojibake_surface()
    {
        var terms = RetrievalQueryPolicy.ExtractComparativeVersionOperandLookupTerms(
            "objectif 1,5Â°C vs synthÃ¨se AR6");

        Assert.Contains("ar6", terms);
        Assert.DoesNotContain("5a", terms);
    }

    [Fact]
    public void ExtractDocumentStatusOperandLookupTerms_adds_bare_vs_version_operands()
    {
        var terms = RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Compare climat AR5 vs AR6 et explique la difference.");

        Assert.Contains("ar5", terms);
        Assert.Contains("ar6", terms);
        Assert.True(RetrievalLexicalExpansion.ShouldMergeDocumentStatusOperandSelectionsForComparativeVersionRoute(
            "Compare climat AR5 vs AR6 et explique la difference.",
            terms));
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForComparativeLookup_detects_precise_comparison_subject()
    {
        Assert.True(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les deux quiches lorraines du corpus : differences ingredients, methode et style."));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les styles de trois procedures presentes."));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare trois procedures pour apprentis : materiel, risques et consignes."));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare trois procedures salees pour enfants : temps, materiel, risque de ratage."));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Si les deux documents ne disent pas exactement la meme chose sur approche americaine FAR vs Banque mondiale pour la mise en concurrence et l evaluation, comment expliquer la nuance ?"));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?"));
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Quelles options sont les plus adaptees pour un dejeuner de semaine rapide ?"));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_reads_french_guillemet_title_in_larger_request()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Asperges vertes au miel \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("asperges vertes au miel", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_stops_card_title_before_spaced_colon_facets()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?");

        Assert.Contains("patatas bravas", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains("ingredients", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_ignores_broad_synthesis_response_shape_tail()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Fais un d\u00eener international avec Scandinavie, Espagne, Italie et Autriche, et explique l\u2019encha\u00eenement.");

        Assert.DoesNotContain("enchainement", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_reads_predicate_object_questions()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Quelle recette utilise du miso blanc ?");

        Assert.Contains("miso blanc", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_prefers_parameter_target_after_for()
    {
        var phrases = RetrievalCandidateSignals.ExtractFocusedLookupPhrases(
            "Quels reglages de rotissage pour le rumsteck aux oignons grilles ?");

        Assert.Contains("rumsteck aux oignons grilles", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPrimaryHeadTokens_prefers_entity_head_over_broad_qualifier()
    {
        var tokens = RetrievalTitleSelection.ExtractFocusedLookupPrimaryHeadTokens(
            "Je cherche la fondue au chocolat pour un groupe d'enfants.");

        Assert.Contains("fondue", tokens);
        Assert.DoesNotContain("chocolat", tokens);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_removes_predicate_false_positives()
    {
        var tomatoPaste = TestMatch(
            text: "SAUCE TOMATE DE BASE Ingredients: huile, oignon, ail, pate de tomates et vin blanc.",
            page: 56,
            chunkId: "tomato-paste");
        var misoPaste = TestMatch(
            text: "SAUCE AUX SHIMEJI ET AUX ENOKI Ingredients: champignons, amandes et une cuillere a soupe de pate de miso.",
            page: 32,
            chunkId: "miso-paste");
        var selected = new List<RagMatch> { tomatoPaste, misoPaste };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(
            "Quelle recette utilise du miso blanc ?",
            selected);

        var kept = Assert.Single(selected);
        Assert.Equal("miso-paste", kept.ChunkId);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_resolved_route_title_when_body_lost_title()
    {
        var resolvedRoute = TestMatch(
            text: "P PREPARATION 1 INGREDIENTS : 500 g de pommes de terre 300 ml d'huile vegetale Sel. Faire frire env. 20 minutes.",
            embedText: "Matched title_anchor_route: Patatas Bravas\nP PREPARATION 1 INGREDIENTS : 500 g de pommes de terre.",
            docPath: "Cuisine/neff.pdf",
            page: 24,
            chunkId: "patatas-route",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.918);
        var selected = new List<RagMatch> { resolvedRoute };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(
            "Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?",
            selected);

        Assert.Single(selected);
        Assert.Equal("patatas-route", selected[0].ChunkId);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_uses_associated_topic_instead_of_table_facets()
    {
        const string query = "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a conditions de paiement.";
        var paymentTerms = TestMatch(
            text: "Payment terms and conditions define invoices, payment deadlines, acceptance and contractual remedies.",
            docPath: "Achats/Test.pdf",
            page: 5,
            chunkId: "payment-terms");
        var selected = new List<RagMatch> { paymentTerms };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["payment-terms"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_backtick_associated_topic_matches()
    {
        const string query = "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a `conditions de paiement`.";
        var paymentTerms = TestMatch(
            text: "PAYMENT TERMS: invoices, payment period, corrected invoices, acceptance and contractual conditions.",
            docPath: "Generic/Terms.pdf",
            page: 5,
            chunkId: "payment-terms");
        var selected = new List<RagMatch> { paymentTerms };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["payment-terms"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_ignores_broad_scoped_synthesis_questions()
    {
        Assert.Empty(RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé."));
        Assert.Empty(RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Trouve deux réunions où le diagnostic semble évoluer ou se nuancer. Explique si c’est une contradiction réelle ou seulement un changement de contexte."));
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_preserves_broad_scoped_profiles()
    {
        var selections = new List<RagMatch>
        {
            TestMatch(
                "Document FOMC_Minutes_2024_09_18.pdf. Early excerpts mention policy expectations and economic growth.",
                docPath: "Emails - Réunion/PDF/FOMC_Minutes_2024_09_18.pdf",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile")
        };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé.",
            selections);

        Assert.Single(selections);
    }

    [Fact]
    public void ExtractDocumentStatusOperandLookupTerms_detects_corrections_and_amendments()
    {
        Assert.Contains("berichtigung", RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Le Berichtigung contient-il tout le contenu de la norme complete ?"));
        Assert.Contains("ac", RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Peux-tu appliquer un AC tout seul sans citer la norme principale ?"));
        Assert.Contains("ac", RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Comment signaler qu'il existe un corrigendum ?"));
        var amendmentTerms = RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Le prA1 remplace-t-il automatiquement le document 2008+A1 ?");
        Assert.Contains("pra1", amendmentTerms);
        Assert.Contains("a1", amendmentTerms);
        var baselineTerms = RetrievalLexicalExpansion.ExtractDocumentStatusOperandLookupTerms(
            "Quelle baseline s'applique si je ne donne pas le type de systeme ?");
        Assert.Contains("baseline", baselineTerms);
        Assert.Contains("baselines", baselineTerms);
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_trims_family_prefix_from_standard_reference_hint()
    {
        var phrases = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?");

        Assert.Contains("iso 13849", phrases);
        Assert.DoesNotContain("famille iso 13849", phrases);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_standard_reference_family_anchors()
    {
        var standardPart = TestMatch(
            text: "EN ISO 13849-1:2015 Safety-related parts of control systems. General principles for design.",
            docPath: "Standards/ISO-13849-1.pdf",
            chunkId: "standard-part-1",
            score: 0.76);
        var selected = new List<RagMatch> { standardPart };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?",
            selected);

        Assert.Equal("standard-part-1", Assert.Single(selected).ChunkId);
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_keeps_each_named_file_separate()
    {
        var phrases = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Compare ABC 12.2.pdf et ABC 12.4.pdf si le texte est mal extrait.");

        Assert.Contains("abc 12 2 pdf", phrases);
        Assert.Contains("abc 12 2", phrases);
        Assert.Contains("abc 12 4 pdf", phrases);
        Assert.Contains("abc 12 4", phrases);
        Assert.DoesNotContain("abc 12 5 pdf", phrases);

        var groups = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhraseGroups(
            "Compare ABC 12.2.pdf et ABC 12.4.pdf si le texte est mal extrait.");

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.Contains("abc 12 2 pdf"));
        Assert.Contains(groups, group => group.Contains("abc 12 4 pdf"));

        var longNamedFileGroups = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhraseGroups(
            "Compare NFPA 79 2024 Electrical Standard for Industrial Machinery.pdf et UL 508A 2018 Industrial Control Panels - Scan.pdf sur machine industrielle vs panneaux industriels.");

        Assert.Equal(2, longNamedFileGroups.Count);
        Assert.Contains(longNamedFileGroups, group => group.Contains("nfpa 79 2024 electrical standard for industrial machinery pdf"));
        Assert.Contains(longNamedFileGroups, group => group.Contains("ul 508a 2018 industrial control panels scan pdf"));

        var underscored = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Donne une reponse courte a partir de `US_FAR.pdf` avec une citation exploitable.");

        Assert.Contains("us far pdf", underscored);
        Assert.Contains("us far", underscored);
    }

    [Fact]
    public void SelectionsCoverExplicitFileLookupGroups_accepts_extensionless_standard_reference_in_content_only()
    {
        var selected = new List<RagMatch>
        {
            TestMatch(
                text: "EN 15281. Absolute inerting means replacing air with an inert gas.",
                embedText: "EN 15281. Absolute inerting means replacing air with an inert gas.",
                docPath: "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf")
        };

        Assert.True(RetrievalQueryPolicy.SelectionsCoverExplicitFileLookupGroups(
            "Avant de dire que l'inertage respecte EN 15281, que faut-il demander ?",
            selected));
        Assert.False(RetrievalQueryPolicy.SelectionsCoverExplicitFileLookupGroups(
            "Dans EN 15281.pdf, que faut-il demander ?",
            selected));
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_detects_strong_extensionless_document_names()
    {
        var phrases = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les appendices ?");
        var naturalLanguage = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Dans deux semaines, peux-tu retrouver ou commence le planning ?");
        var roleClassification = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quels documents semblent etre des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?");
        var technicalCertificateSubject = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Que puis-je conclure du certificat USP Class VI pour TF1641 et TF1620, sans extrapoler a tous les PTFE ?");
        var technicalDatasheetSubject = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Que permet de savoir la fiche Siemens Moteur Simotics 1LE1003-0EB42-2FB4-Z ?");
        var technicalMsdsSubject = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quelles informations de securite faut-il extraire de la MSDS 3M PTFE TF1620/TF1641/TF1645 ?");
        var technicalInventorySubject = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quels documents dois-je lire en priorite si je cherche des certificats, des fiches techniques et des modes d emploi ?");
        var quotedProductTopic = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quels documents parlent de `PTFE TF1620 TF1641 TF1645` et que permettent-ils de verifier sans extrapoler ?");
        var quotedEvidenceTopic = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Quels passages du corpus documentent `phthalates DEHP BBP DBP DIBP` et dans quel type de document les trouve-t-on ?");
        var quotedComparisonTopic = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Compare les sources disponibles sur `PTFE TF1620 TF1641 TF1645` entre certificats, data sheets, brochure ou MSDS selon ce qui existe dans le corpus.");
        var documentRelationshipTopic = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Explique le role du fichier AC par rapport au document principal EN 13135-1.");

        Assert.Contains(phrases, phrase =>
            phrase.Contains("800 160v1r1", StringComparison.Ordinal)
            && phrase.Contains("sse appendices", StringComparison.Ordinal));
        Assert.Empty(naturalLanguage);
        Assert.Empty(roleClassification);
        Assert.Empty(technicalCertificateSubject);
        Assert.Empty(technicalDatasheetSubject);
        Assert.Empty(technicalMsdsSubject);
        Assert.Empty(technicalInventorySubject);
        Assert.Empty(quotedProductTopic);
        Assert.Empty(quotedEvidenceTopic);
        Assert.Empty(quotedComparisonTopic);
        Assert.Empty(documentRelationshipTopic);

        var possessive = RetrievalLexicalExpansion.ExtractExplicitFileLookupPhrases(
            "Resume uniquement ce que les annexes de ACME_Report_2024_Appendices ajoutent au document principal.");

        Assert.Contains(possessive, phrase =>
            phrase.Contains("acme report 2024 appendices", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_matches_ocr_glued_title_tokens()
    {
        var score = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(
            ["escalope de volaille aux champignons pommes darphin"],
            "Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS, POMMES DARPHIN50 min Ingredients.");

        Assert.True(score >= 20.0);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_prefers_full_ocr_title_over_partial_ingredient_overlap()
    {
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");
        var target = "FLORA LYCEE PROFESSIONNEL Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS, POMMES DARPHIN50 min Ingredients.";
        var partial = "Alouettes sans tete. Ingredients: escalopes de volailles, champignons sautes et puree de pomme de terre.";

        var targetScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > partialScore);
        Assert.True(targetScore >= 20.0);
        Assert.Equal(0.0, partialScore);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 12)]
    [InlineData(8, 48)]
    [InlineData(20, 96)]
    public void ResolveExactMatchCandidateLimit_overfetches_before_selection(int topK, int expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ResolveExactMatchCandidateLimit(topK));
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_catalog_overview_profiles()
    {
        const string query = "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?";
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

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Single(selected);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_document_overview_profiles()
    {
        const string query = "Quels documents dois-je lire en priorite si je cherche des certificats, des fiches techniques et des modes d emploi ?";
        var selected = new List<RagMatch>
        {
            TestMatch(
                "Document profile: certificates, technical data sheets and operating instructions are available.",
                docPath: "Docs/technical-profile.pdf",
                chunkId: "technical-profile",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile",
                score: 0.74)
        };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Single(selected);
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_does_not_apply_precise_pruning_to_comparative_profile_assist()
    {
        const string query = "Compare les recettes etudiantes et les recettes dejeuner au travail : objectif budget et equilibre.";
        var studentProfile = TestMatch(
            "Document profile: super recettes etudiants, rapides et accessibles.",
            chunkId: "student-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");
        var workProfile = TestMatch(
            "Document profile: dejeuner au travail, budget et equilibre.",
            chunkId: "work-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");
        var selected = new List<RagMatch> { studentProfile, workProfile };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["student-profile", "work-profile"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void PruneUnanchoredFocusedLookupSelections_keeps_profiles_for_evidence_quality_synthesis()
    {
        const string query = "Quels documents doivent produire une reponse \u201cje ne peux pas confirmer\u201d si le texte n\u2019est pas extrait ?";
        var profile = TestMatch(
            "Document profile: extraction can be incomplete and some pages require manual review.",
            chunkId: "quality-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");
        var selected = new List<RagMatch> { profile };

        RetrievalTitleSelection.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["quality-profile"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_keeps_single_strong_quoted_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Chouquettes \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("chouquettes", phrases);
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_repairs_mojibake_french_quote_markers()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00c2\u00ab Sauce au poivre \u00c2\u00bb : ingr\u00c3\u00a9dients, \u00c3\u00a9tapes, temps et source ?");

        Assert.Contains("sauce au poivre", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.EndsWith(" a", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_ignores_quoted_response_shape_columns()
    {
        var query = "Peux-tu faire un tableau \u201cancienne source / nouvelle source / changement / impact\u201d pour objectif 1,5 degres vs synthese AR6 ?";

        Assert.Empty(RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(query));
        Assert.Empty(RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query));
        Assert.True(RetrievalQueryPolicy.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_ignores_quoted_generic_user_question()
    {
        var query = "Donne une reponse prudente a un utilisateur qui demande \u201cquelle version est correcte ?\u201d pour objectif 1,5 degres vs synthese AR6.";

        Assert.Empty(RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(query));
        Assert.True(RetrievalQueryPolicy.ShouldTreatAsBroadDiversityQuery(query));
    }

    [Theory]
    [InlineData("Tu peux me faire une fiche claire pour « Pudding vapeur au sirop » : ingredients, etapes, temps et source ?", true)]
    [InlineData("Pudding vapeur au sirop source", true)]
    [InlineData("Quels sont les composants de Pudding vapeur au sirop ?", true)]
    [InlineData("Je veux une fiche claire sans titre precis.", false)]
    [InlineData("Suggest a complete weekly plan from this category.", false)]
    [InlineData("Quels documents parlent d'inertage ?", false)]
    public void ShouldRelaxDefaultScoreFloorForFocusedStructuredLookup_only_for_targeted_detail_requests(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RetrievalTitleSelection.ShouldRelaxDefaultScoreFloorForFocusedStructuredLookup(query));
    }

    [Theory]
    [InlineData("Combien de composants pour assembler le kit Alpha ?", "balanced", false)]
    [InlineData("Calcule les quantites pour 10 lots de module Alpha.", "balanced", false)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", "balanced", false)]
    [InlineData("How many O-rings for pump HPX-2000?", "balanced", false)]
    [InlineData("Calcule les quantites pour \"Mode acces A\".", "balanced", false)]
    [InlineData("Combien de composants pour assembler le kit Alpha ?", "focused", false)]
    public void ShouldSkipSparseRetrieverForQuantityLookup_keeps_lexical_retrieval_for_factual_values(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipSparseRetrieverForQuantityLookup(query, mode));
    }

    [Theory]
    [InlineData("Est-ce que la sauce aux 4 fromages vient de Chefbot ou Moulinex ?", true)]
    [InlineData("Tu as la recette du boeuf bourguingnon ?", true)]
    [InlineData("Donne-moi le one pot pasta brocoli dinde bacon.", true)]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?", true)]
    [InlineData("Saumon avec sauce yaourt-menthe source", true)]
    [InlineData("Alpha Beta Procedure source", true)]
    [InlineData("Je cherche le gateau chocolat courgette.", true)]
    [InlineData("J'ai des champignons, propose-moi plusieurs options.", false)]
    [InlineData("Quelles sources parlent de corrosion interne ?", false)]
    [InlineData("Quels documents parlent d'inertage ?", false)]
    [InlineData("Montre-moi les documents qui parlent d'inertage.", false)]
    [InlineData("Comment choisir un capteur pour zone dangereuse ?", false)]
    [InlineData("How should I choose the right sensor from these manuals?", false)]
    [InlineData("Suggest a complete weekly plan from this category.", false)]
    [InlineData("Compare trois procedures salees pour enfants : temps, materiel, risque de ratage.", false)]
    [InlineData("Compare trois recettes sal\u00e9es pour enfants : temps, mat\u00e9riel, risque de ratage.", false)]
    [InlineData("Une procedure enfant.", false)]
    [InlineData("Pour la procedure Alpha Beta, quels sont les parametres et le reglage ?", true)]
    [InlineData("Quels reglages de temperature pour le module Alpha Beta ?", true)]
    [InlineData("Le modele HPX-2000 est-il certifie ATEX ?", false)]
    [InlineData("Quelle certification pour le capteur ABC-123 ?", false)]
    [InlineData("Donne les specifications techniques du module ZX-9.", false)]
    [InlineData("Ce produit est-il conforme a la norme IEC 60079 ?", false)]
    [InlineData("Que puis-je conclure du certificat USP Class VI pour TF1641 et TF1620 sans extrapoler ?", false)]
    [InlineData("Que permet de savoir la fiche Siemens Moteur Simotics 1LE1003-0EB42-2FB4-Z ?", false)]
    [InlineData("Quelles informations de securite faut-il extraire de la MSDS TF1620 TF1641 TF1645 ?", false)]
    [InlineData("Explique le role du fichier AC par rapport au document principal EN 13135-1.", false)]
    public void ShouldSkipDocumentProfileSearchForPreciseLookup_only_skips_focused_title_requests(string query, bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForPreciseLookup(query));
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForComparativeLookup_keeps_profile_assist_for_reference_comparisons()
    {
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les variantes BS, DIN et ISO de la vanne trois voies 4G 300130."));
    }

    [Theory]
    [InlineData("Combien de composants pour assembler le kit Alpha ?", true)]
    [InlineData("Calcule les quantites pour 10 lots de module Alpha.", true)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", false)]
    [InlineData("How many O-rings for pump HPX-2000?", false)]
    [InlineData("Combien de documents parlent d'inertage ?", false)]
    [InlineData("Fais une liste de quantites communes a partir des documents.", false)]
    public void ShouldSkipDocumentProfileSearchForQuantityLookup_handles_precise_quantity_requests(string query, bool expected)
    {
        Assert.Equal(expected, RetrievalCandidateSignals.ShouldSkipDocumentProfileSearchForQuantityLookup(query));
    }

    [Theory]
    [InlineData("Il me faut la tartiflette, ingrédients + étapes en version claire.", true)]
    [InlineData("Quels livres de recettes tu vois dans la base ?", false)]
    [InlineData("Compare les desserts chocolatés et dis lequel est le plus simple.", false)]
    public void ShouldRunFocusedLookupEmptySelectionRecovery_detects_precise_zero_result_recovery(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RetrievalCandidateSignals.ShouldRunFocusedLookupEmptySelectionRecovery(query));
    }

    [Fact]
    public void ShouldRunFocusedLookupEmptySelectionRecovery_skips_explicit_file_queries()
    {
        Assert.False(RetrievalCandidateSignals.ShouldRunFocusedLookupEmptySelectionRecovery(
            "Cite les conditions de paiement dans `Example_General_Conditions.pdf`."));
    }

    [Fact]
    public void BuildExactMatchLookupTerms_keeps_quoted_title_for_glued_heading_lookup()
    {
        var terms = RetrievalChannels.BuildExactMatchLookupTerms(
            "Give me a clear sheet for \"Safety valve inspection\".");

        Assert.Contains("safety valve inspection", terms);
    }

    [Fact]
    public void BuildExactMatchLookupTerms_keeps_standard_reference_without_year()
    {
        var terms = RetrievalChannels.BuildExactMatchLookupTerms(
            "Explique le role du fichier AC par rapport au document principal EN 13135-1.");

        Assert.Contains("en 13135 1", terms);
    }

    [Fact]
    public void ComputeExactMatchScore_keeps_prefix_heading_match_below_short_circuit_threshold()
    {
        var score = RetrievalNavigation.ComputeExactMatchScore(
            "verbatim_excerpt",
            "safety valve inspection",
            "Safety valve inspectionThe inspection schedule starts here.");

        Assert.InRange(score, 0.96, 0.969);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_rewards_full_title_token_coverage_when_pdf_glues_words()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?");
        var target = "Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce. Salade de haricotsverts à l'avocat.";
        var partial = "Haricots verts vapeur avec huile d'olive, citron, sel et poivre.";

        var targetScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > partialScore);
        Assert.True(targetScore >= 8.0);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_prefers_heading_over_measured_ingredient_occurrence()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour « Bouillon de volaille » : ingredients, etapes, temps et source ?");
        var heading = "INGRÉDIENTS: carcasse, oignons, céleri. BOUILLON DE VOLAILLE SAUCES PRÉPARATION: laisser mijoter 1h.";
        var ingredient = "Osso buco pour 4 personnes: 75 cl de bouillon de volaille, tomates, farine, vin blanc.";

        var headingScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, heading);
        var ingredientScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, ingredient);

        Assert.True(headingScore > ingredientScore);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_requires_leading_signal_for_long_partial_title()
    {
        var phrases = RetrievalLexicalExpansion.ExtractQuotedLookupPhrases(
            "Give me a clear sheet for \"Alpha Beta Gamma Delta\".");
        var target = "Alpha Beta Gamma Delta procedure. Steps and controls follow.";
        var partial = "Beta, gamma and delta are referenced together in a glossary row.";

        var targetScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RetrievalLexicalExpansion.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > 0);
        Assert.Equal(0.0, partialScore);
    }

    [Fact]
    public void ShouldTreatQuotedLookupAsTopic_detects_yes_no_and_corpus_topic_quotes()
    {
        Assert.True(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Je veux une reponse oui/non sur `conditions de paiement`. Si les PDF ne permettent pas un oui/non clair, refuse la simplification."));
        Assert.True(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Retrouve les documents qui parlent de `conflit d interets` et donne les passages utiles."));
        Assert.True(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a `conditions de paiement`."));
        Assert.True(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Quels passages du corpus documentent `phthalates DEHP BBP DBP DIBP` et dans quel type de document les trouve-t-on ?"));
        Assert.True(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Je crois que le corpus impose toujours `conditions de paiement`. Verifie si c est vraiment une obligation generale ou si ce n est pas demontre."));

        Assert.False(RetrievalQueryPolicy.ShouldTreatQuotedLookupAsTopic(
            "Tu peux me faire une fiche claire pour `Patatas Bravas` : ingredients, etapes, temps et source ?"));
    }

    [Fact]
    public void ComputeExactMatchScore_prioritizes_structured_references_over_plain_verbatim()
    {
        var standard = RetrievalNavigation.ComputeExactMatchScore("standard_ref", "en 15281", "EN 15281");
        var code = RetrievalNavigation.ComputeExactMatchScore("code_ref", "ind570", "IND570");
        var verbatim = RetrievalNavigation.ComputeExactMatchScore("verbatim_excerpt", "maintenance", "maintenance procedure");

        Assert.True(standard > code);
        Assert.True(code > verbatim);
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_prefers_single_strong_reference_hit()
    {
        var exact = new RagMatch(
            0.98,
            "doc-1",
            "ATEX/CEN TR 15281 2006.pdf",
            "CEN TR 15281 2006.pdf",
            null,
            null,
            "docmeta:1",
            -1,
            "CEN TR 15281 2006.pdf [cen tr 15281 2006]",
            1,
            "hash",
            "CEN TR 15281 2006.pdf [cen tr 15281 2006]",
            "exact_match_v1",
            null,
            null,
            null,
            null,
            "document_metadata_ref",
            null,
            null,
            null);

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterExact([exact]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_accepts_single_source_backed_canonical_chunk()
    {
        var exact = new RagMatch(
            Score: 1.0,
            DocId: "doc-1",
            DocPath: "Programmation/Mettler/MettlerToledo_IND570.pdf",
            DocName: "MettlerToledo_IND570.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "10bc55ca-b9d8-2138-fe34-069939a1bee5",
            ChunkIndex: 0,
            Text: "The IND570 terminal manual includes installation, configuration and diagnostics.",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "The IND570 terminal manual includes installation, configuration and diagnostics.",
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Manual",
            HeadingPath: "Manual",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: "content");

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterExact(
            "Est ce que tu as le manuel du terminal IND570 ?",
            [exact]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_companion_relation_queries_open()
    {
        var exact = new RagMatch(
            0.98,
            "doc-1",
            "Standards/EN 13135-2 2005 AC.pdf",
            "EN 13135-2 2005 AC.pdf",
            null,
            null,
            "docmeta:1",
            -1,
            "EN 13135-2 2005 AC.pdf [en 13135 2]",
            1,
            "hash",
            "EN 13135-2 2005 AC.pdf [en 13135 2]",
            "exact_match_v1",
            null,
            null,
            null,
            null,
            "document_metadata_ref",
            null,
            null,
            null);

        Assert.True(RetrievalNavigation.ContainsDocumentCompanionRelationIntent(
            "Explique le role du fichier AC par rapport au document principal EN 13135-2."));
        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterExact(
            "Explique le role du fichier AC par rapport au document principal EN 13135-2.",
            [exact]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_search_open_when_exact_results_are_ambiguous()
    {
        var top = new RagMatch(0.98, "doc-1", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "docmeta:1", -1, "CEN TR 15281 2006.pdf", 1, "hash1", "CEN TR 15281 2006.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var second = new RagMatch(0.955, "doc-2", "ATEX/Other 15281.pdf", "Other 15281.pdf", null, null, "docmeta:2", -1, "Other 15281.pdf", 1, "hash2", "Other 15281.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterExact([top, second]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_allows_overfetched_clear_winner()
    {
        var top = new RagMatch(1.02, "doc-1", "Ops/Primary.pdf", "Primary.pdf", 4, 4, "exact:1", 1, "Primary reference", 1, "hash1", "Primary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);
        var second = new RagMatch(0.90, "doc-2", "Ops/Secondary.pdf", "Secondary.pdf", 8, 8, "exact:2", 2, "Secondary reference", 1, "hash2", "Secondary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);
        var third = new RagMatch(0.84, "doc-3", "Ops/Tertiary.pdf", "Tertiary.pdf", 9, 9, "exact:3", 3, "Tertiary reference", 1, "hash3", "Tertiary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);

        Assert.True(RetrievalNavigation.ShouldShortCircuitAfterExact([top, second, third]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_search_open_for_navigation_hits()
    {
        var index = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41",
            "exact_match_v1",
            1,
            1,
            "Document",
            "Document",
            "exact_match_entry",
            null,
            null,
            null);

        Assert.False(RetrievalNavigation.ShouldShortCircuitAfterExact([index]));
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForPreciseLookup_keeps_profile_assist_for_document_hint()
    {
        Assert.False(RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForPreciseLookup(
            "Donne-moi la procedure Alpha Beta du guide Acme."));
    }

    [Fact]
    public void PruneUnpagedProfileSelectionsForPreciseLookup_keeps_paged_title_evidence()
    {
        var unpagedProfile = new RagMatch(
            1.02,
            "doc-profile",
            "Generic/Profile.pdf",
            "Profile.pdf",
            null,
            null,
            "profile",
            null,
            "This profile gives a broad overview of the document.",
            1,
            "hash-profile",
            "This profile gives a broad overview of the document.",
            "document_profile_v1",
            null,
            null,
            null,
            null,
            "document_profile",
            null,
            null,
            null);
        var pagedTitle = TestMatch(
            text: "Index entry and details for Alpha Beta are available on this page.",
            embedText: "Index entry and details for Alpha Beta are available on this page.",
            docPath: "Generic/Details.pdf",
            page: 4,
            score: 0.82,
            chunkId: "paged-title",
            chunkType: "unit_exact_v1");
        var selected = new List<RagMatch> { unpagedProfile, pagedTitle };

        RetrievalCandidateSignals.PruneUnpagedProfileSelectionsForPreciseLookup("alpha beta", selected);

        Assert.Single(selected);
        Assert.Equal("paged-title", selected[0].ChunkId);
    }

    [Fact]
    public void PruneUnpagedProfileSelectionsForPreciseLookup_keeps_comparative_profile_assist_context()
    {
        var unpagedProfile = new RagMatch(
            1.02,
            "doc-profile",
            "Generic/Profile.pdf",
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
        var pagedTitle = TestMatch(
            text: "Procedure for apprentices with material checklist and setup controls.",
            docPath: "Generic/Details.pdf",
            page: 4,
            score: 0.82,
            chunkId: "paged-title",
            chunkType: "unit_exact_v1");
        var selected = new List<RagMatch> { unpagedProfile, pagedTitle };

        RetrievalCandidateSignals.PruneUnpagedProfileSelectionsForPreciseLookup(
            "Compare three procedures for apprentices: material, risks and setup notes.",
            selected);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void BuildExactMatchLookupTerms_prunes_weak_count_terms_but_keeps_specific_references()
    {
        var terms = RetrievalChannels.BuildExactMatchLookupTerms(
            "Compare documented options XR-42 for 4 people and pick the best candidate.");

        Assert.DoesNotContain(terms, RetrievalChannels.IsWeakCountQuantityExactLookupTerm);
        Assert.Contains("xr 42", terms);
    }

    [Theory]
    [InlineData("mode a jd", true)]
    [InlineData("safety valve inspection", true)]
    [InlineData("en 15281", false)]
    [InlineData("jd", false)]
    [InlineData("", false)]
    public void ShouldUseContainsExactMatchLookupTerm_keeps_only_phrase_fallback_terms(string term, bool expected)
    {
        Assert.Equal(expected, RetrievalChannels.ShouldUseContainsExactMatchLookupTerm(term));
    }

    [Fact]
    public void BuildContainsExactMatchLookupTerms_filters_short_exact_terms()
    {
        var terms = RetrievalChannels.BuildContainsExactMatchLookupTerms(
            ["en 15281", "mode a jd", "safety valve inspection"]);

        Assert.DoesNotContain("en 15281", terms);
        Assert.Contains("mode a jd", terms);
        Assert.Contains("safety valve inspection", terms);
    }
}
