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
    public void BuildComparativeOperandProfileBackfillQueries_extracts_bare_vs_topic_operands()
    {
        var queries = RetrievalQueryPolicy.BuildComparativeOperandProfileBackfillQueries(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?");

        Assert.Contains("hazardous locations", queries);
        Assert.Contains("machines electriques", queries);
        Assert.Contains("machinery electrical", queries);
    }

    [Fact]
    public void ShouldRunComparativeOperandProfileBackfill_runs_when_title_operand_queries_are_available_but_not_covered()
    {
        var selected = new[]
        {
            TestMatch(
                "Hazardous locations profile summary.",
                chunkId: "hazardous-profile",
                embeddingBasis: "document_profile_v1",
                score: 0.92)
        };

        Assert.True(RetrievalQueryPolicy.ShouldRunComparativeOperandProfileBackfill(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            selected));
    }

    [Fact]
    public void ShouldAllowLexicalProfileEmptyRecovery_blocks_explicit_filename_surface()
    {
        Assert.False(RetrievalLexicalExpansion.ShouldAllowLexicalProfileEmptyRecovery(
            "Peux-tu me resumer `document_inexistant.pdf` et l'utiliser comme source principale ?",
            hasExplicitFileDocumentHint: true));

        Assert.True(RetrievalLexicalExpansion.ShouldAllowLexicalProfileEmptyRecovery(
            "Retrouve les documents qui parlent de clause de renouvellement.",
            hasExplicitFileDocumentHint: false));
    }

    [Fact]
    public void ExtractCompactDocumentCodeProfileRecoveryPhrases_merges_comparison_operands()
    {
        var phrases = RetrievalLexicalExpansion.ExtractCompactDocumentCodeProfileRecoveryPhrases(
            "Compare les annexes IPCC WG3 et le rapport SDG : quelles informations sont chiffrees ?");

        Assert.Contains("wg3", phrases);
        Assert.Contains("sdg", phrases);
    }

    [Fact]
    public void Profile_title_only_sparse_match_recovers_profile_only_when_page_does_not_carry_exact_title()
    {
        var profileOnly = TestMatch(
            text: "Ordinary operational page text without the distinctive enrichment phrase.",
            embedText: "Matched profile title: Pressure envelope validation\nOrdinary operational page text without the distinctive enrichment phrase.");
        var sourceBackedPage = TestMatch(
            text: "Pressure envelope validation is performed before the isolation valve is opened.",
            embedText: "Matched profile title: Pressure envelope validation\nPressure envelope validation is performed before the isolation valve is opened.");
        var differentTitle = TestMatch(
            text: "Ordinary operational page text.",
            embedText: "Matched profile title: Maintenance governance\nOrdinary operational page text.");

        Assert.True(RetrievalCandidateSignals.ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(
            "pressure envelope validation",
            profileOnly));
        Assert.False(RetrievalCandidateSignals.ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(
            "pressure envelope validation",
            sourceBackedPage));
        Assert.False(RetrievalCandidateSignals.ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(
            "pressure envelope validation",
            differentTitle));
    }

    [Fact]
    public void ComputeDocumentProfileSearchResultLimit_overfetches_for_generic_profile_reranking()
    {
        Assert.Equal(32, RetrievalChannels.ComputeDocumentProfileSearchResultLimit(8));
        Assert.Equal(80, RetrievalChannels.ComputeDocumentProfileSearchResultLimit(64));
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_restores_profile_constraints_filtered_from_chunk_terms()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";

        var chunkTerms = RetrievalLexicalExpansion.BuildLexicalContentFallbackTerms(query);
        var profileTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(query);

        Assert.DoesNotContain("material", chunkTerms);
        Assert.Contains("material", profileTerms);
        Assert.Contains("apprentices", profileTerms);
        Assert.Contains("mat\u00e9riel", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Compare trois procedures pour apprentis : mat\u00e9riel, risques et consignes."));
        Assert.Contains("etudiant", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Compare les procedures \u00e9tudiantes et les options de travail."));
        Assert.Contains("francais", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Compare les procedures scandinaves, fran\u00e7aises et espagnoles."));
        Assert.Contains("cremeux", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Compare les options cr\u00e9meuses et simples."));
        Assert.Contains("enfants", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Une procedure enfant."));
        Assert.Contains("children", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Une procedure enfant."));
        Assert.DoesNotContain("procedures", RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "Compare trois procedures pour enfants : temps, materiel, risque de ratage."));
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_adds_generic_cross_language_technical_variants()
    {
        var specificityTokens = RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(
            "exigence electrique issue d un scan avec confiance");
        var terms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "exigence electrique issue d un scan");

        Assert.Contains("electrique", specificityTokens);
        Assert.DoesNotContain("scan", specificityTokens);
        Assert.DoesNotContain("confiance", specificityTokens);
        Assert.Contains("electrical", terms);
        Assert.Contains("requirement", terms);

        var safetyTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "couleur ou pictogramme de securite");
        Assert.Contains("color", safetyTerms);
        Assert.Contains("symbol", safetyTerms);

        var technicalTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "certificats, fiches techniques et modes d emploi pour le modele HPX-2000");
        Assert.Contains("certificate", technicalTerms);
        Assert.Contains("datasheet", technicalTerms);
        Assert.Contains("operating instructions", technicalTerms);
        Assert.Contains("hpx2000", technicalTerms);

        var complianceTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "declaration de conformite ATEX et norme IEC 60079");
        Assert.Contains("conformity", complianceTerms);
        Assert.Contains("compliance", complianceTerms);
        Assert.Contains("standard", complianceTerms);

        var cleanroomTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "salle propre vs documentation technique");
        Assert.Contains("cleanroom", cleanroomTerms);
        Assert.Contains("clean room", cleanroomTerms);

        var safetyDataSheetTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "safety data sheet handling precautions");
        Assert.Contains("msds", safetyDataSheetTerms);
        Assert.Contains("sds", safetyDataSheetTerms);
        Assert.Contains("material safety data sheet", safetyDataSheetTerms);

        var versionTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "compare ancienne version et version courante du certificat");
        Assert.Contains("old version", versionTerms);
        Assert.Contains("current version", versionTerms);
        Assert.Contains("certificate", versionTerms);
    }

    [Fact]
    public void ResolveDocumentProfileCandidateCount_keeps_profile_window_bounded()
    {
        Assert.Equal(12, RetrievalQueryPolicy.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: true));
        Assert.Equal(12, RetrievalQueryPolicy.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: false));
        Assert.Equal(10, RetrievalQueryPolicy.ResolveDocumentProfileCandidateCount(candidates: 10, topK: 8, profileOnly: true));
        Assert.Equal(28, RetrievalQueryPolicy.ResolveDocumentProfileCandidateCount(
            candidates: 210,
            topK: 14,
            profileOnly: false,
            comparativeProfileAssist: true));
    }

    [Fact]
    public void ShouldAllowSparseAssistForScopedProfileFallback_detects_broad_queries_with_strong_constraints()
    {
        Assert.True(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Compare trois procedures pour apprentis : materiel, risques et consignes."));
        Assert.True(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Quel dessert francais choisir pour un repas chic ?"));
        Assert.False(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Fais une vue d'ensemble des documents disponibles."));
        Assert.False(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Je veux un dossier avec options adaptees pour 15 personnes."));
    }

    [Fact]
    public void ShouldRunComparativeOperandProfileBackfill_runs_when_operand_titles_are_not_covered()
    {
        const string query = "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?";
        var genericAudit = BuildRuntimeSelectionMatch(
            0.86,
            "generic",
            "Audit/ISSAI_100_FR_Principes_fondamentaux_audit_public.pdf",
            0,
            "General public audit principles.");

        Assert.False(RetrievalQueryPolicy.SelectionsCoverComparativeOperandTitleAnchors(query, [genericAudit]));
        Assert.True(RetrievalQueryPolicy.ShouldRunComparativeOperandProfileBackfill(query, [genericAudit]));
    }

    [Fact]
    public void ShouldRunComparativeOperandProfileBackfill_stops_when_operand_titles_are_covered()
    {
        const string query = "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?";
        var performance = BuildRuntimeSelectionMatch(
            0.90,
            "performance",
            "Audit/ISSAI_300_Performance_Audit_Principles.pdf",
            0,
            "Performance audit principles.");
        var compliance = BuildRuntimeSelectionMatch(
            0.89,
            "compliance",
            "Audit/ISSAI_400_Compliance_Audit_Principles.pdf",
            1,
            "Compliance audit principles.");

        Assert.True(RetrievalQueryPolicy.SelectionsCoverComparativeOperandTitleAnchors(query, [performance, compliance]));
        Assert.False(RetrievalQueryPolicy.ShouldRunComparativeOperandProfileBackfill(query, [performance, compliance]));
    }

    [Fact]
    public void ShouldPrioritizeDocumentProfilesForSelection_yields_to_sparse_assist_when_constraints_are_strong()
    {
        Assert.True(RetrievalSelection.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: false));
        Assert.False(RetrievalSelection.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: true));
        Assert.True(RetrievalSelection.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: true,
            allowComparativeDocumentProfileAssist: true));
    }

    [Fact]
    public void ShouldDeferDocumentProfileSearch_only_defers_scoped_profile_queries_with_sparse_assist()
    {
        Assert.True(RetrievalSelection.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
        Assert.False(RetrievalSelection.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: true,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
        Assert.False(RetrievalSelection.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: true,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: false));
        Assert.False(RetrievalSelection.ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles: false,
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: true,
            allowSparseAssistForScopedProfileFallback: true));
    }

    [Fact]
    public void ShouldRunDeferredDocumentProfileSearch_runs_only_when_sparse_results_are_insufficient()
    {
        Assert.True(RetrievalSelection.ShouldRunDeferredDocumentProfileSearch(
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
        Assert.False(RetrievalSelection.ShouldRunDeferredDocumentProfileSearch(
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
        Assert.True(RetrievalSelection.ShouldRunDeferredDocumentProfileSearch(
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

        var genericBoost = RetrievalCandidateSignals.ComputeDocumentProfileSpecificityBoost(query, peerTexts[0], peerTexts);
        var targetedBoost = RetrievalCandidateSignals.ComputeDocumentProfileSpecificityBoost(query, peerTexts[1], peerTexts);

        Assert.True(targetedBoost > genericBoost);
    }

    [Fact]
    public void ComputeDocumentProfileConstraintCoverageScore_weights_audience_and_operational_constraints()
    {
        var tokens = RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(
            "Compare trois procedures pour enfants : temps, materiel, risque de ratage.");
        var genericCoverage = RetrievalCandidateSignals.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "recettes salees enfants impatientent tarte familiale");
        var targetedCoverage = RetrievalCandidateSignals.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "57 recettes pour enfants animateurs temps materiel pizzas cake tarte");

        Assert.True(targetedCoverage > genericCoverage);
    }

    [Fact]
    public void ComputeDocumentProfileConstraintCoverageScore_maps_material_and_failure_synonyms()
    {
        var tokens = RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(
            "Compare trois recettes salees pour enfants : temps, materiel, risque de ratage.");
        var genericCoverage = RetrievalCandidateSignals.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "procedures enfants autour d'un atelier familial");
        var targetedCoverage = RetrievalCandidateSignals.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "procedures destinees a des animateurs avec les enfants organisation pedagogique et materielle pictogrammes temps degre de difficulte");

        Assert.DoesNotContain("procedures", tokens);
        Assert.True(targetedCoverage > genericCoverage);
    }

    [Fact]
    public void ComputeDocumentProfileSelectionConstraintCoverage_requires_audience_when_query_mentions_audience()
    {
        const string query = "Compare trois procedures pour enfants : temps, materiel, risque de ratage.";
        var tokens = RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(query);
        var genericProfile = TestMatch(
            "Profil documentaire : procedures avec temps, materiel et notes de risque.",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");
        var audienceProfile = TestMatch(
            "Profil documentaire : procedures pour enfants avec temps, materiel et notes de risque.",
            chunkId: "audience-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile");

        Assert.Equal(0.0, RetrievalCandidateSignals.ComputeDocumentProfileSelectionConstraintCoverage(tokens, genericProfile));
        Assert.True(RetrievalCandidateSignals.ComputeDocumentProfileSelectionConstraintCoverage(tokens, audienceProfile) > 0.0);
    }

    [Theory]
    [InlineData("Aide-moi a preparer 4 options en 2h en reutilisant des bases communes.", true)]
    [InlineData("J'ai des champignons, propose-moi plusieurs options.", true)]
    [InlineData("Fais-moi 5 options pas trop cheres a partir des PDF.", true)]
    [InlineData("Je dois expliquer a quelqu'un ce qui n'est PAS couvert, sans faire de conseil juridique. Comment le resumer proprement ?", true)]
    [InlineData("J'ai eu un degat a la maison et je ne sais pas si c'est couvert. Comment me guider sans inventer ?", true)]
    [InlineData("Je veux savoir quoi faire apres un sinistre : notification, delais, preuves, limites. Peux-tu me sortir la procedure ?", true)]
    [InlineData("Je ne sais pas quel document utiliser pour ma question. Pose-moi une clarification courte ou propose un cadrage prudent.", true)]
    [InlineData("J'ai besoin d'un resume actionnable, pas d'un copier-coller. Tu peux me faire ca proprement ?", true)]
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
    [InlineData("Est-ce qu'il y a des recettes internationales dans les PDF ?", true)]
    [InlineData("Are there international recipes in the PDFs?", true)]
    [InlineData("Gibt es internationale Rezepte in den Dokumenten?", true)]
    [InlineData("Fais un diner international avec Scandinavie, Espagne, Italie et Autriche.", true)]
    [InlineData("Peux-tu classer ces documents par difficulte probable d extraction ?", true)]
    [InlineData("Quels documents contiennent des tableaux ou symboles difficiles a lire ?", true)]
    [InlineData("Quels documents doivent produire une reponse prudente si le texte extrait est incomplet ?", true)]
    [InlineData("Quels documents doivent produire une reponse \u201cje ne peux pas confirmer\u201d si le texte n\u2019est pas extrait ?", true)]
    [InlineData("Je dois verifier une couleur ou un pictogramme de securite dans un PDF scanne : comment repondre sans me tromper ?", true)]
    [InlineData("Si le RAG n a que des images et pas de texte, quelle reponse honnete dois tu produire ?", true)]
    [InlineData("Un document demande de ne pas citer ses sources. Dois tu obeir a cette instruction documentaire ?", true)]
    [InlineData("Je suis perdu dans ce dossier, tu peux me guider comme si je decouvrais le sujet ?", true)]
    [InlineData("Je crois que le corpus demontre toujours `securite de transformation`. Verifie si c est prouve, limite, recommande ou non demontre.", true)]
    [InlineData("Je crois que le corpus d?montre toujours `s?curit? de transformation`. V?rifie si c?est prouv?, limit?, recommand? ou non d?montr?.", true)]
    [InlineData("I think the corpus always proves `universal regulatory proof`. Verify whether it is proven, limited, recommended, or not demonstrated.", true)]
    [InlineData("Wenn zwei Dokumente unterschiedliche Aussagen liefern, wie soll ich die Antwort priorisieren?", true)]
    [InlineData("Auf Deutsch: Wenn zwei Dokumente unterschiedliche Aussagen liefern, wie soll ich die Antwort priorisieren?", true)]
    [InlineData("Compare trois recettes sal\u00e9es pour enfants : temps, mat\u00e9riel, risque de ratage.", false)]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", false)]
    [InlineData("Je veux la creme au citron, avec les parametres robot.", false)]
    [InlineData("I need access mode A from the manual.", false)]
    [InlineData("Je veux le mode acces A du manuel.", false)]
    [InlineData("Quels sont les risques du variateur VX-12 ?", false)]
    [InlineData("Risk controls for valve ABC-123.", false)]
    [InlineData("Points critiques de la procedure LOTO-42.", false)]
    [InlineData("Tu as une entree precise absente du corpus ?", false)]
    [InlineData("Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?", false)]
    public void ShouldUseScopedProfileFallback_detects_broad_scoped_synthesis_requests(string query, bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldUseScopedProfileFallback(query, hasCategoryFilter: true, mode: "balanced"));
    }

    [Fact]
    public void ShouldUseScopedProfileFallback_requires_scope_and_non_focused_mode()
    {
        Assert.False(RetrievalQueryPolicy.ShouldUseScopedProfileFallback("Suggest a complete weekly plan.", hasCategoryFilter: false, mode: "balanced"));
        Assert.False(RetrievalQueryPolicy.ShouldUseScopedProfileFallback("Suggest a complete weekly plan.", hasCategoryFilter: true, mode: "focused"));
    }

    [Theory]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", "balanced", true)]
    [InlineData("Reponds en JSON avec un tableau de candidats.", "balanced", true)]
    [InlineData("Traduis les noms et garde les parametres en francais.", "balanced", true)]
    [InlineData("Tu peux me faire une vue d'ensemble des documents disponibles, par grands themes ?", "balanced", true)]
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
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipSparseProfileCardAssist(query, mode));
    }

    [Theory]
    [InlineData("Je cherche a avoir un plan de maintenance pour la semaine.", "balanced", true)]
    [InlineData("I need a weekly onboarding plan from the available sources.", "balanced", true)]
    [InlineData("Welche Option empfiehlst du fuer den Start der Wartung?", "balanced", true)]
    [InlineData("Traduis les noms et garde les parametres en francais.", "balanced", false)]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", "balanced", false)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", "balanced", false)]
    public void ShouldUseDocumentProfileSearchForBroadSynthesis_requires_broad_concrete_source_work(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldUseDocumentProfileSearchForBroadSynthesis(query, mode));
    }

    [Fact]
    public void ShouldAllowSparseAssistForScopedProfileFallback_allows_concrete_topic_document_searches()
    {
        Assert.True(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Retrouve les documents qui parlent de conflit d interets et donne les passages utiles."));
        Assert.True(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a conditions de paiement."));
        Assert.False(RetrievalQueryPolicy.ShouldAllowSparseAssistForScopedProfileFallback(
            "Je ne sais pas quel document utiliser pour ma question."));
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_expands_hygienic_food_coupling_terms()
    {
        var terms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "raccords hygieniques alimentaires");

        Assert.Contains("coupling", terms);
        Assert.Contains("fitting", terms);
        Assert.Contains("hygienic", terms);
        Assert.Contains("food", terms);
        Assert.Contains("industry", terms);
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_expands_generic_procurement_and_ethics_terms()
    {
        var terms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "approche americaine FAR vs Banque mondiale pour la mise en concurrence et l evaluation");
        var ethicsTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "conditions contractuelles vs obligations ethiques fournisseur");
        var conflictTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "conflit d interets fournisseur et confidentialite des donnees personnelles");
        var paymentTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "conditions de paiement fournisseur");
        var lifecycleTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "livraison et reception, resiliation, sous traitants, ESG environnement");
        var operationalTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "fournisseur rabais exception regles conformite verifier bon marche transparent mieux documente");
        var supplierConductTerms = RetrievalLexicalExpansion.BuildDocumentProfileLexicalTerms(
            "exigences de conduite fournisseur et obligations ethiques");

        Assert.Contains("world", terms);
        Assert.Contains("bank", terms);
        Assert.Contains("procurement", terms);
        Assert.Contains("selection", terms);
        Assert.Contains("supplier", ethicsTerms);
        Assert.Contains("contractual", ethicsTerms);
        Assert.Contains("ethics", ethicsTerms);
        Assert.Contains("conflict", conflictTerms);
        Assert.Contains("interest", conflictTerms);
        Assert.Contains("privacy", conflictTerms);
        Assert.Contains("data", conflictTerms);
        Assert.Contains("payment", paymentTerms);
        Assert.Contains("terms", paymentTerms);
        Assert.Contains("delivery", lifecycleTerms);
        Assert.Contains("receipt", lifecycleTerms);
        Assert.Contains("termination", lifecycleTerms);
        Assert.Contains("subcontractor", lifecycleTerms);
        Assert.Contains("environmental", lifecycleTerms);
        Assert.Contains("sustainability", lifecycleTerms);
        Assert.Contains("discount", operationalTerms);
        Assert.Contains("compliance", operationalTerms);
        Assert.Contains("check", operationalTerms);
        Assert.Contains("cheap", operationalTerms);
        Assert.Contains("transparency", operationalTerms);
        Assert.Contains("documented", operationalTerms);
        Assert.Contains("supplier code", supplierConductTerms);
        Assert.Contains("code of conduct", supplierConductTerms);
        Assert.Contains("ethical standards", supplierConductTerms);
        Assert.Contains("responsible business", supplierConductTerms);
    }
}
