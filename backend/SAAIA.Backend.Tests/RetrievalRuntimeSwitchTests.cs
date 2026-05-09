using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalRuntimeSwitchTests
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
    public void BuildFocusedLexicalBackfillQuery_keeps_specific_user_topic()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?");

        Assert.Contains("lentilles", focused, StringComparison.Ordinal);
        Assert.Contains("corail", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("existent", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("recettes", focused, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.", "coq au vin")]
    [InlineData("Donne-moi la methode pour les fruits en beignets.", "fruits en beignets")]
    [InlineData("C'est quoi les grandes etapes du boeuf bourguignon ?", "boeuf bourguignon")]
    [InlineData("C’est quoi les grandes étapes du bœuf bourguignon ?", "boeuf bourguignon")]
    [InlineData("C'est quoi la Tentation de Jansson et comment la faire ?", "tentation de jansson")]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", "tartiflette")]
    [InlineData("Donne-moi le one pot pasta brocoli dinde bacon.", "one pot pasta brocoli dinde bacon")]
    [InlineData("Je cherche la tartiflet ou un truc fromage pomme de terre.", "tartiflet")]
    [InlineData("Tu as la recette du boeuf bourguingnon ?", "boeuf bourguingnon")]
    [InlineData("Est-ce que la sauce aux 4 fromages vient de Chefbot ou Moulinex ?", "sauce aux 4 fromages")]
    [InlineData("Combien de temps et quels ingredients pour le gratin dauphinois ?", "gratin dauphinois")]
    [InlineData("Calcule les quantites pour 10 bols de veloute.", "veloute")]
    [InlineData("D'ou vient la recette de la Tentation de Jansson ? Donne le PDF et la page si possible.", "tentation de jansson")]
    [InlineData("Comment cuire les asperges vertes au miel avec la sonde de rotissage ?", "asperges vertes au miel")]
    [InlineData("Tu peux m'expliquer les patatas bravas du livre NEFF ?", "patatas bravas")]
    [InlineData("Donne la recette des patattas bravas.", "patattas bravas")]
    [InlineData("Comment faire la mayonnaise au tofu ?", "mayonnaise au tofu")]
    [InlineData("Detaille le curry de crevettes et riz basmati.", "curry de crevettes et riz basmati")]
    [InlineData("Explique-moi les churros sauce chocolat au Companion.", "churros sauce chocolat")]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?", "patatas bravas")]
    [InlineData("Je veux une fiche pour Cr\u00e8me au citron avec source.", "creme au citron")]
    [InlineData("Give me the procedure for access mode A from the manual.", "access mode a")]
    [InlineData("Dame una ficha para modo acceso A con fuente.", "modo acceso a")]
    [InlineData("Quero uma ficha para modo acesso A com fonte.", "modo acesso a")]
    [InlineData("Zeige eine Karte zu Zugang Modus A aus dem Handbuch.", "zugang modus a")]
    [InlineData("Mostra una scheda per modalita accesso A con fonte.", "modalita accesso a")]
    public void BuildFocusedLexicalBackfillQuery_extracts_unquoted_content_targets(string query, string expected)
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(query);

        Assert.Equal(expected, focused);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_keeps_full_context_after_trimmed_title_variant()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Explique-moi les churros sauce chocolat au Companion.");

        Assert.Collection(
            phrases.Take(2),
            first => Assert.Equal("churros sauce chocolat", first),
            second => Assert.Equal("churros sauce chocolat au companion", second));
    }

    [Theory]
    [InlineData("Compare les deux quiches lorraines du corpus : differences ingredients, methode et style.", "quiches lorraines")]
    [InlineData("Il y a plusieurs cremes brulees ? Compare-les si oui.", "cremes brulees")]
    [InlineData("Compare les sauces tomate des deux documents.", "sauces tomate")]
    public void ExtractComparativeLookupPhrases_extracts_subject_without_corpus_noise(string query, string expected)
    {
        var phrases = RagEndpoints.ExtractComparativeLookupPhrases(query);

        Assert.Contains(expected, phrases);
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForComparativeLookup_detects_precise_comparison_subject()
    {
        Assert.True(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les deux quiches lorraines du corpus : differences ingredients, methode et style."));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_reads_french_guillemet_title_in_larger_request()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Asperges vertes au miel \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("asperges vertes au miel", phrases);
    }

    [Fact]
    public void BuildTitleTokens_adds_singular_variants_for_plural_lookup_terms()
    {
        var tokens = TitleAnchorNormalizer.BuildTitleTokens("Terrines de legumes", maxTokens: 12);

        Assert.Contains("terrines", tokens);
        Assert.Contains("terrine", tokens);
        Assert.Contains("legumes", tokens);
    }

    [Fact]
    public void ComputeDocumentProfileSearchResultLimit_overfetches_for_generic_profile_reranking()
    {
        Assert.Equal(32, RagEndpoints.ComputeDocumentProfileSearchResultLimit(8));
        Assert.Equal(80, RagEndpoints.ComputeDocumentProfileSearchResultLimit(64));
    }

    [Fact]
    public void BuildDocumentProfileSpecificityTokens_keeps_domain_constraints_without_primary_intent_noise()
    {
        var tokens = RagEndpoints.BuildDocumentProfileSpecificityTokens(
            "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.");

        Assert.Contains("apprentices", tokens);
        Assert.Contains("material", tokens);
        Assert.Contains("failure", tokens);
        Assert.Contains("setup", tokens);
        Assert.DoesNotContain("compare", tokens);
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_restores_profile_constraints_filtered_from_chunk_terms()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";

        var chunkTerms = RagEndpoints.BuildLexicalContentFallbackTerms(query);
        var profileTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(query);

        Assert.DoesNotContain("material", chunkTerms);
        Assert.Contains("material", profileTerms);
        Assert.Contains("apprentices", profileTerms);
        Assert.Contains("mat\u00e9riel", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Compare trois procedures pour apprentis : mat\u00e9riel, risques et consignes."));
    }

    [Fact]
    public void ResolveDocumentProfileCandidateCount_keeps_profile_window_bounded()
    {
        Assert.Equal(12, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: true));
        Assert.Equal(12, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: false));
        Assert.Equal(10, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 10, topK: 8, profileOnly: true));
    }

    [Fact]
    public void ShouldAllowSparseAssistForScopedProfileFallback_detects_broad_queries_with_strong_constraints()
    {
        Assert.True(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Compare trois procedures pour apprentis : materiel, risques et consignes."));
        Assert.False(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Fais une vue d'ensemble des documents disponibles."));
    }

    [Fact]
    public void ShouldPrioritizeDocumentProfilesForSelection_yields_to_sparse_assist_when_constraints_are_strong()
    {
        Assert.True(RagEndpoints.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: false));
        Assert.False(RagEndpoints.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: true));
    }

    [Fact]
    public void ShouldDeferDocumentProfileSearch_only_defers_scoped_profile_queries_with_sparse_assist()
    {
        Assert.True(RagEndpoints.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
        Assert.False(RagEndpoints.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: true,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
        Assert.False(RagEndpoints.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: false));
        Assert.False(RagEndpoints.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: false,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
    }

    [Fact]
    public void ShouldRunDeferredDocumentProfileSearch_runs_only_when_sparse_results_are_insufficient()
    {
        Assert.True(RagEndpoints.ShouldRunDeferredDocumentProfileSearch(
            selected: [],
            topK: 8,
            preferDocumentDiversity: true,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));

        var enoughDiverseMatches = new[]
        {
            TestMatch("Procedure one with material and risks.", docPath: "Docs/A.pdf", chunkId: "a"),
            TestMatch("Procedure two with material and risks.", docPath: "Docs/B.pdf", chunkId: "b"),
            TestMatch("Procedure three with material and risks.", docPath: "Docs/C.pdf", chunkId: "c"),
            TestMatch("Procedure four with material and risks.", docPath: "Docs/D.pdf", chunkId: "d")
        };
        Assert.False(RagEndpoints.ShouldRunDeferredDocumentProfileSearch(
            enoughDiverseMatches,
            topK: 8,
            preferDocumentDiversity: true,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));

        var sameDocumentMatches = new[]
        {
            TestMatch("Procedure one with material and risks.", docPath: "Docs/A.pdf", chunkId: "a", page: 1),
            TestMatch("Procedure two with material and risks.", docPath: "Docs/A.pdf", chunkId: "b", page: 2),
            TestMatch("Procedure three with material and risks.", docPath: "Docs/A.pdf", chunkId: "c", page: 3),
            TestMatch("Procedure four with material and risks.", docPath: "Docs/A.pdf", chunkId: "d", page: 4)
        };
        Assert.True(RagEndpoints.ShouldRunDeferredDocumentProfileSearch(
            sameDocumentMatches,
            topK: 8,
            preferDocumentDiversity: true,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
    }

    [Fact]
    public void ComputeDocumentProfileSpecificityBoost_rewards_rare_constraints_over_common_profile_words()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var peerTexts = new[]
        {
            "Procedure catalogue with setup notes and common operating procedure references.",
            "Procedure guide for apprentices with material checklist, failure risk notes and setup controls.",
            "General procedure overview with setup notes and operating constraints."
        };

        var genericBoost = RagEndpoints.ComputeDocumentProfileSpecificityBoost(query, peerTexts[0], peerTexts);
        var targetedBoost = RagEndpoints.ComputeDocumentProfileSpecificityBoost(query, peerTexts[1], peerTexts);

        Assert.True(targetedBoost > genericBoost);
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_keeps_single_strong_quoted_title()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Chouquettes \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("chouquettes", phrases);
    }

    [Theory]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.")]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?")]
    public void ShouldBackfillEnumerativeSearch_detects_precise_content_lookup_requests(string query)
    {
        Assert.True(RagEndpoints.ShouldBackfillEnumerativeSearch(query, selectedCount: 0, topK: 8));
    }

    [Theory]
    [InlineData("Aide-moi a preparer 4 options en 2h en reutilisant des bases communes.", true)]
    [InlineData("J’ai des champignons, propose-moi plusieurs options.", true)]
    [InlineData("Fais-moi 5 options pas trop cheres a partir des PDF.", true)]
    [InlineData("Cree une FAQ a partir des conseils du guide.", true)]
    [InlineData("Fais un retroplanning de preparation pour deux sujets.", true)]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", true)]
    [InlineData("Je dois animer un atelier avec 12 personnes : quelles fiches choisir ?", true)]
    [InlineData("Regroupe les exigences communes de 4 documents pour limiter les achats.", true)]
    [InlineData("Traduis en anglais les noms mais garde les parametres en francais.", true)]
    [InlineData("Ajoute les points critiques a surveiller pour eviter une erreur.", true)]
    [InlineData("Suggest a complete weekly plan from this category.", true)]
    [InlineData("Busco una opcion: que versiones encuentras y como distinguirlas?", true)]
    [InlineData("Procuro uma opcao: que versoes encontras e como as distinguir?", true)]
    [InlineData("Cerco una opzione: quali versioni trovi e come distinguerle?", true)]
    [InlineData("Quiero trabajar con ninos: que fichas parecen adecuadas y por que?", true)]
    [InlineData("Quero trabalhar com criancas: que fichas parecem adequadas e porque?", true)]
    [InlineData("Voglio lavorare con bambini: quali schede sembrano adatte e perche?", true)]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", false)]
    [InlineData("I need access mode A from the manual.", false)]
    [InlineData("Je veux le mode acces A du manuel.", false)]
    [InlineData("Quels sont les risques du variateur VX-12 ?", false)]
    [InlineData("Risk controls for valve ABC-123.", false)]
    [InlineData("Points critiques de la procedure LOTO-42.", false)]
    [InlineData("Tu as une entree precise absente du corpus ?", false)]
    public void ShouldUseScopedProfileFallback_detects_broad_scoped_synthesis_requests(string query, bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldUseScopedProfileFallback(query, hasCategoryFilter: true, mode: "balanced"));
    }

    [Fact]
    public void ShouldUseScopedProfileFallback_requires_scope_and_non_focused_mode()
    {
        Assert.False(RagEndpoints.ShouldUseScopedProfileFallback("Suggest a complete weekly plan.", hasCategoryFilter: false, mode: "balanced"));
        Assert.False(RagEndpoints.ShouldUseScopedProfileFallback("Suggest a complete weekly plan.", hasCategoryFilter: true, mode: "focused"));
    }

    [Theory]
    [InlineData("Fais-moi 5 options pas trop cheres a partir des PDF.", "balanced")]
    [InlineData("Compare deux procedures proches.", "broad")]
    [InlineData("Mets les elements avec un mot-cle dans un tableau : nom, source, type.", "balanced")]
    [InlineData("Reponds en JSON avec des candidats pour un plan vegetarien.", "balanced")]
    [InlineData("Je dois eviter une contrainte : quels elements semblent risques et lesquels sont plus faciles a adapter ?", "balanced")]
    [InlineData("Tu peux me faire une vue d'ensemble des documents disponibles, par grands themes ?", "broad")]
    [InlineData("Tu peux me faire une vue d’ensemble des elements disponibles, par grands themes ?", "broad")]
    [InlineData("Menu complet utilisant les elements concus pour la sonde X.", "balanced")]
    [InlineData("Rends le mode A ou le mode B un peu plus robuste sans pretendre que c'est officiel.", "balanced")]
    [InlineData("Je recois des invites : choisis entre option alpha, option beta, option gamma ou option delta et justifie.", "broad")]
    [InlineData("Pose-toi 5 questions de verification avant de repondre a une demande ambigue.", "balanced")]
    [InlineData("Quelles entrees utilisent le produit Alpha et comment les gerer sans le produit Beta ?", "balanced")]
    [InlineData("Compare les options.", "focused")]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "focused")]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", "focused")]
    [InlineData("Donne-moi le mode acces A.", "focused")]
    public void ResolveEffectiveSearchMode_promotes_focused_only_for_broad_intents(string query, string expected)
    {
        Assert.Equal(expected, RagEndpoints.ResolveEffectiveSearchMode("focused", query));
    }

    [Theory]
    [InlineData("Compare les styles de trois procedures presentes.", "broad", true)]
    [InlineData("Compare les sauces robotisees : lesquelles sont adaptees a un debutant ?", "broad", true)]
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipSparseRetrieverForBroadDiversity(query, mode));
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipSparseRetrieverForQuantityLookup(query, mode));
    }

    [Theory]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", "balanced", true)]
    [InlineData("Reponds en JSON avec un tableau de candidats.", "balanced", true)]
    [InlineData("Traduis les noms et garde les parametres en francais.", "balanced", true)]
    [InlineData("Tu peux me faire une vue d’ensemble des documents disponibles, par grands themes ?", "balanced", true)]
    [InlineData("Menu complet utilisant les elements concus pour la sonde X.", "balanced", true)]
    [InlineData("Rends le mode A ou le mode B un peu plus robuste sans pretendre que c'est officiel.", "balanced", true)]
    [InlineData("Pose-toi 5 questions de verification avant de repondre a une demande ambigue.", "balanced", true)]
    [InlineData("Which valve should I choose for pressure class PN16?", "balanced", false)]
    [InlineData("Quels sont les risques du variateur VX-12 ?", "balanced", false)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", "balanced", false)]
    [InlineData("How many O-rings for pump HPX-2000?", "balanced", false)]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "broad", false)]
    public void ShouldSkipSparseProfileCardAssist_only_for_unanchored_broad_work(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldSkipSparseProfileCardAssist(query, mode));
    }

    [Theory]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", "balanced", true)]
    [InlineData("Reponds en JSON avec un tableau de candidats.", "balanced", true)]
    [InlineData("Traduis les noms et garde les parametres en francais.", "balanced", true)]
    [InlineData("Tu peux me faire une vue d’ensemble des documents disponibles, par grands themes ?", "balanced", false)]
    [InlineData("Menu complet utilisant les elements concus pour la sonde X.", "balanced", true)]
    [InlineData("Rends le mode A ou le mode B un peu plus robuste sans pretendre que c'est officiel.", "balanced", true)]
    [InlineData("Pose-toi 5 questions de verification avant de repondre a une demande ambigue.", "balanced", true)]
    [InlineData("Which valve should I choose for pressure class PN16?", "balanced", false)]
    [InlineData("Quels sont les risques du variateur VX-12 ?", "balanced", false)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", "balanced", false)]
    [InlineData("How many O-rings for pump HPX-2000?", "balanced", false)]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "broad", false)]
    public void ShouldSkipDocumentProfileSearchForLowCostBroadQuery_preserves_precise_anchors(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldSkipDocumentProfileSearchForLowCostBroadQuery(query, mode));
    }

    [Theory]
    [InlineData("Est-ce que la sauce aux 4 fromages vient de Chefbot ou Moulinex ?", true)]
    [InlineData("Tu as la recette du boeuf bourguingnon ?", true)]
    [InlineData("Donne-moi le one pot pasta brocoli dinde bacon.", true)]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?", true)]
    [InlineData("J'ai des champignons, propose-moi plusieurs options.", false)]
    [InlineData("Quels documents parlent d'inertage ?", false)]
    [InlineData("Montre-moi les documents qui parlent d'inertage.", false)]
    [InlineData("Comment choisir un capteur pour zone dangereuse ?", false)]
    [InlineData("How should I choose the right sensor from these manuals?", false)]
    [InlineData("Suggest a complete weekly plan from this category.", false)]
    [InlineData("Pour la procedure Alpha Beta, quels sont les parametres et le reglage ?", true)]
    [InlineData("Quels reglages de temperature pour le module Alpha Beta ?", true)]
    public void ShouldSkipDocumentProfileSearchForPreciseLookup_only_skips_focused_title_requests(string query, bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldSkipDocumentProfileSearchForPreciseLookup(query));
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipDocumentProfileSearchForQuantityLookup(query));
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
            RagEndpoints.BuildMatchDedupKey(kept),
            RagEndpoints.BuildMatchDedupKey(pruned)
        };

        RagEndpoints.RebuildSelectedKeys(selected, selectedKeys);

        Assert.Contains(RagEndpoints.BuildMatchDedupKey(kept), selectedKeys);
        Assert.DoesNotContain(RagEndpoints.BuildMatchDedupKey(pruned), selectedKeys);
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

        Assert.True(RagEndpoints.ShouldBackfillFuzzyTitleLead("Donne la recette des patattas bravas.", [navigation]));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_skips_when_content_candidate_exists()
    {
        var content = TestMatch(
            text: "Patatas Bravas ingredients pommes de terre preparation.",
            embedText: "Patatas Bravas ingredients pommes de terre preparation.");

        Assert.False(RagEndpoints.ShouldBackfillFuzzyTitleLead("Donne la recette des patatas bravas.", [content]));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_runs_when_content_lacks_focused_title_coverage()
    {
        var weakContent = TestMatch(
            text: "Techniques de cuisson du boeuf hache et conservation.",
            embedText: "Techniques de cuisson du boeuf hache et conservation.");

        Assert.True(RagEndpoints.ShouldBackfillFuzzyTitleLead("Tu as la recette du boeuf bourguingnon ?", [weakContent]));
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_unquoted_short_title_words_in_focused_targets()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Donne-moi la recette du coq au vin dans le livre international.");

        Assert.Contains("coq au vin", terms);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_uses_focused_target_without_touching_broad_queries()
    {
        var focused = RagEndpoints.ResolvePrimaryRetrievalQuery(
            "Donne-moi la recette du coq au vin dans le livre international.",
            "cuisine");
        var broad = RagEndpoints.ResolvePrimaryRetrievalQuery(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?",
            "cuisine");

        Assert.Equal("coq au vin", focused);
        Assert.Equal("Quelles recettes avec des lentilles corail existent dans les PDF ?", broad);
    }

    [Theory]
    [InlineData("Quels documents parlent d'inertage ?", true)]
    [InlineData("Which documents mention inerting?", true)]
    [InlineData("Explique l'inertage dans ce passage.", false)]
    public void ShouldBackfillEnumerativeSearch_detects_list_or_find_requests(string query, bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldBackfillEnumerativeSearch(query, selectedCount: 1, topK: 8));
    }

    [Fact]
    public void ShouldConstrainPreciseTitleLookup_does_not_limit_enumerative_recipe_searches()
    {
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?"));
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
            OffsetEnd: 133);

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
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

        Assert.Equal("Chunk snippet", payload["text"]);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", payload["embed_text"]);
        Assert.Equal("contextual_text_v1", payload["embedding_basis"]);
        Assert.Equal(2, payload["section_ordinal"]);
        Assert.Equal(9, payload["unit_ordinal"]);
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

        Assert.Equal("canonical-safety", RagEndpoints.ResolveMatchCategory(match, "fallback"));
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

        Assert.True(RagEndpoints.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 9, currentContentHashHex: "deadbeef"));
        Assert.False(RagEndpoints.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 7, currentContentHashHex: "deadbeef"));
        Assert.False(RagEndpoints.IsActiveDenseMatchForRevision(match, currentRevisionIngestionVersion: 9, currentContentHashHex: "feedface"));
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

        Assert.True(RagEndpoints.IsActiveDenseMatchForRevision(legacy, currentRevisionIngestionVersion: 9, currentContentHashHex: "DEADBEEF"));
        Assert.False(RagEndpoints.IsActiveDenseMatchForRevision(legacy, currentRevisionIngestionVersion: 9, currentContentHashHex: "feedface"));
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
            RagEndpoints.BuildMatchDedupKey(exact),
            RagEndpoints.BuildMatchDedupKey(dense));
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
            RagEndpoints.BuildMatchDedupKey(dense),
            RagEndpoints.BuildMatchDedupKey(linked));
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

        var firstKey = RagEndpoints.BuildMatchDedupKey(first);
        Assert.Equal(firstKey, RagEndpoints.BuildMatchDedupKey(second));
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

        var fused = RagEndpoints.FuseWithRrf([exact], [sparse], [denseForB, denseForA]);

        Assert.Equal("doc-a", fused[0].DocId);
        Assert.Equal("exact_match_v1", fused[0].EmbeddingBasis);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void ResolveRetriever_maps_title_and_navigation_routes_explicitly()
    {
        var titleRoute = new RagMatch(0.90, "doc-a", "Ops/Guide.pdf", "Guide.pdf", 5, 5, "chunk-a", 3, "Release checklist", 1, "hash-a", "Release checklist", "title_anchor_route_v1", null, null, "Release", "Release", "section", null, null, null);
        var navigationRoute = titleRoute with { EmbeddingBasis = "navigation_route_v1" };

        Assert.Equal("title_anchor_route", RagEndpoints.ResolveRetriever(titleRoute));
        Assert.Equal("navigation_route", RagEndpoints.ResolveRetriever(navigationRoute));
    }

    [Fact]
    public void NavigationRouteMinimumConfidence_includes_projected_point_seven_values()
    {
        var projectedPointSeven = (double)0.70f;

        Assert.True(projectedPointSeven < 0.70d);
        Assert.True(projectedPointSeven >= RagEndpoints.NavigationRouteMinimumConfidence);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_sparse_for_high_overlap_lexical_query()
    {
        var sparse = new RagMatch(0.74, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-1", 0, "Inerting safety controls and gas flow monitoring requirements", 1, "hash-a", "Inerting safety controls and gas flow monitoring requirements", "sparse_bm25_v1", 1, 1, "Inerting", "Chapter 2 > Inerting", "unit_exact_v1", null, null, null);
        var dense = new RagMatch(0.76, "doc-b", "ATEX/General.pdf", "General.pdf", 1, 1, "dense-1", 0, "General safety guidance overview", 1, "hash-b", "General safety guidance overview", "contextual_text_v1", 1, 1, "Overview", "Chapter 1 > Overview", "section_window_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Quels documents parlent d inerting safety controls en zone ATEX ?", [dense, sparse]);

        Assert.Equal("sparse-1", calibrated[0].ChunkId);
        Assert.Equal("sparse_bm25_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_title_chunk_over_neighbor_context_title()
    {
        var neighbor = new RagMatch(1.02, "doc-a", "Ops/Robot.pdf", "Robot.pdf", 66, 66, "neighbor", 10,
            "Mijote details and timings without the requested item title.",
            1,
            "hash-a",
            "document_name: Robot.pdf\nprevious_context:\nBoeuf bourguignon Pour 4 personnes\nexcerpt:\nMijote details and timings without the requested item title.",
            "sparse_bm25_v1",
            1,
            1,
            "Mijote details",
            "Mijote details",
            "unit_exact_v1",
            null,
            null,
            null);
        var direct = new RagMatch(0.91, "doc-b", "Ops/Top30.pdf", "Top30.pdf", 5, 5, "direct", 2,
            "Boeuf bourguignon Pour 4 personnes ingredients and preparation.",
            1,
            "hash-b",
            "Matched profile title: Boeuf bourguignon\nBoeuf bourguignon Pour 4 personnes ingredients and preparation.",
            "sparse_bm25_v1",
            1,
            1,
            "Boeuf bourguignon",
            "Boeuf bourguignon",
            "unit_exact_v1",
            null,
            null,
            null,
            MatchedContentCards: [new RagMatchedContentCard("Boeuf bourguignon")]);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "boeuf bourguignon",
            [neighbor, direct],
            "C'est quoi les grandes etapes du boeuf bourguignon ?");

        Assert.Equal("direct", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_navigation_chunks_for_content_queries()
    {
        var navigation = new RagMatch(
            0.99,
            "doc-index",
            "Docs/Device.pdf",
            "Device.pdf",
            176,
            176,
            "nav",
            0,
            "Vegetarian items 153Burger vegetarian 154Stuffed cabbage 155Tomato clafoutis 156Vegetable flan 157Potato galette 158Quiche lorraine 186Chocolate cake 187",
            1,
            "hash-index",
            "Vegetarian items 153Burger vegetarian 154Stuffed cabbage 155Tomato clafoutis 156Vegetable flan 157Potato galette 158Quiche lorraine 186Chocolate cake 187",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var content = new RagMatch(
            0.94,
            "doc-recipe",
            "Docs/Recipes.pdf",
            "Recipes.pdf",
            15,
            15,
            "content",
            1,
            "Quiche lorraine ingredients include eggs and bacon. Preparation method: make the dough, fill the tart and bake until golden.",
            1,
            "hash-content",
            "Quiche lorraine ingredients include eggs and bacon. Preparation method: make the dough, fill the tart and bake until golden.",
            "sparse_bm25_v1",
            1,
            1,
            "Guide",
            "Guide",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Compare les quiches lorraines du corpus : ingredients et methode.", [navigation, content]);

        Assert.Equal("content", calibrated[0].ChunkId);
        Assert.True(RagEndpoints.LooksLikeNavigationalChunk(navigation));
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
            "Entrées•Salade de lentilles1•Salade de haricots verts à l’avocat2•Taboulé5•Quiche lorraine16Index•Clafoutis aux pommes3•Brownies21FicheFicheFichefiche-index Page 1",
            1,
            "hash-index",
            "Entrées•Salade de lentilles1•Salade de haricots verts à l’avocat2•Taboulé5•Quiche lorraine16Index•Clafoutis aux pommes3•Brownies21FicheFicheFichefiche-index Page 1",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        Assert.True(RagEndpoints.LooksLikeNavigationalChunk(index));

        var inlineIndex = index with
        {
            ChunkId = "inline-index",
            Text = "Index des recettesAAsperges vertes au miel, 16BBoulettes de viande hachee a la mozzarella, 30Brochettes de poisson mediterraneennes, 34 Rumsteck aux oignons grilles, 38 Quiche lorraine, 16 Brownies, 21",
            EmbedText = "Index des recettesAAsperges vertes au miel, 16BBoulettes de viande hachee a la mozzarella, 30Brochettes de poisson mediterraneennes, 34 Rumsteck aux oignons grilles, 38 Quiche lorraine, 16 Brownies, 21"
        };

        Assert.True(RagEndpoints.LooksLikeNavigationalChunk(inlineIndex));
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

        Assert.True(RagEndpoints.LooksLikeSourceListChunk(sourceList));
    }

    [Fact]
    public void CalibrateFusedMatches_promotes_structured_answer_unit_over_sources_and_indexes()
    {
        var sourceList = new RagMatch(
            1.02,
            "doc-source",
            "Cuisine/Sources.pdf",
            "Sources.pdf",
            52,
            55,
            "sources",
            63,
            "Ma boite http://maboite.qc.ca MAPAQ https://mapaq.gouv.qc.ca Maxi https://maxi.ca Metro https://metro.ca Naître et grandir https://naitreetgrandir.com/fr Sauce bechamel",
            1,
            "hash-source",
            "Ma boite http://maboite.qc.ca MAPAQ https://mapaq.gouv.qc.ca Maxi https://maxi.ca Metro https://metro.ca Naître et grandir https://naitreetgrandir.com/fr Sauce bechamel",
            "sparse_bm25_v1",
            1,
            1,
            "Sources",
            "Sources",
            "section_window_v1",
            null,
            null,
            null);
        var index = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/Index.pdf",
            "Index.pdf",
            7,
            7,
            "index",
            7,
            "Sauce bechamel 43 Sauce tomate 44 Sauce fromage 45 Sauce rosee 46 Sauce moutarde 47 Sauce pizza 48",
            1,
            "hash-index",
            "Sauce bechamel 43 Sauce tomate 44 Sauce fromage 45 Sauce rosee 46 Sauce moutarde 47 Sauce pizza 48",
            "sparse_bm25_v1",
            1,
            1,
            "Sommaire",
            "Sommaire",
            "unit_exact_v1",
            null,
            null,
            null);
        var recipe = new RagMatch(
            0.88,
            "doc-recipe",
            "Cuisine/Recipes.pdf",
            "Recipes.pdf",
            55,
            55,
            "recipe",
            131,
            "SAUCE BECHAMEL DE BASE Ingredients 45 ml de beurre 45 ml de farine 500 ml de lait Preparation 1. Faire fondre le beurre. Ajouter la farine puis le lait.",
            1,
            "hash-recipe",
            "SAUCE BECHAMEL DE BASE Ingredients 45 ml de beurre 45 ml de farine 500 ml de lait Preparation 1. Faire fondre le beurre. Ajouter la farine puis le lait.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauce bechamel",
            "Sauce bechamel",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Je veux une sauce bechamel", [sourceList, index, recipe]);

        Assert.True(RagEndpoints.LooksLikeStrongNavigationalChunk(index));
        Assert.Equal("recipe", calibrated[0].ChunkId);
        Assert.True(calibrated[0].Score > calibrated[1].Score);
        Assert.True(calibrated.Single(match => match.ChunkId == "index").Score <= 0.68);
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

        Assert.True(RagEndpoints.LooksLikeDocumentOverviewChunk(overview));
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

        Assert.False(RagEndpoints.LooksLikeDocumentOverviewChunk(profile));
    }

    [Fact]
    public void ExpandRetrievalQuery_only_adds_corpus_agnostic_surface_forms()
    {
        var mealPlanning = RagEndpoints.ExpandRetrievalQuery("Je veux organiser des repas pour toute la semaine", "cuisine");
        var accentFolded = RagEndpoints.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecôte ?", "cuisine");

        Assert.Equal("Je veux organiser des repas pour toute la semaine", mealPlanning);
        Assert.Contains("entrecote", accentFolded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steak", accentFolded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch cooking", accentFolded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_quoted_titles_with_short_words_and_digits()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Tu peux me faire une fiche claire pour « Sauce aux 4 fromages » : ingredients, etapes, temps et source ?");

        Assert.Contains("sauce aux 4 fromages", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_keeps_quoted_titles_for_exact_recipe_names()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Tu peux me faire une fiche claire pour « Asperges vertes au miel » : ingredients, etapes, temps et source ?");

        Assert.Contains("asperges vertes au miel", terms);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_ignores_comparison_quantity_words()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms(
            "Il y a plusieurs crèmes brûlées ? Compare-les si oui.");

        Assert.Contains("creme brulee", terms);
        Assert.DoesNotContain("plusieurs", terms);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_rewards_full_title_token_coverage_when_pdf_glues_words()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?");
        var target = "Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce. Salade de haricotsverts à l'avocat.";
        var partial = "Haricots verts vapeur avec huile d'olive, citron, sel et poivre.";

        var targetScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > partialScore);
        Assert.True(targetScore >= 8.0);
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_prefers_heading_over_measured_ingredient_occurrence()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour « Bouillon de volaille » : ingredients, etapes, temps et source ?");
        var heading = "INGRÉDIENTS: carcasse, oignons, céleri. BOUILLON DE VOLAILLE SAUCES PRÉPARATION: laisser mijoter 1h.";
        var ingredient = "Osso buco pour 4 personnes: 75 cl de bouillon de volaille, tomates, farine, vin blanc.";

        var headingScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, heading);
        var ingredientScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, ingredient);

        Assert.True(headingScore > ingredientScore);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_quoted_title_coverage_to_break_capped_score_ties()
    {
        var partial = new RagMatch(
            1.02,
            "doc-partial",
            "Cuisine/chefbot.pdf",
            "chefbot.pdf",
            112,
            112,
            "partial",
            171,
            "Haricots verts vapeur avec huile d'olive et citron.",
            IngestionVersion: 1,
            HashDoc: "a",
            EmbedText: "Haricots verts vapeur avec huile d'olive et citron.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Document",
            HeadingPath: "Document",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var target = partial with
        {
            DocId = "doc-target",
            DocPath = "Cuisine/si-on-cuisinait.pdf",
            DocName = "si-on-cuisinait.pdf",
            ChunkId = "target",
            ChunkIndex = 53,
            PageStart = 28,
            PageEnd = 28,
            Text = "Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce. Salade de haricotsverts à l'avocat.",
            EmbedText = "Document: si-on-cuisinait.pdf\nExcerpt: Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce."
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?",
            [partial, target]);

        Assert.Equal("target", calibrated[0].ChunkId);

        var selected = new List<RagMatch> { partial, target };
        RagEndpoints.PrioritizeQuotedTitleSelections(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("target", selected[0].ChunkId);
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

        RagEndpoints.PrioritizeQuotedTitleSelections("\"Sauce aux 4 fromages\"", selected);
        RagEndpoints.PrunePreciseTitleTailSelections("\"Sauce aux 4 fromages\"", selected);

        Assert.Single(selected);
        Assert.Equal("quoted-anchor", selected[0].ChunkId);
    }

    [Fact]
    public void ShouldSupplementSparseWithLexicalFallback_keeps_long_quoted_title_queries()
    {
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(
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
        Assert.False(RagEndpoints.ShouldSupplementSparseWithLexicalFallback("generic", query));
    }

    [Fact]
    public void CalibrateFusedMatches_boosts_serving_fit_chunks_with_multiple_specific_anchors()
    {
        var genericSauce = new RagMatch(
            0.98,
            "doc-generic",
            "Cuisine/Sauces.pdf",
            "Sauces.pdf",
            18,
            18,
            "generic",
            18,
            "SAUCE MOUTARDE Ingredients moutarde vinaigre huile sel poivre Realisation melanger dans un bol.",
            1,
            "hash-generic",
            "SAUCE MOUTARDE Ingredients moutarde vinaigre huile sel poivre Realisation melanger dans un bol.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauces",
            "Sauces",
            "section_window_v1",
            null,
            null,
            null);
        var steakSauce = new RagMatch(
            0.88,
            "doc-steak",
            "Cuisine/Robot.pdf",
            "Robot.pdf",
            121,
            121,
            "steak-sauce",
            306,
            "1 c. a c. de poivre concasse 10 cl de creme liquide. Ajoutez l'eau puis lancez la cuisson. Servez avec une entrecote. SAUCE AU POIVRE.",
            1,
            "hash-steak",
            "1 c. a c. de poivre concasse 10 cl de creme liquide. Ajoutez l'eau puis lancez la cuisson. Servez avec une entrecote. SAUCE AU POIVRE.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauces",
            "Sauces",
            "section_window_v1",
            null,
            null,
            null);
        var retrievalQuery = RagEndpoints.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecote ?", "cuisine");

        var calibrated = RagEndpoints.CalibrateFusedMatches(retrievalQuery, [genericSauce, steakSauce]);

        Assert.Equal("steak-sauce", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_breaks_capped_ties_with_specific_anchor_coverage()
    {
        var genericMarinade = new RagMatch(
            1.02,
            "doc-generic",
            "Cuisine/Marinades.pdf",
            "Marinades.pdf",
            46,
            46,
            "generic-marinade",
            46,
            "Marinade pour poulet avec mayonnaise, paprika, sel et poivre.",
            1,
            "hash-generic",
            "Marinade pour poulet avec mayonnaise, paprika, sel et poivre.",
            "sparse_bm25_v1",
            1,
            1,
            "Marinades",
            "Marinades",
            "unit_exact_v1",
            null,
            null,
            null);
        var steakSauce = genericMarinade with
        {
            DocId = "doc-steak",
            DocPath = "Cuisine/Robot.pdf",
            DocName = "Robot.pdf",
            ChunkId = "steak-sauce",
            ChunkIndex = 121,
            PageStart = 121,
            PageEnd = 121,
            Text = "Sauce au poivre avec creme, fond de veau et cognac. Servez avec une entrecote.",
            EmbedText = "Sauce au poivre avec creme, fond de veau et cognac. Servez avec une entrecote.",
            ChunkType = "section_window_v1"
        };
        var retrievalQuery = RagEndpoints.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecote ?", "cuisine");

        var calibrated = RagEndpoints.CalibrateFusedMatches(retrievalQuery, [genericMarinade, steakSauce]);

        Assert.Equal("steak-sauce", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_ignores_answer_format_words_for_specific_recipe_queries()
    {
        var genericOnionRecipe = new RagMatch(
            1.02,
            "doc-generic",
            "Cuisine/Other.pdf",
            "Other.pdf",
            83,
            83,
            "generic-onion",
            0,
            "Ingredients: oignons grelots, sucre, vinaigre. Preparation: cuire les oignons.",
            1,
            "hash-generic",
            "Matched profile title: Oignons caramelises\nIngredients: oignons grelots, sucre, vinaigre. Preparation: cuire les oignons.",
            "sparse_bm25_v1",
            1,
            1,
            "Oignons",
            "Oignons",
            "unit_exact_v1",
            null,
            null,
            null);
        var specificRecipe = new RagMatch(
            0.82,
            "doc-rumsteck",
            "Cuisine/Grill.pdf",
            "Grill.pdf",
            38,
            38,
            "rumsteck-oignons",
            0,
            "Ingredients: 1 gros oignon, paprika, farine, huile vegetale, 2 rumstecks. Preparation: peler les oignons et faire cuire les rumstecks.",
            1,
            "hash-rumsteck",
            "Matched profile title: Rumsteck aux oignons grilles\nIngredients: 1 gros oignon, paprika, farine, huile vegetale, 2 rumstecks. Preparation: peler les oignons et faire cuire les rumstecks.",
            "sparse_bm25_v1",
            1,
            1,
            "Rumsteck aux oignons",
            "Poissons et viandes > Rumsteck aux oignons",
            "unit_exact_v1",
            null,
            null,
            null);

        var query = "Tu peux me faire une fiche claire pour Rumsteck grille : details, etapes, temps et source ?";
        var tokens = RagEndpoints.ExtractLexicalQueryTokens(query);
        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [genericOnionRecipe, specificRecipe]);

        Assert.DoesNotContain("fiche", tokens);
        Assert.DoesNotContain("claire", tokens);
        Assert.DoesNotContain("etapes", tokens);
        Assert.DoesNotContain("source", tokens);
        Assert.Contains("rumsteck", tokens);
        Assert.True(RagEndpoints.HasProfileTitleHint(specificRecipe));
        Assert.True(RagEndpoints.HasProfileTitleHint(genericOnionRecipe));
        Assert.True(RagEndpoints.RequiresPrimarySpecificLexicalAnchor(tokens));
        Assert.True(RagEndpoints.ContainsPrimarySpecificLexicalAnchor(tokens, specificRecipe.EmbedText));
        Assert.False(RagEndpoints.ContainsPrimarySpecificLexicalAnchor(tokens, genericOnionRecipe.EmbedText));
        Assert.Equal("rumsteck-oignons", calibrated[0].ChunkId);
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

        Assert.True(RagEndpoints.ShouldSuppressUnanchoredSpecificResults("inertage", [denseNoise]));
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

        Assert.False(RagEndpoints.ShouldSuppressUnanchoredSpecificResults("inerting", [anchored]));
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

        Assert.False(RagEndpoints.ShouldSuppressUnanchoredSpecificResults("quiero una receta vegetariana facil", [denseCandidate]));
    }

    [Fact]
    public void IsNearDuplicatePageOverlap_detects_overlapping_contained_chunks()
    {
        var broader = new RagMatch(
            0.91,
            "doc-recipe",
            "Cuisine/Classic.pdf",
            "Classic.pdf",
            3,
            5,
            "broader",
            1,
            "Intro generale. Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            1,
            "hash",
            "Intro generale. Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            "sparse_bm25_v1",
            1,
            1,
            "Classiques",
            "Classiques",
            "section_window_v1",
            null,
            null,
            null);
        var contained = broader with
        {
            PageStart = 5,
            PageEnd = 5,
            ChunkId = "contained",
            Text = "Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux.",
            EmbedText = "Boeuf bourguignon pour quatre personnes avec boeuf, champignons, lardons, carottes, oignons, vin rouge, huile, ail, bouquet garni, sel et poivre. Degraisser la viande puis la tailler en morceaux."
        };

        Assert.True(RagEndpoints.IsNearDuplicatePageOverlap(broader, contained));
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

        Assert.True(RagEndpoints.LooksLikeNavigationalChunk(indexChunk));
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

        Assert.True(RagEndpoints.LooksLikeNavigationalChunk(indexChunk));
        Assert.True(RagEndpoints.LooksLikeStrongNavigationalChunk(indexChunk));
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

        Assert.False(RagEndpoints.LooksLikeNavigationalChunk(recipe));
    }

    [Fact]
    public void LooksLikeStructuredQuantityList_detects_bulleted_measurements()
    {
        var text = "Boeuf bourguignon Pour 4 personnes • 1,2 kg de boeuf • 250 g de champignons • 100 g de lardons • 1,5 l de vin rouge • 2 c. a soupe d'huile.";

        Assert.True(RagEndpoints.LooksLikeStructuredQuantityList(text));
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_dense_index_below_recipe_chunk()
    {
        var recipe = new RagMatch(
            0.88,
            "doc-recipe",
            "Cuisine/Classiques.pdf",
            "Classiques.pdf",
            5,
            5,
            "recipe",
            2,
            "Boeuf bourguignon Pour 4 personnes. Ingredients boeuf champignons lardons carottes oignons vin rouge ail bouquet garni. Preparation degraisser la viande puis faire revenir et mijoter.",
            1,
            "hash-recipe",
            "Boeuf bourguignon Pour 4 personnes. Ingredients boeuf champignons lardons carottes oignons vin rouge ail bouquet garni. Preparation degraisser la viande puis faire revenir et mijoter.",
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

        var calibrated = RagEndpoints.CalibrateFusedMatches("boeuf bourguignon grandes etapes", [indexChunk, recipe]);

        Assert.Equal("recipe", calibrated[0].ChunkId);
        Assert.True(calibrated.Single(match => match.ChunkId == "index").Score < 0.70);
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

        var suppressed = RagEndpoints.SuppressNavigationalNoise("boeuf bourguignon grandes etapes", [indexChunk, recipe]);

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

        var suppressed = RagEndpoints.SuppressNavigationalNoise("C'est quoi les grandes etapes du boeuf bourguignon ?", [indexChunk, recipe]);

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

        RagEndpoints.PruneNavigationalSelections("boeuf bourguignon grandes etapes", selected);

        Assert.Equal("recipe", Assert.Single(selected).ChunkId);
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

        var suppressed = RagEndpoints.SuppressNavigationalNoise(
            "fiche Asperges vertes au miel ingredients etapes temps source",
            [route, competingContent]);

        Assert.Contains(suppressed, match => match.ChunkId == "route");
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(route));
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

        var suppressed = RagEndpoints.SuppressNavigationalNoise(
            "Donne la procedure Alpha Beta.",
            [indexOnly]);

        Assert.Empty(suppressed);
    }

    [Fact]
    public void PruneNavigationalSelections_keeps_resolved_navigation_route_after_selection()
    {
        var route = TestMatch(
            text: "Alpha Beta Procedure. The destination page contains the procedure body but still has index-like extraction noise.",
            embedText: "Matched navigation_route: Alpha Beta Procedure\nAlpha Beta Procedure. The destination page contains the procedure body but still has index-like extraction noise.",
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

        RagEndpoints.PruneNavigationalSelections("Alpha Beta Procedure", selected);

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

        RagEndpoints.PruneNavigationalSelections("Alpha Beta Procedure", selected);

        Assert.DoesNotContain(selected, match => match.ChunkId == "route");
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

        var suppressed = RagEndpoints.SuppressNavigationalNoise("Quels documents et quelles sources vois-tu dans le corpus ?", [indexChunk, overview]);

        Assert.Equal(1.02, suppressed[0].Score);
        Assert.Equal("index", suppressed[0].ChunkId);
    }

    [Fact]
    public void ContainsOrderedPhraseWindow_accepts_short_title_gaps_but_rejects_loose_topic_matches()
    {
        Assert.True(RagEndpoints.ContainsOrderedPhraseWindow(
            "© Test\nSalade \nde lentilles\nIngrédients...",
            "salade lentilles",
            maxGapChars: 40));

        Assert.False(RagEndpoints.ContainsOrderedPhraseWindow(
            "Cette salade peut être adaptée avec tomates, œufs, tofu, légumineuses et même des lentilles selon ce que vous avez.",
            "salade lentilles",
            maxGapChars: 40));
    }

    [Fact]
    public void CalibrateFusedMatches_prioritizes_exact_title_candidate_over_loose_term_overlap()
    {
        var looseOverlap = new RagMatch(
            1.02,
            "doc-loose",
            "Cuisine/International.pdf",
            "International.pdf",
            135,
            135,
            "loose",
            1,
            "Falafels servis avec une sauce concombre et quelques feuilles de laitue romaine.",
            1,
            "hash-loose",
            "Falafels servis avec une sauce concombre et quelques feuilles de laitue romaine.",
            "sparse_bm25_v1",
            1,
            1,
            "Falafels",
            "Falafels",
            "section_window_v1",
            null,
            null,
            null);
        var exactTitle = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            33,
            33,
            "exact-title",
            2,
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

        var calibrated = RagEndpoints.CalibrateFusedMatches("Concombres a la romaine", [looseOverlap, exactTitle]);
        var filtered = RagEndpoints.SuppressNavigationalNoise("Concombres a la romaine", calibrated);

        Assert.Equal("exact-title", calibrated[0].ChunkId);
        Assert.Equal("exact-title", filtered[0].ChunkId);
        Assert.True(RagEndpoints.ComputeExactTitleCandidateScore("Concombres a la romaine", exactTitle)
            > RagEndpoints.ComputeExactTitleCandidateScore("Concombres a la romaine", looseOverlap));
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

        RagEndpoints.PruneWeakTitleExpansionSelections("Concombres a la romaine", selected);

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

        RagEndpoints.PruneWeakTitleExpansionSelections("Concombres a la romaine", selected);

        Assert.Equal(2, selected.Count);
        Assert.Equal("exact-title", selected[0].ChunkId);
        Assert.Equal("continuation", selected[1].ChunkId);
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

        RagEndpoints.PruneWeakAdjacentSiblingSelections("Concombres a la romaine", selected);

        Assert.Single(selected);
        Assert.Equal("anchor", selected[0].ChunkId);
    }

    [Fact]
    public void ShouldConstrainPreciseTitleLookup_detects_title_like_queries_but_not_explanatory_requests()
    {
        Assert.True(RagEndpoints.ShouldConstrainPreciseTitleLookup("Concombres a la romaine"));
        Assert.True(RagEndpoints.ShouldConstrainPreciseTitleLookup("je veux une recette de concombres romaine"));

        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("comment preparer des concombres pour la semaine"));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("compare concombres romaine et tomates printanieres"));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("Quelles recettes sont les plus adaptées pour un déjeuner de semaine rapide ?"));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("Quel dessert français choisir pour un repas chic ?"));
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

        RagEndpoints.PrunePreciseTitleTailSelections("Concombres a la romaine", selected);

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

        RagEndpoints.PrunePreciseTitleTailSelections("sauce bearnaise", selected);

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

        var titleScore = RagEndpoints.ComputeExactTitleCandidateScore("XR 200 fieldbus commissioning", titleCandidate);
        var looseScore = RagEndpoints.ComputeExactTitleCandidateScore("XR 200 fieldbus commissioning", looseCandidate);

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

        RagEndpoints.PruneWeakTitleExpansionSelections("Churros sauce chocolat ingredients", selected);
        RagEndpoints.PruneWeakAdjacentSiblingSelections("Churros sauce chocolat ingredients", selected);
        RagEndpoints.PrunePreciseTitleTailSelections("Churros sauce chocolat ingredients", selected);

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

        RagEndpoints.PruneWeakTitleExpansionSelections("gratin dauphinois", selected);
        RagEndpoints.PruneWeakAdjacentSiblingSelections("gratin dauphinois", selected);
        RagEndpoints.PrunePreciseTitleTailSelections("gratin dauphinois", selected);

        var remaining = Assert.Single(selected);
        Assert.Equal("anchor", remaining.ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_dense_noise_when_lexical_anchor_exists()
    {
        var sparseAnchor = new RagMatch(0.91, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-anchor", 0, "Inerting prevents explosion by controlling oxygen concentration and purge conditions.", 1, "hash-a", "Inerting prevents explosion by controlling oxygen concentration and purge conditions.", "sparse_bm25_v1", 1, 1, "Inerting", "Inerting", "unit_exact_v1", null, null, null);
        var denseNoise = new RagMatch(0.98, "doc-b", "Kitchen/Menu.pdf", "Menu.pdf", 1, 1, "dense-noise", 0, "Balanced meals, vegetables and weekly menu planning.", 1, "hash-b", "Balanced meals, vegetables and weekly menu planning.", "contextual_text_v1", 1, 1, "Meals", "Meals", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Quels documents parlent d inerting explosion oxygen ?", [denseNoise, sparseAnchor]);

        Assert.Equal("sparse-anchor", calibrated[0].ChunkId);
        Assert.Equal("sparse_bm25_v1", calibrated[0].EmbeddingBasis);
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

        var hints = RagEndpoints.BuildSelectionHints(match, quality);

        Assert.Equal("low_confidence", hints.EvidenceRole);
        Assert.True(hints.QualityPenalty >= 10);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_exact_reference_above_dense_for_reference_query()
    {
        var exact = new RagMatch(0.96, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "exact-1", -1, "EN 15281", 1, "hash-a", "EN 15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var dense = new RagMatch(0.98, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", 3, 3, "dense-1", 1, "Maintenance guidance around EN 15281", 1, "hash-a", "Maintenance guidance around EN 15281", "contextual_text_v1", 1, 1, "Maintenance", "Chapter 3 > Maintenance", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Ou trouve-t-on EN 15281 ?", [dense, exact]);

        Assert.Equal("exact-1", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_numeric_exact_reference_above_unrelated_dense_noise()
    {
        var exact = new RagMatch(0.965, "doc-a", "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf", "CEN TR 15281 2006 Guidance on inerting.pdf", null, null, "exact-15281", -1, "15281", 1, "hash-a", "15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var denseNoise = new RagMatch(0.985, "doc-b", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 2, 2, "dense-15281-noise", 0, "general maintenance guidance around standard references", 1, "hash-b", "general maintenance guidance around standard references", "contextual_text_v1", 1, 1, "Integration", "Chapter 2 > Integration", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Ou trouve-t-on 15281 ?", [denseNoise, exact]);

        Assert.Equal("exact-15281", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
        Assert.Contains("15281", $"{calibrated[0].DocName} {calibrated[0].DocPath}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_sparse_noise_when_lexical_overlap_is_low()
    {
        var sparseNoise = new RagMatch(0.79, "doc-a", "ATEX/Appendix.pdf", "Appendix.pdf", 1, 1, "sparse-noise", 0, "general safety appendix summary", 1, "hash-a", "general safety appendix summary", "sparse_bm25_v1", 1, 1, "Appendix", "Appendix > Summary", "unit_exact_v1", null, null, null);
        var denseRelevant = new RagMatch(0.77, "doc-b", "Maintenance/Consignation.pdf", "Consignation.pdf", 2, 2, "dense-relevant", 0, "procedure de consignation electrique et verrouillage", 1, "hash-b", "procedure de consignation electrique et verrouillage", "contextual_text_v1", 1, 1, "Procedure", "Chapter 2 > Procedure", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Resume la procedure de consignation electrique.", [sparseNoise, denseRelevant]);

        Assert.Equal("dense-relevant", calibrated[0].ChunkId);
        Assert.Equal("contextual_text_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void ExtractLexicalQueryTokens_ignores_ui_noise_tokens_but_keeps_business_words()
    {
        var tokens = RagEndpoints.ExtractLexicalQueryTokens("stp je cherche le pdf inerting safety controls manual");

        Assert.Contains("inerting", tokens);
        Assert.Contains("safety", tokens);
        Assert.Contains("controls", tokens);
        Assert.DoesNotContain("pdf", tokens);
        Assert.DoesNotContain("stp", tokens);
        Assert.DoesNotContain("manual", tokens);
    }

    [Fact]
    public void BuildLexicalContentFallbackTerms_ignores_advice_filler_but_keeps_subject_terms()
    {
        var terms = RagEndpoints.BuildLexicalContentFallbackTerms("Quelle sauce irait bien avec une entrecote ?");

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

        var reranked = RagEndpoints.RerankDenseMatches([broad, precise]);

        Assert.Equal("b", reranked[0].ChunkId);
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

        RagEndpoints.ApplyAutocut(matches, absoluteMinScore: 0.25);

        Assert.Equal(5, matches.Count);
    }

    [Theory]
    [InlineData("same_section", 0.88)]
    [InlineData("next", 0.865)]
    [InlineData("prev", 0.86)]
    public void ComputeLinkedMatchScore_applies_expected_penalty(string linkType, double expected)
    {
        var score = RagEndpoints.ComputeLinkedMatchScore(0.90, linkType);

        Assert.Equal(expected, score, 3);
    }

    [Fact]
    public void ComputeLinkedMatchScore_applies_extra_penalty_when_expanding_from_linked_context()
    {
        var fromDense = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromLinked = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromLinked);
        Assert.Equal(0.865, fromLinked, 3);
    }

    [Fact]
    public void NormalizeSparseScore_returns_stable_monotonic_values()
    {
        Assert.Equal(0.0, RagEndpoints.NormalizeSparseScore(0.0));

        var lower = RagEndpoints.NormalizeSparseScore(0.01);
        var higher = RagEndpoints.NormalizeSparseScore(0.25);

        Assert.InRange(lower, 0.45, 0.92);
        Assert.InRange(higher, 0.45, 0.92);
        Assert.True(higher > lower);
    }

    [Fact]
    public void ResolveProvenance_marks_sparse_matches_explicitly()
    {
        var sparse = new RagMatch(0.72, "doc", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "chunk", 0, "inerting guidance", 1, "hash", "Document: CEN.pdf", "sparse_bm25_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        Assert.Equal("retriever:sparse_bm25", RagEndpoints.ResolveProvenance(sparse));
        Assert.Equal("sparse_bm25", RagEndpoints.BuildProvenanceInfo(sparse).Channel);
    }

    [Fact]
    public void ApplyRerankScores_promotes_highest_reranked_candidate_and_sets_rerank_score()
    {
        var first = new RagMatch(0.90, "doc-a", "ATEX/A.pdf", "A.pdf", 1, 1, "a", 0, "alpha", 1, "hash-a", "alpha", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var second = new RagMatch(0.70, "doc-b", "ATEX/B.pdf", "B.pdf", 1, 1, "b", 0, "beta", 1, "hash-b", "beta", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var third = new RagMatch(0.60, "doc-c", "ATEX/C.pdf", "C.pdf", 1, 1, "c", 0, "gamma", 1, "hash-c", "gamma", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        var reranked = RagEndpoints.ApplyRerankScores(
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
        Assert.Equal(0.0, RagEndpoints.NormalizeRerankScore(0.25, 0.25, 0.75), 3);
        Assert.Equal(1.0, RagEndpoints.NormalizeRerankScore(0.75, 0.25, 0.75), 3);
        Assert.Equal(0.5, RagEndpoints.NormalizeRerankScore(0.50, 0.25, 0.75), 3);
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
        var fromDense = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromExact = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "exact_match");
        var fromSparse = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "sparse_bm25");
        var fromLinked = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromSparse);
        Assert.True(fromSparse > fromExact);
        Assert.True(fromDense > fromExact);
        Assert.True(fromExact > fromLinked);
        Assert.Equal(0.875, fromSparse, 3);
        Assert.Equal(0.87, fromExact, 3);
    }

    [Fact]
    public void ComputeExactMatchScore_prioritizes_structured_references_over_plain_verbatim()
    {
        var standard = RagEndpoints.ComputeExactMatchScore("standard_ref", "en 15281", "EN 15281");
        var code = RagEndpoints.ComputeExactMatchScore("code_ref", "ind570", "IND570");
        var verbatim = RagEndpoints.ComputeExactMatchScore("verbatim_excerpt", "maintenance", "maintenance procedure");

        Assert.True(standard > code);
        Assert.True(code > verbatim);
    }

    [Fact]
    public void ComputeMetadataReferenceScore_prefers_direct_term_overlap_over_numeric_key_overlap()
    {
        var exactReference = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 1,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var keyBacked = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var numericOnly = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 0,
            genericDirectMatches: 0,
            keyMatches: 1);

        Assert.True(exactReference > keyBacked);
        Assert.True(keyBacked > numericOnly);
        Assert.True(numericOnly >= 0.95);
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

        Assert.True(RagEndpoints.ShouldShortCircuitAfterExact([exact]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_search_open_when_exact_results_are_ambiguous()
    {
        var top = new RagMatch(0.98, "doc-1", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "docmeta:1", -1, "CEN TR 15281 2006.pdf", 1, "hash1", "CEN TR 15281 2006.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var second = new RagMatch(0.955, "doc-2", "ATEX/Other 15281.pdf", "Other 15281.pdf", null, null, "docmeta:2", -1, "Other 15281.pdf", 1, "hash2", "Other 15281.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);

        Assert.False(RagEndpoints.ShouldShortCircuitAfterExact([top, second]));
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterExact([index]));
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

        var leftHash = RagEndpoints.ComputeDataHash(left);
        var rightHash = RagEndpoints.ComputeDataHash(right);
        var changedHash = RagEndpoints.ComputeDataHash(changed);

        Assert.Equal(leftHash, rightHash);
        Assert.NotEqual(leftHash, changedHash);
    }

    [Fact]
    public void ResolveProvenance_returns_explicit_retriever_prefixed_label()
    {
        var match = new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null);

        var provenance = RagEndpoints.ResolveProvenance(match);

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

        var info = RagEndpoints.BuildProvenanceInfo(match);

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

        var info = RagEndpoints.BuildProvenanceInfo(match, sourceHash: null, allowLegacyHashFallback: false);

        Assert.Null(info.SourceHash);
    }

    [Fact]
    public void ResolveDocumentSourceHash_uses_doc_path_when_doc_id_is_not_usable()
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
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Knowledge/manual.pdf"] = "revision-aware-hash"
        };

        var resolved = RagEndpoints.ResolveDocumentSourceHash(match, hashes);

        Assert.Equal("revision-aware-hash", resolved);
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
        var matched = RagEndpoints.ComputeHypQuestionsMatched(
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
        var matched = RagEndpoints.ComputeHypQuestionsMatched(
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

        var matched = RagEndpoints.ResolveHypQuestionsMatched(
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

        var matched = RagEndpoints.ResolveHypQuestionsMatched(
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

        var matched = RagEndpoints.ResolveHypQuestionsMatched(
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            byDocPath);

        Assert.Null(matched);
    }

    [Fact]
    public void BuildDocumentCategoryPath_and_category_are_derived_from_doc_path()
    {
        Assert.Equal("ATEX/Guidance", RagEndpoints.BuildDocumentCategoryPath("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Equal("atex", RagEndpoints.BuildDocumentCategory("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Null(RagEndpoints.BuildDocumentCategoryPath("root-level.pdf"));
        Assert.Null(RagEndpoints.BuildDocumentCategory("RootLevel"));
    }

    [Fact]
    public void ResolveCategoryRef_uses_top_level_category_path()
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATEX"] = "cat_001",
            ["Programmation"] = "cat_002"
        };

        Assert.Equal("cat_001", RagEndpoints.ResolveCategoryRef("ATEX/Guidance", refs));
        Assert.Equal("cat_002", RagEndpoints.ResolveCategoryRef("Programmation/Mettler", refs));
        Assert.Null(RagEndpoints.ResolveCategoryRef("General", refs));
        Assert.Null(RagEndpoints.ResolveCategoryRef(null, refs));
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
            "same-1");

        var context = RagEndpoints.BuildContextInfo(match);

        Assert.Equal("unit_exact_v1", context.ChunkType);
        Assert.Equal("Safety", context.SectionTitle);
        Assert.Equal("Chapter 1 > Safety", context.HeadingPath);
        Assert.Equal("prev-1", context.PrevChunkId);
        Assert.Equal("next-1", context.NextChunkId);
        Assert.Equal("same-1", context.SameSectionChunkId);
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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("raclette suisse", selected);

        Assert.Empty(selected);
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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("gratin dauphinois", selected);

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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("fiche mixxing valve", selected);

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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("one pot pasta brocoli dinde bacon", selected);

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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("SAUCE AUX 4 FROMAGES", selected);

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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections("quelle sauce irait bien avec entrecote", selected);

        Assert.Single(selected);
    }

    [Fact]
    public void BuildAnswerGuidance_marks_empty_retrieval_as_no_source_match()
    {
        var guidance = RagEndpoints.BuildAnswerGuidance("raclette suisse", Array.Empty<RagMatch>());

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("no_relevant_source_found", guidance.Reason);
        Assert.Equal("no_source_match", guidance.ResponseShape);
    }

    [Fact]
    public void BuildAnswerGuidance_adds_caveat_when_selected_sources_have_low_extraction_quality()
    {
        var match = TestMatch(
            text: "Procedure: verify the scanned label before using the recorded pressure value.",
            docPath: "Scans/Archive.pdf",
            page: 3);
        var quality = new RagItemExtractionQualityDto(
            PageQualityStatus: "manual_review_low_text",
            PageExtractionConfidence: 0.32,
            PageManualReviewRecommended: true,
            TextStatus: "low_confidence");
        var qualityByMatch = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase)
        {
            [RagEndpoints.BuildExtractionQualityMatchKey(match)] = quality
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(
            "Que dit ce document sur la procedure ?",
            [match],
            qualityByMatch);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("source_quality_requires_manual_review_caveat", guidance.Reason);
        Assert.NotNull(guidance.QualificationNote);
        Assert.Contains("OCR", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
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

        var fullRecipeScore = RagEndpoints.ComputeExactTitleCandidateScore("gratin dauphinois", fullRecipe);
        var recipeTailScore = RagEndpoints.ComputeExactTitleCandidateScore("gratin dauphinois", recipeTail);

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

        RagEndpoints.PrioritizeExactTitleSelections("gratin dauphinois", selected);

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

        RagEndpoints.PrioritizeExactTitleSelections("alpha beta", selected);

        Assert.Equal("exact-phrase", selected[0].ChunkId);
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

        Assert.True(RagEndpoints.HasProfileTitleHint(routeMatch));
        Assert.True(RagEndpoints.ComputeExactTitleCandidateScore("beta checklist", routeMatch)
            > RagEndpoints.ComputeExactTitleCandidateScore("beta checklist", looseOverlap));
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_route_exact_label_over_loose_overlap()
    {
        var looseOverlap = TestMatch(
            text: "The beta appendix references alpha separately without a combined title.",
            embedText: "The beta appendix references alpha separately without a combined title.",
            chunkId: "loose-overlap",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "section_window_v1",
            score: 1.02);
        var navigationRoute = TestMatch(
            text: "Alpha Beta. This page has the operational details and confirms the title from navigation.",
            embedText: "Matched navigation_route: Alpha Beta\nAlpha Beta. This page has the operational details and confirms the title from navigation.",
            chunkId: "navigation-route",
            embeddingBasis: "navigation_route_v1",
            chunkType: "section_window_v1",
            score: 0.86);

        var calibrated = RagEndpoints.CalibrateFusedMatches("alpha beta", [looseOverlap, navigationRoute]);

        Assert.Equal("navigation-route", calibrated[0].ChunkId);
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

        Assert.False(RagEndpoints.HasProfileTitleHint(unconfirmedNavigationRoute));
        Assert.True(RagEndpoints.HasProfileTitleHint(confirmedNavigationRoute));
        Assert.Equal(0.0, RagEndpoints.ComputeExactTitleCandidateScore("alpha beta", unconfirmedNavigationRoute));
        Assert.True(RagEndpoints.ComputeExactTitleCandidateScore("alpha beta", confirmedNavigationRoute) > 0.0);
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
        Assert.Equal(expected, RagEndpoints.ResolveDirectTitleTokenRouteMinimumOverlap(tokenCount));
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_title_token_route_over_partial_sparse_overlap()
    {
        var partialSparse = TestMatch(
            text: "Broccoli noodles with a bacon garnish and unrelated notes.",
            embedText: "Broccoli noodles with a bacon garnish and unrelated notes.",
            page: 8,
            chunkId: "partial-sparse",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.96);
        var directRoute = TestMatch(
            text: "ONE POT PASTA BROCOLI DINDE ET BACONPLATS PRINCIPAUX. Ingredients: pasta, broccoli, cooked turkey and bacon.",
            embedText: "Matched direct_title_token_route: ONE POT PASTA BROCOLI DINDE ET BACON\nONE POT PASTA BROCOLI DINDE ET BACONPLATS PRINCIPAUX. Ingredients: pasta, broccoli, cooked turkey and bacon.",
            page: 44,
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.88);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "one pot pasta brocoli dinde bacon",
            [partialSparse, directRoute]);

        Assert.Equal("direct-route", calibrated[0].ChunkId);
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(calibrated[0]));
    }

    [Fact]
    public void ResolveRetriever_maps_fuzzy_title_lead_separately_from_dense_qdrant()
    {
        var fuzzy = TestMatch(
            text: "Alpha beta target body.",
            embedText: "Matched fuzzy_title_lead: Alpha Beta\nAlpha beta target body.",
            embeddingBasis: "fuzzy_title_lead_v1",
            chunkType: "section_window_v1");

        Assert.Equal("fuzzy_title_lead", RagEndpoints.ResolveRetriever(fuzzy));
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(fuzzy));
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_document_hint_match_for_homonymous_titles()
    {
        var otherBook = TestMatch(
            text: "Coq au vin ingredients and steps from a generic recipe book.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "other-book",
            embeddingBasis: "dense_qdrant_v1",
            score: 0.72);
        var hintedBook = TestMatch(
            text: "Coq au vin ingredients and steps from the international collection.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            chunkId: "hinted-book",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.62);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "coq au vin",
            [otherBook, hintedBook],
            "Donne-moi la recette du coq au vin dans le livre international.");

        Assert.Equal("hinted-book", calibrated[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prioritizes_resolved_title_routes_before_neighboring_sparse_chunks()
    {
        var sparseChunk = TestMatch(
            text: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            embedText: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            chunkId: "sparse-neighbor",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var titleRoute = TestMatch(
            text: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            embedText: "Matched title_anchor_route: Green asparagus with honey\nIngredients and steps mention asparagus, honey and walnuts.",
            chunkId: "title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91);

        var ordered = RagEndpoints.OrderMatchesForSelection([sparseChunk, titleRoute], prioritizeDocumentProfiles: false);

        Assert.Equal("title-route", ordered[0].ChunkId);
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(ordered[0]));
    }

    [Fact]
    public void OrderMatchesForSelection_keeps_resolved_routes_first_when_document_diversity_is_active()
    {
        var sparseChunk = TestMatch(
            text: "A different page in the same document mentions honey but not the requested title.",
            embedText: "A different page in the same document mentions honey but not the requested title.",
            chunkId: "sparse-same-doc",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var titleRoute = TestMatch(
            text: "The routed page contains the concrete procedure.",
            embedText: "Matched title_anchor_route: Green asparagus with honey\nThe routed page contains the concrete procedure.",
            chunkId: "title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91);

        var ordered = RagEndpoints.OrderMatchesForSelection([sparseChunk, titleRoute], prioritizeDocumentProfiles: true);

        Assert.Equal("title-route", ordered[0].ChunkId);
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

        RagEndpoints.PruneUnpagedProfileSelectionsForPreciseLookup("alpha beta", selected);

        Assert.Single(selected);
        Assert.Equal("paged-title", selected[0].ChunkId);
    }

    [Fact]
    public void ExtractMatchedDocHints_derives_generic_reference_and_alpha_hints()
    {
        var matches = new[]
        {
            new RagMatch(0.9, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 1, "chunk-1", 0, "functional safety handbook", 1, "hash-1", "functional safety handbook", "exact_match_v1", 1, 1, "Safety", "Safety", "document_metadata_ref", null, null, null),
            new RagMatch(0.9, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 1, 1, "chunk-2", 0, "fieldbus integration guide", 1, "hash-2", "fieldbus integration guide", "exact_match_v1", 1, 1, "PLC", "PLC", "document_metadata_ref", null, null, null),
            new RagMatch(0.7, "doc-3", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 1, 1, "chunk-3", 0, "agreement document", 1, "hash-3", "agreement document", "contextual_text_v1", 1, 1, "General", "General", "unit_exact_v1", null, null, null),
            new RagMatch(0.7, "doc-4", "Safety/Prevention.pdf", "Prevention.pdf", 1, 1, "chunk-4", 0, "prevention document", 1, "hash-4", "prevention document", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null)
        };

        var hints = RagEndpoints.ExtractMatchedDocHints(matches);

        Assert.Contains("61511", hints);
        Assert.Contains("XR200", hints);
        Assert.Contains("ACCORD", hints);
        Assert.Contains("PREVENTION", hints);
        Assert.DoesNotContain("GUIDE", hints);
    }

    [Fact]
    public void BuildAnswerGuidance_uses_generic_domain_context_for_qualification()
    {
        var matches = new[]
        {
            new RagMatch(0.92, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 2, "chunk-1", 0, "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", 1, "hash-1", "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", "contextual_text_v1", 1, 1, "Process safety", "Process safety", "unit_exact_v1", null, null, null),
            new RagMatch(0.89, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 3, 4, "chunk-2", 1, "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", 1, "hash-2", "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", "contextual_text_v1", 2, 2, "PLC integration", "PLC integration", "unit_exact_v1", null, null, null)
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(
            "J'ai une discussion avec un client qui me demande si notre projet est conforme a cette norme, tu peux m'aider a repondre ?",
            matches);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.NotNull(guidance.QualificationNote);
        Assert.Contains("61511", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XR200", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("perimetre", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contexte projet", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnswerGuidance_keeps_high_impact_caveats_generic_without_corpus_terms()
    {
        var matches = new[]
        {
            TestMatch(
                text: "Privacy policy retention notes mention customer data handling and audit history.",
                docPath: "Policies/Privacy policy.pdf")
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(
            "Un client demande si on peut garantir legalement que cette option est conforme, tu repondrais quoi ?",
            matches);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("high_impact_or_safety_answer_requires_qualification", guidance.Reason);
    }

    [Fact]
    public void BuildAnswerGuidance_does_not_treat_domain_incident_words_as_high_impact_by_themselves()
    {
        var matches = new[]
        {
            TestMatch(
                text: "The historical incident report mentions an explosion during an old production event.",
                docPath: "Archive/Incident history.pdf")
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(
            "Le document mentionne une explosion dans l'historique ?",
            matches);

        Assert.Equal("answer", guidance.Behavior);
        Assert.Equal("documented_question_with_relevant_sources", guidance.Reason);
    }

    private static RagMatch TestMatch(
        string text,
        string? embedText = null,
        string docPath = "Cuisine/Test.pdf",
        int page = 1,
        string chunkId = "chunk-1",
        string embeddingBasis = "sparse_bm25_v1",
        string chunkType = "unit_exact_v1",
        double score = 0.93)
        => new(
            Score: score,
            DocId: "doc-1",
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: page,
            PageEnd: page,
            ChunkId: chunkId,
            ChunkIndex: page,
            Text: text,
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: embedText ?? text,
            EmbeddingBasis: embeddingBasis,
            SectionOrdinal: 1,
            UnitOrdinal: page,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

}
