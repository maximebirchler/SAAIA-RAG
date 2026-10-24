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
    public void BuildFocusedLexicalBackfillQuery_keeps_specific_user_topic()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?");

        Assert.Contains("lentilles", focused, StringComparison.Ordinal);
        Assert.Contains("corail", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("existent", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("recettes", focused, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_extracts_relative_mention_target()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(
            "Retrouve la procedure qui parle de sonde de rotissage et de niveau de cuisson.");

        Assert.Contains("sonde", focused, StringComparison.Ordinal);
        Assert.Contains("rotissage", focused, StringComparison.Ordinal);
        Assert.Contains("niveau", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("procedure", focused, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_trims_apostrophe_context_facets_from_title_subject()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(
            "Compare les deux quiches lorraines du corpus : diff\u00e9rences d\u2019ingr\u00e9dients, m\u00e9thode et style.");

        Assert.Equal("quiches lorraine", focused);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_trims_audience_context_from_title_subject()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(
            "Je cherche la fondue au chocolat pour un groupe d'enfants.");

        Assert.Equal("fondue au chocolat", focused);
    }

    [Theory]
    [InlineData("Retrouve les documents qui parlent de sonde de rotissage.", "sonde de rotissage")]
    [InlineData("Find documents that mention pressure relief valve.", "pressure relief valve")]
    [InlineData("Busca documentos que mencionan valvula de alivio.", "valvula de alivio")]
    [InlineData("Procura documentos que mencionam valvula de alivio.", "valvula de alivio")]
    [InlineData("Trova documenti che menzionano valvola di sicurezza.", "valvola di sicurezza")]
    [InlineData("Finde Dokumente, die sicherheitsventil erwaehnen.", "sicherheitsventil")]
    public void BuildFocusedLexicalBackfillQuery_extracts_mention_targets_across_client_languages(
        string query,
        string expected)
    {
        Assert.Equal(expected, RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(query));
    }

    [Theory]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.", "coq au vin")]
    [InlineData("Donne-moi la methode pour les fruits en beignets.", "fruits en beignets")]
    [InlineData("C'est quoi les grandes etapes du boeuf bourguignon ?", "boeuf bourguignon")]
    [InlineData("C'est quoi les grandes étapes du bœuf bourguignon ?", "boeuf bourguignon")]
    [InlineData("C'est quoi la Tentation de Jansson et comment la faire ?", "tentation de jansson")]
    [InlineData("Il me faut la tartiflette, ingredients + etapes en version claire.", "tartiflette")]
    [InlineData("Il me faut la tartiflette, ingrédients + étapes en version claire.", "tartiflette")]
    [InlineData("Donne-moi le one pot pasta brocoli dinde bacon.", "one pot pasta brocoli dinde bacon")]
    [InlineData("Je cherche le gateau chocolat courgette.", "gateau chocolat courgette")]
    [InlineData("Je cherche la tartiflet ou un truc fromage pomme de terre.", "tartiflet")]
    [InlineData("Tu as la recette du boeuf bourguingnon ?", "boeuf bourguingnon")]
    [InlineData("Je cherche le boeuf bourguingnon, tu peux retrouver la bonne recette malgre la faute ?", "boeuf bourguingnon")]
    [InlineData("I am looking for boeuf bourguingnon; can you find the right recipe despite the typo?", "boeuf bourguingnon")]
    [InlineData("Busco boeuf bourguingnon; puedes encontrar la receta correcta pese al error?", "boeuf bourguingnon")]
    [InlineData("Procuro boeuf bourguingnon; consegues encontrar a receita correta apesar do erro?", "boeuf bourguingnon")]
    [InlineData("Ich suche boeuf bourguingnon; findest du trotz Tippfehler das richtige Rezept?", "boeuf bourguingnon")]
    [InlineData("Cerco boeuf bourguingnon; riesci a trovare la ricetta giusta nonostante l'errore?", "boeuf bourguingnon")]
    [InlineData("Est-ce que la sauce aux 4 fromages vient de Chefbot ou Moulinex ?", "sauce aux 4 fromages")]
    [InlineData("Combien de temps et quels ingredients pour le gratin dauphinois ?", "gratin dauphinois")]
    [InlineData("Combien d'ingredients pour faire des crepes pour 25 personnes ?", "crepes")]
    [InlineData("Calcule les quantites pour 10 bols de veloute.", "veloute")]
    [InlineData("D'ou vient la recette de la Tentation de Jansson ? Donne le PDF et la page si possible.", "tentation de jansson")]
    [InlineData("Comment cuire les asperges vertes au miel avec la sonde de rotissage ?", "asperges vertes au miel")]
    [InlineData("Tu peux m'expliquer les patatas bravas du livre NEFF ?", "patatas bravas")]
    [InlineData("Tu peux me sortir la quiche ?", "quiche")]
    [InlineData("Can you pull up safety valve from the manual?", "safety valve")]
    [InlineData("Donne la recette des patattas bravas.", "patattas bravas")]
    [InlineData("Comment faire la mayonnaise au tofu ?", "mayonnaise au tofu")]
    [InlineData("Detaille le curry de crevettes et riz basmati.", "curry de crevettes et riz basmati")]
    [InlineData("Explique-moi les churros sauce chocolat au Companion.", "churros sauce chocolat")]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?", "patatas bravas")]
    [InlineData("Fondue chocolat pour un anniversaire de 18 enfants : organisation + quantites.", "fondue chocolat")]
    [InlineData("Je veux la creme au citron, avec les parametres robot.", "creme au citron")]
    [InlineData("Je veux la crème au citron, avec les paramètres robot.", "creme au citron")]
    [InlineData("C'est quoi la crepe a Jo ?", "crepe a jo")]
    [InlineData("Je veux une fiche pour Cr\u00e8me au citron avec source.", "creme au citron")]
    [InlineData("Bouillon de volaille source", "bouillon de volaille")]
    [InlineData("Saumon avec sauce yaourt-menthe source", "saumon avec sauce yaourt menthe")]
    [InlineData("Alpha Beta materials", "alpha beta")]
    [InlineData("Alpha Beta etapes", "alpha beta")]
    [InlineData("Alpha Beta temps", "alpha beta")]
    [InlineData("Alpha Beta duration", "alpha beta")]
    [InlineData("Alpha Beta pages", "alpha beta")]
    [InlineData("Quelle est la recette de base de la bechamel ?", "bechamel")]
    [InlineData("What is the basic recipe for bechamel?", "bechamel")]
    [InlineData("Que es la receta basica de bechamel?", "bechamel")]
    [InlineData("O que e a receita basica de bechamel?", "bechamel")]
    [InlineData("Che cos e la ricetta base di bechamel?", "bechamel")]
    [InlineData("Was ist das Grundrezept fuer Bechamel?", "bechamel")]
    [InlineData("Pour les alpha beta VND, quels sont les reglages ?", "alpha beta")]
    [InlineData("Give me the procedure for access mode A from the manual.", "access mode a")]
    [InlineData("Dame una ficha para modo acceso A con fuente.", "modo acceso a")]
    [InlineData("Quero uma ficha para modo acesso A com fonte.", "modo acesso a")]
    [InlineData("Zeige eine Karte zu Zugang Modus A aus dem Handbuch.", "zugang modus a")]
    [InlineData("Mostra una scheda per modalita accesso A con fonte.", "modalita accesso a")]
    public void BuildFocusedLexicalBackfillQuery_extracts_unquoted_content_targets(string query, string expected)
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(query);

        Assert.Equal(expected, focused);
    }

    [Theory]
    [InlineData("Una procedure usa ToolA o ToolB, pero no tengo herramienta. Explica como adaptarla sin inventar.", "ToolA", "ToolB")]
    [InlineData("A procedure uses ToolA or ToolB, but I do not have the tool. Explain how to adapt it without inventing.", "ToolA", "ToolB")]
    [InlineData("Uma procedure usa ToolA ou ToolB, mas nao tenho ferramenta. Explica como adaptar sem inventar.", "ToolA", "ToolB")]
    [InlineData("Eine procedure nutzt ToolA oder ToolB, aber ich habe kein Werkzeug. Erklare die Anpassung ohne zu erfinden.", "ToolA", "ToolB")]
    [InlineData("Una procedure usa ToolA o ToolB, ma non ho lo strumento. Spiega come adattarla senza inventare.", "ToolA", "ToolB")]
    public void ResolvePrimaryRetrievalQuery_ignores_meta_instruction_focus_phrases(
        string query,
        string firstTool,
        string secondTool)
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(query, category: "generic");

        Assert.Contains(firstTool, retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondTool, retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("sin inventar", retrievalQuery, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("without inventing", retrievalQuery, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("sem inventar", retrievalQuery, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("senza inventare", retrievalQuery, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_keeps_short_audience_generic_fragment_intact()
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Une recette enfant.",
            category: "generic");

        Assert.Equal("Une recette enfant.", retrievalQuery);
    }

    [Theory]
    [InlineData("Tu peux m'expliquer simplement ce que le document appelle l'inertage absolu ?", "inertage")]
    [InlineData("Pour resumer a un client, quels sont les grands composants d'un systeme d'inertage selon le guide ?", "composants")]
    [InlineData("Avant de dire au client que son inertage respecte EN 15281, qu'est-ce qu'il faut lui demander ?", "15281")]
    [InlineData("Si je l'explique simplement a un client, l'idee c'est bien de remplacer l'air par un gaz neutre ?", "remplacer")]
    [InlineData("Pour expliquer le fonctionnement a un client, comment tu distingues le seuil de regulation et le seuil de coupure ?", "seuil")]
    [InlineData("Le guide explique aussi comment aller lire ou ecrire dans la memoire partagee du terminal depuis le PLC ?", "memoire")]
    public void ResolvePrimaryRetrievalQuery_does_not_replace_runtime_subject_with_request_scaffold(
        string query,
        string expectedSubject)
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(query, category: null);

        Assert.Contains(expectedSubject, retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldTreatAsBroadDiversityQuery_allows_vs_guidance_with_version_operand()
    {
        Assert.True(RetrievalQueryPolicy.ShouldTreatAsBroadDiversityQuery(
            "Si les deux documents semblent contradictoires sur objectif 1,5°C vs synthèse AR6, comment dois-tu decider quelle source utiliser ?"));
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_keeps_implicit_document_hint_out_of_title()
    {
        var focused = RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.");

        Assert.Equal("nouilles sautees legumes crevettes", focused);
    }

    [Fact]
    public void ExpandRetrievalQuery_adds_corpus_agnostic_cross_language_progress_terms()
    {
        var expanded = RetrievalLexicalExpansion.ExpandRetrievalQuery(
            "Peux-tu affirmer que tous les pays ont atteint les objectifs ?",
            category: null);

        Assert.Contains("countries", expanded);
        Assert.Contains("achieved", expanded);
        Assert.Contains("goals", expanded);
    }

    [Fact]
    public void BuildLexicalProfileEmptyRecoveryQuery_uses_cross_language_expansion_terms()
    {
        var query = RetrievalLexicalExpansion.BuildLexicalProfileEmptyRecoveryQuery(
            "Peux-tu affirmer que tous les pays ont atteint les objectifs ?",
            category: null);

        Assert.Contains("countries", query);
        Assert.Contains("achieved", query);
        Assert.Contains("goals", query);
    }

    [Fact]
    public void ExtractReferenceVersionGroupKeysFromQuery_expands_slash_separated_parts()
    {
        var keys = RetrievalQueryPolicy.ExtractReferenceVersionGroupKeysFromQuery(
            "Quelles sources citer separement pour ISO 13849-1/2 ?");

        Assert.Contains("iso 13849 1", keys);
        Assert.Contains("iso 13849 2", keys);
    }

    [Fact]
    public void ShouldTreatAsBroadDiversityQuery_allows_standard_family_comparison()
    {
        const string query = "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?";

        Assert.True(RetrievalQueryPolicy.ShouldPreferComparativeDocumentDiversity(query));
        Assert.True(RetrievalQueryPolicy.ShouldTreatAsBroadDiversityQuery(query));
        Assert.False(RetrievalQueryPolicy.ShouldTreatAsBroadDiversityQuery(
            "Que dit ISO 13849 sur les principes de conception ?"));
    }

    [Fact]
    public void BuildDocumentProfileSpecificityTokens_keeps_domain_constraints_without_primary_intent_noise()
    {
        var tokens = RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(
            "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.");

        Assert.Contains("apprentices", tokens);
        Assert.Contains("material", tokens);
        Assert.Contains("failure", tokens);
        Assert.Contains("setup", tokens);
        Assert.DoesNotContain("compare", tokens);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_keeps_full_text_for_topical_document_overview()
    {
        const string query = "I am looking for advice about a roasting probe and doneness levels. Which documents mention this?";

        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(query, category: "generic");

        Assert.Contains("Which documents mention this", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("roasting probe", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_adds_corpus_agnostic_translingual_variants()
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Cherche les tableaux associes a `dommages accidentels`.",
            category: "generic");

        Assert.Contains("dommages accidentels", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accidental damage", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("damage", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_focuses_delimited_user_demand()
    {
        const string query = "Prepare une reponse courte pour orienter un utilisateur qui demande `Alpha Beta proprietes thermiques`.";

        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(query, category: "generic");
        var lexicalSurface = RetrievalLexicalExpansion.ResolveLexicalRetrievalSurface(query, retrievalQuery);

        Assert.StartsWith("Alpha Beta proprietes thermiques", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thermal", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("melt point", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(retrievalQuery, lexicalSurface);
        Assert.DoesNotContain("orienter", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utilisateur", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("demande", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_repairs_replacement_accent_markers_in_delimited_user_demand()
    {
        const string query = "Pr?pare une r?ponse courte pour orienter un utilisateur qui demande `propri?t?s ?lectriques`.";

        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(query, category: "generic");
        var lexicalSurface = RetrievalLexicalExpansion.ResolveLexicalRetrievalSurface(query, retrievalQuery);

        Assert.StartsWith("proprietes electriques", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("electrical", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dielectric", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(retrievalQuery, lexicalSurface);
        Assert.DoesNotContain("Pr?pare", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQueryExpansionTerms_adds_generic_technical_property_variants()
    {
        var electrical = RetrievalLexicalExpansion.BuildQueryExpansionTerms("proprietes electriques");
        var thermal = RetrievalLexicalExpansion.BuildQueryExpansionTerms("proprietes thermiques");
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery("proprietes electriques", category: "generic");

        Assert.Contains("property", electrical);
        Assert.Contains("electrical properties", electrical);
        Assert.Contains("electrical", electrical);
        Assert.Contains("dielectric properties", electrical);
        Assert.Contains("dielectric", electrical);
        Assert.Contains("thermal properties", thermal);
        Assert.Contains("thermal", thermal);
        Assert.Contains("temperature properties", thermal);
        Assert.Contains("melt point", thermal);
        Assert.Equal(retrievalQuery, RetrievalLexicalExpansion.ResolveLexicalRetrievalSurface("proprietes electriques", retrievalQuery));
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_focuses_quoted_topical_document_lookup()
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Retrouve les documents qui parlent de `biens assures` et donne-moi les passages utiles.",
            category: "generic");

        Assert.Contains("biens assures", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("insured property", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Retrouve les documents", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_expands_required_evidence_topics()
    {
        var retrievalQuery = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Retrouve les documents qui parlent de `preuves a fournir` et donne-moi les passages utiles.",
            category: "generic");

        Assert.Contains("preuves a fournir", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supporting evidence", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supporting documents", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Je veux un dossier avec options adaptees pour 15 personnes.", "balanced", true)]
    [InlineData("Reponds en JSON avec un tableau de candidats.", "balanced", true)]
    [InlineData("Traduis les noms et garde les parametres en francais.", "balanced", true)]
    [InlineData("Tu peux me faire une vue d'ensemble des documents disponibles, par grands themes ?", "balanced", false)]
    [InlineData("Menu complet utilisant les elements concus pour la sonde X.", "balanced", true)]
    [InlineData("Rends le mode A ou le mode B un peu plus robuste sans pretendre que c'est officiel.", "balanced", true)]
    [InlineData("Pose-toi 5 questions de verification avant de repondre a une demande ambigue.", "balanced", true)]
    [InlineData("Which valve should I choose for pressure class PN16?", "balanced", false)]
    [InlineData("Quels sont les risques du variateur VX-12 ?", "balanced", false)]
    [InlineData("Combien de vis M6 pour assembler le kit Alpha ?", "balanced", false)]
    [InlineData("How many O-rings for pump HPX-2000?", "balanced", false)]
    [InlineData("Compare les styles de trois procedures presentes.", "balanced", false)]
    [InlineData("Compare trois procedures pour apprentis : materiel, risques et consignes.", "balanced", false)]
    [InlineData("Compare trois procedures salees pour enfants : temps, materiel, risque de ratage.", "balanced", false)]
    [InlineData("Compare trois recettes sal\u00e9es pour enfants : temps, mat\u00e9riel, risque de ratage.", "balanced", false)]
    [InlineData("Compare \"Mode acces A\" et \"Mode acces B\".", "broad", false)]
    public void ShouldSkipDocumentProfileSearchForLowCostBroadQuery_preserves_precise_anchors(
        string query,
        string mode,
        bool expected)
    {
        Assert.Equal(expected, RetrievalQueryPolicy.ShouldSkipDocumentProfileSearchForLowCostBroadQuery(query, mode));
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_uses_focused_target_without_touching_broad_queries()
    {
        var focused = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Donne-moi la recette du coq au vin dans le livre international.",
            "cuisine");
        var broad = RetrievalLexicalExpansion.ResolvePrimaryRetrievalQuery(
            "Quelles recettes avec des lentilles corail existent dans les PDF ?",
            "cuisine");

        Assert.Equal("coq au vin", focused);
        Assert.Equal("Quelles recettes avec des lentilles corail existent dans les PDF ?", broad);
    }

    [Fact]
    public void ExpandRetrievalQuery_only_adds_corpus_agnostic_surface_forms()
    {
        var mealPlanning = RetrievalLexicalExpansion.ExpandRetrievalQuery("Je veux organiser des repas pour toute la semaine", "cuisine");
        var accentFolded = RetrievalLexicalExpansion.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecôte ?", "cuisine");

        Assert.Equal("Je veux organiser des repas pour toute la semaine", mealPlanning);
        Assert.Contains("entrecote", accentFolded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steak", accentFolded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch cooking", accentFolded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractLexicalQueryTokens_ignores_ui_noise_tokens_but_keeps_business_words()
    {
        var tokens = RetrievalLexicalExpansion.ExtractLexicalQueryTokens("stp je cherche le pdf inerting safety controls manual");

        Assert.Contains("inerting", tokens);
        Assert.Contains("safety", tokens);
        Assert.Contains("controls", tokens);
        Assert.DoesNotContain("pdf", tokens);
        Assert.DoesNotContain("stp", tokens);
        Assert.DoesNotContain("manual", tokens);
    }

    [Fact]
    public void BuildDocumentHintScopedBackfillQuery_requires_hint_and_focused_subject()
    {
        var focused = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la procedure Alpha Beta du guide Acme.",
            "Alpha Beta");
        var naturalLanguageHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la recette du coq au vin dans le livre international.",
            "coq au vin");
        var explicitFileHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Dans ANSI Z535.1.pdf, trouve un tableau, une liste ou une section structuree et explique ce que tu peux lire avec confiance.",
            "trouve tableau liste section structuree");
        var bareFileMentionHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Fais un resume prudent de ANSI Z535.2.pdf en distinguant ce qui est sur de ce qui depend de la qualite OCR.",
            "resume prudent qualite OCR");
        var extensionlessStrongDocumentHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            "retrouver annexes appendices");
        var possessiveExtensionlessStrongDocumentHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Resume uniquement ce que les annexes de ACME_Report_2024_Appendices ajoutent au document principal.",
            "resume annexes document principal");
        var noHint = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la procedure Alpha Beta.",
            "Alpha Beta");
        var noSubject = RetrievalQueryPolicy.BuildDocumentHintScopedBackfillQuery(
            "Que dit le guide Acme ?",
            "Acme");

        Assert.Equal("Alpha Beta", focused);
        Assert.Equal("coq au vin", naturalLanguageHint);
        Assert.Equal("trouve tableau liste section structuree", explicitFileHint);
        Assert.Equal("resume prudent qualite OCR", bareFileMentionHint);
        Assert.Equal("retrouver annexes appendices", extensionlessStrongDocumentHint);
        Assert.Equal("resume annexes document principal", possessiveExtensionlessStrongDocumentHint);
        Assert.Equal(string.Empty, noHint);
        Assert.Equal(string.Empty, noSubject);
    }

    [Fact]
    public void BuildDirectSpecificContentCoverageTokens_keeps_distinctive_terms_without_generic_facets()
    {
        var tokens = RetrievalSelection.BuildDirectSpecificContentCoverageTokens(
            "Je veux les reglages de rotissage pour le module vert avec la sonde principale.");

        Assert.Contains("module", tokens);
        Assert.Contains("sonde", tokens);
        Assert.DoesNotContain("veux", tokens);
        Assert.DoesNotContain("pour", tokens);
    }
}
