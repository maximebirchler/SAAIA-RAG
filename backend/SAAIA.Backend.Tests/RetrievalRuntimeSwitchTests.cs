using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
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

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_extracts_relative_mention_target()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(
            "Retrouve la procedure qui parle de sonde de rotissage et de niveau de cuisson.");

        Assert.Contains("sonde", focused, StringComparison.Ordinal);
        Assert.Contains("rotissage", focused, StringComparison.Ordinal);
        Assert.Contains("niveau", focused, StringComparison.Ordinal);
        Assert.DoesNotContain("procedure", focused, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_trims_apostrophe_context_facets_from_title_subject()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(
            "Compare les deux quiches lorraines du corpus : diff\u00e9rences d\u2019ingr\u00e9dients, m\u00e9thode et style.");

        Assert.Equal("quiches lorraine", focused);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_trims_audience_context_from_title_subject()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(
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
        Assert.Equal(expected, RagEndpoints.BuildFocusedLexicalBackfillQuery(query));
    }

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

        Assert.True(RagEndpoints.IsDenseMatchEmbeddingCompatible(e5Match, "intfloat/multilingual-e5-base"));
        Assert.False(RagEndpoints.IsDenseMatchEmbeddingCompatible(rawMatch, "intfloat/multilingual-e5-base"));
        Assert.False(RagEndpoints.IsDenseMatchEmbeddingCompatible(legacyMatch, "intfloat/multilingual-e5-base"));
        Assert.True(RagEndpoints.IsDenseMatchEmbeddingCompatible(rawMatch, "sentence-transformers/all-MiniLM-L6-v2"));
        Assert.False(RagEndpoints.IsDenseMatchEmbeddingCompatible(legacyMatch, "sentence-transformers/all-MiniLM-L6-v2"));
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

        Assert.False(RagEndpoints.IsDenseMatchEmbeddingCompatible(navigationByRole, "intfloat/multilingual-e5-base"));
        Assert.False(RagEndpoints.IsDenseMatchEmbeddingCompatible(navigationByChunkType, "intfloat/multilingual-e5-base"));
        Assert.True(RagEndpoints.IsDenseMatchEmbeddingCompatible(mixedContent, "intfloat/multilingual-e5-base"));
    }

    private static RagMatch BuildDenseMatch(
        string? embeddingModel,
        string? embeddingInputFormat,
        string? chunkType = "unit_exact_v1",
        string? contentRole = null)
        => new(
            Score: 0.80,
            DocId: "doc-a",
            DocPath: "Docs/A.pdf",
            DocName: "A.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "chunk-a",
            ChunkIndex: 0,
            Text: "content",
            IngestionVersion: 1,
            HashDoc: "hash-a",
            EmbedText: "content",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Section",
            HeadingPath: "Section",
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: contentRole,
            EmbeddingModel: embeddingModel,
            EmbeddingInputFormat: embeddingInputFormat);

    private static RagMatch BuildRuntimeSelectionMatch(
        double score,
        string docId,
        string docPath,
        int chunkIndex,
        string text)
        => new(
            Score: score,
            DocId: docId,
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: chunkIndex + 1,
            PageEnd: chunkIndex + 1,
            ChunkId: $"chunk-{docId}",
            ChunkIndex: chunkIndex,
            Text: text,
            IngestionVersion: 1,
            HashDoc: $"hash-{docId}",
            EmbedText: text,
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: chunkIndex,
            UnitOrdinal: chunkIndex,
            SectionTitle: "Runtime",
            HeadingPath: "Runtime",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: RetrievalContentClassifier.ContentRole,
            ContentDensityScore: 1.0);

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

    [Fact]
    public void ExtractFocusedLookupPhrases_stops_accented_direct_object_before_structured_facets()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Il me faut la tartiflette, ingrédients + étapes en version claire.");

        Assert.Contains("tartiflette", phrases);
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
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(query, category: "generic");

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
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(
            "Une recette enfant.",
            category: "generic");

        Assert.Equal("Une recette enfant.", retrievalQuery);
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
        var phrases = RagEndpoints.ExtractComparativeLookupPhrases(query);

        Assert.Contains(expected, phrases);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_keeps_short_acronym_operands()
    {
        var operands = RagEndpoints.ExtractBareVsComparativeOperands(
            "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?");

        Assert.Contains("uk", operands);
        Assert.Contains("edp", operands);
        Assert.DoesNotContain("fournisseur", operands);
        Assert.DoesNotContain("conduite fournisseur uk", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_prefers_right_revision_label_after_concept_word()
    {
        var operands = RagEndpoints.ExtractBareVsComparativeOperands(
            "Fais un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6.");

        Assert.Contains("ar6", operands);
        Assert.DoesNotContain("synthese", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_handles_conjunction_comparisons_with_short_codes()
    {
        var operands = RagEndpoints.ExtractBareVsComparativeOperands(
            "Compare SSDF et control baselines : est-ce la meme granularite d'exigence ?");

        Assert.Contains("ssdf", operands);
        Assert.Contains("control baselines", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_skips_document_descriptor_before_short_code()
    {
        var operands = RagEndpoints.ExtractBareVsComparativeOperands(
            "Compare les annexes IPCC WG3 et le rapport SDG : quelles informations sont chiffrees ?");

        Assert.Contains("wg3", operands);
        Assert.Contains("sdg", operands);
        Assert.DoesNotContain("rapport", operands);
    }

    [Fact]
    public void ExtractBareVsComparativeOperands_keeps_shared_short_suffix_phrases()
    {
        var operands = RagEndpoints.ExtractBareVsComparativeOperands(
            "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?");

        Assert.Contains("performance audit", operands);
        Assert.Contains("compliance audit", operands);
        Assert.DoesNotContain("audit", operands);
        Assert.DoesNotContain("compliance", operands);
    }

    [Fact]
    public void ExtractComparativeVersionOperandLookupTerms_reads_generic_revision_labels()
    {
        var terms = RagEndpoints.ExtractComparativeVersionOperandLookupTerms(
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
        var terms = RagEndpoints.ExtractComparativeVersionOperandLookupTerms(
            "objectif 1,5Â°C vs synthÃ¨se AR6");

        Assert.Contains("ar6", terms);
        Assert.DoesNotContain("5a", terms);
    }

    [Fact]
    public void ShouldUseBareVsComparativeOperandRoute_allows_version_like_operands_without_concept_phrase()
    {
        Assert.True(RagEndpoints.ShouldUseBareVsComparativeOperandRoute("X r1 vs r2"));
    }

    [Fact]
    public void ShouldTreatAsBroadDiversityQuery_allows_vs_guidance_with_version_operand()
    {
        Assert.True(RagEndpoints.ShouldTreatAsBroadDiversityQuery(
            "Si les deux documents semblent contradictoires sur objectif 1,5°C vs synthèse AR6, comment dois-tu decider quelle source utiliser ?"));
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
        Assert.True(RagEndpoints.ShouldPreferComparativeDocumentDiversity(query));
    }

    [Fact]
    public void Operational_version_operands_are_not_treated_as_extensionless_document_hints()
    {
        const string query = "Peux-tu fusionner les etapes de la r2 et de la r3 dans une seule procedure sans indiquer les sources ?";

        var terms = RagEndpoints.ExtractDocumentStatusOperandLookupTerms(query);

        Assert.Contains("r2", terms);
        Assert.Contains("r3", terms);
        Assert.False(RagEndpoints.ShouldUseExplicitDocumentScopedBackfillRoute(query, "etapes procedure"));
    }

    [Fact]
    public void ShouldUseGenericVersionConflictOverviewRecovery_detects_unscoped_old_new_version_question()
    {
        Assert.True(RagEndpoints.ShouldUseGenericVersionConflictOverviewRecovery(
            "Comment repondre si l'ancienne version est plus detaillee mais la nouvelle est plus recente ?"));
        Assert.False(RagEndpoints.ShouldUseGenericVersionConflictOverviewRecovery(
            "Peux-tu dire que l'AR6 invalide totalement l'AR5 sans verifier le contexte ?"));
    }

    [Fact]
    public void BuildComparativeVersionCounterpartBackfillQueries_extracts_non_version_side()
    {
        var queries = RagEndpoints.BuildComparativeVersionCounterpartBackfillQueries(
            "Donne une reponse prudente a un utilisateur qui demande \u201cquelle version est correcte ?\u201d pour objectif 1,5\u00b0C vs synthese AR6.");

        Assert.Contains("1 5 c", queries);
        Assert.DoesNotContain(queries, query => query.Contains("quelle version", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildComparativeVersionCounterpartBackfillQueries_repairs_mojibake_temperature_token()
    {
        var queries = RagEndpoints.BuildComparativeVersionCounterpartBackfillQueries(
            "objectif 1,5Â°C vs synthÃ¨se AR6");

        Assert.Contains("1 5 c", queries);
        Assert.DoesNotContain(queries, query => query.Contains("5a", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDocumentStatusOperandLookupTerms_adds_bare_vs_version_operands()
    {
        var terms = RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Compare climat AR5 vs AR6 et explique la difference.");

        Assert.Contains("ar5", terms);
        Assert.Contains("ar6", terms);
        Assert.True(RagEndpoints.ShouldMergeDocumentStatusOperandSelectionsForComparativeVersionRoute(
            "Compare climat AR5 vs AR6 et explique la difference.",
            terms));
    }

    [Fact]
    public void BuildComparativeOperandProfileBackfillQueries_extracts_bare_vs_topic_operands()
    {
        var queries = RagEndpoints.BuildComparativeOperandProfileBackfillQueries(
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

        Assert.True(RagEndpoints.ShouldRunComparativeOperandProfileBackfill(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            selected));
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForComparativeLookup_detects_precise_comparison_subject()
    {
        Assert.True(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les deux quiches lorraines du corpus : differences ingredients, methode et style."));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare les styles de trois procedures presentes."));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare trois procedures pour apprentis : materiel, risques et consignes."));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Compare trois procedures salees pour enfants : temps, materiel, risque de ratage."));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Si les deux documents ne disent pas exactement la meme chose sur approche americaine FAR vs Banque mondiale pour la mise en concurrence et l evaluation, comment expliquer la nuance ?"));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?"));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
            "Quelles options sont les plus adaptees pour un dejeuner de semaine rapide ?"));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_reads_french_guillemet_title_in_larger_request()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Asperges vertes au miel \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("asperges vertes au miel", phrases);
    }

    [Fact]
    public void ResolveInternalSelectionTopK_overfetches_precise_title_requests_without_changing_contract_topk()
    {
        var resolved = RagEndpoints.ResolveInternalSelectionTopK(
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
        var resolved = RagEndpoints.ResolveInternalSelectionTopK(
            "Reponds avec `Contract.pdf` comme source.",
            requestedTopK: 8,
            maxTopK: 128,
            hasDocScope: false,
            hasExplicitFileDocumentHint: true,
            mode: "balanced");

        Assert.Equal(8, resolved);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_stops_card_title_before_spaced_colon_facets()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?");

        Assert.Contains("patatas bravas", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains("ingredients", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_ignores_broad_synthesis_response_shape_tail()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Fais un d\u00eener international avec Scandinavie, Espagne, Italie et Autriche, et explique l\u2019encha\u00eenement.");

        Assert.DoesNotContain("enchainement", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_reads_predicate_object_questions()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Quelle recette utilise du miso blanc ?");

        Assert.Contains("miso blanc", phrases);
    }

    [Fact]
    public void ExtractFocusedLookupPhrases_prefers_parameter_target_after_for()
    {
        var phrases = RagEndpoints.ExtractFocusedLookupPhrases(
            "Quels reglages de rotissage pour le rumsteck aux oignons grilles ?");

        Assert.Contains("rumsteck aux oignons grilles", phrases);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_uses_parameter_target_after_for_as_precise_title()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Quels reglages de rotissage pour le rumsteck aux oignons grilles ?");

        Assert.Contains("rumsteck aux oignons grilles", phrases);
    }

    [Fact]
    public void BuildFocusedLexicalBackfillQuery_keeps_implicit_document_hint_out_of_title()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.");

        Assert.Equal("nouilles sautees legumes crevettes", focused);
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_ignores_secondary_qualifiers()
    {
        var tokens = RagEndpoints.ExtractFocusedLookupSelectionAnchorTokens(
            "Quelle recette utilise du miso blanc ?");

        Assert.Equal(["miso"], tokens);
    }

    [Fact]
    public void ExtractFocusedLookupPrimaryHeadTokens_prefers_entity_head_over_broad_qualifier()
    {
        var tokens = RagEndpoints.ExtractFocusedLookupPrimaryHeadTokens(
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["payment-terms"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_prefers_associated_topic_over_table_facets()
    {
        var tokens = RagEndpoints.ExtractFocusedLookupSelectionAnchorTokens(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a conflit d interets.");

        Assert.Contains("conflit", tokens);
        Assert.Contains("interets", tokens);
        Assert.DoesNotContain("tableaux", tokens);
        Assert.DoesNotContain("criteres", tokens);
    }

    [Fact]
    public void ExtractFocusedLookupSelectionAnchorTokens_prefers_backtick_associated_topic_over_table_facets()
    {
        var tokens = RagEndpoints.ExtractFocusedLookupSelectionAnchorTokens(
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
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
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
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);
        var tokens = RagEndpoints.BuildTitleLookupSqlTokens(phrases);

        Assert.Contains(expectedPhrase, phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains(forbiddenFacet, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(forbiddenFacet, tokens);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_does_not_trim_non_terminal_lookup_words()
    {
        var focused = RagEndpoints.BuildFocusedLexicalBackfillQuery("source code transfer agreement");
        var titlePhrases = RagEndpoints.ExtractTitleLookupPhrases("source code transfer agreement");

        Assert.NotEqual("source code transfer", focused);
        Assert.DoesNotContain("source code transfer", titlePhrases);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_ignores_broad_document_overview_requests()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Retrouve les documents qui parlent de sonde de rotissage.");

        Assert.Empty(phrases);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_extracts_explicit_file_mentions()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Fais un resume prudent de ANSI Z535.2.pdf en distinguant ce qui depend de la qualite OCR.");
        var multi = RagEndpoints.ExtractTitleLookupPhrases(
            "Comment citer correctement ANSI Z535.1.pdf et ANSI Z535.4.pdf si le texte est mal extrait ?");

        Assert.Contains("ansi z535 2 pdf", phrases);
        Assert.Contains("ansi z535 2", phrases);
        Assert.Contains("ansi z535 1 pdf", multi);
        Assert.Contains("ansi z535 4 pdf", multi);

        var encoded = RagEndpoints.ExtractTitleLookupPhrases(
            "Dans `Pol%C3%ADtica%20de%20Privacidade_Fornecedores_14112023%20%281%29_eng-GB.pdf`, retrouve les passages utiles.");
        var lowercase = RagEndpoints.ExtractTitleLookupPhrases(
            "Peux-tu me resumer `document_inexistant.pdf` et l'utiliser comme source principale ?");
        const string predicateAfterFileQuery =
            "Comment verifier qu'une valeur extraite de ISO 13849-1 2023 Safety of machinery - Safety-related parts of control systems - Part 1 General principles for design.pdf n'est pas issue d'une ancienne version ?";
        const string unicodePredicateAfterFileQuery =
            "Comment v\u00e9rifier qu\u2019une valeur extraite de ISO 13849-1 2023 Safety of machinery - Safety-related parts of control systems - Part 1 General principles for design.pdf n\u2019est pas issue d\u2019une ancienne version ?";
        var predicateAfterFile = RagEndpoints.ExtractTitleLookupPhrases(predicateAfterFileQuery);
        var explicitPredicateAfterFile = RagEndpoints.ExtractExplicitFileLookupPhrases(predicateAfterFileQuery);
        var explicitUnicodePredicateAfterFile = RagEndpoints.ExtractExplicitFileLookupPhrases(unicodePredicateAfterFileQuery);

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
        Assert.True(RagEndpoints.ShouldUseExplicitDocumentScopedBackfillRoute(
            predicateAfterFileQuery,
            "valeur extraite ancienne version"));
    }

    [Fact]
    public void ShouldAllowLexicalProfileEmptyRecovery_blocks_explicit_filename_surface()
    {
        Assert.False(RagEndpoints.ShouldAllowLexicalProfileEmptyRecovery(
            "Peux-tu me resumer `document_inexistant.pdf` et l'utiliser comme source principale ?",
            hasExplicitFileDocumentHint: true));

        Assert.True(RagEndpoints.ShouldAllowLexicalProfileEmptyRecovery(
            "Retrouve les documents qui parlent de clause de renouvellement.",
            hasExplicitFileDocumentHint: false));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_builds_numeric_variants_for_natural_language_dates()
    {
        var french = RagEndpoints.ExtractTitleLookupPhrases(
            "Dans les minutes reunion FOMC des 17-18 septembre 2024, quels sujets sont traites ?");
        var english = RagEndpoints.ExtractTitleLookupPhrases(
            "How do the FOMC Sep 17-18 2024 minutes describe inflation?");

        Assert.Contains("fomc 2024 09 18", french);
        Assert.Contains("fomc minutes 2024 09 18", french);
        Assert.Contains("fomc 2024 09 18", english);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_prioritizes_near_date_title_anchor_over_instruction_words()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "J'ai besoin d'une réponse claire : dans les minutes réunion FOMC des 17-18 septembre 2024, quelle est la structure ?");

        Assert.True(Array.IndexOf(phrases.ToArray(), "fomc 2024 09 18") >= 0);
        Assert.True(Array.IndexOf(phrases.ToArray(), "fomc 2024 09 18") < 8);
        Assert.DoesNotContain("claire 2024 09 18", phrases.Take(6));
    }

    [Fact]
    public void ExtractDateAnchoredTitleLookupPhrases_reads_dated_document_reference_after_preamble()
    {
        var phrases = RagEndpoints.ExtractDateAnchoredTitleLookupPhrases(
            "J'ai besoin d'une r\u00e9ponse claire : dans les minutes r\u00e9union FOMC des 17-18 septembre 2024, quelle est la structure ?");

        Assert.Contains("fomc 2024 09 18", phrases);
        Assert.Contains("fomc minutes 2024 09 18", phrases);
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_keeps_short_numeric_date_parts()
    {
        var tokens = RagEndpoints.BuildTitleLookupSqlTokens(["fomc 2024 09 18"]);

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

        Assert.True(RagEndpoints.SelectionsCoverDateAnchoredDocument(
            "Dans les minutes réunion FOMC des 17-18 septembre 2024, quels sujets sont traités ?",
            selected));
        Assert.False(RagEndpoints.SelectionsCoverDateAnchoredDocument(
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

        Assert.False(RagEndpoints.SelectionsCoverDateAnchoredDocument(
            "Dans les minutes reunion FOMC des 17-18 septembre 2024, quels sujets sont traites ?",
            selected));
    }

    [Fact]
    public void IsResolvedTitleOrNavigationRoute_treats_document_name_routes_as_resolved()
    {
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Document-name routed content.",
            embeddingBasis: "date_document_name_v1")));
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Reference document-name routed content.",
            embeddingBasis: "standard_reference_document_name_v1")));
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(TestMatch(
            "Reference operand routed content.",
            embeddingBasis: "reference_version_operand_document_name_v1")));
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

        RagEndpoints.PruneUnrelatedReferenceVersionGroupSelections(
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

        RagEndpoints.PruneUnrelatedReferenceVersionGroupSelections(
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

        RagEndpoints.PruneUnrelatedReferenceVersionGroupSelections(
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

        RagEndpoints.PruneUnrequestedReferenceVersionYearSelections(
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

        RagEndpoints.PruneUnrequestedReferenceVersionYearSelections(
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

        Assert.False(RagEndpoints.SelectionsCoverReferenceVersionOperandContent(query, onlyProfileForNewVersion));
        Assert.True(RagEndpoints.SelectionsCoverReferenceVersionOperandContent(query, contentForBothVersions));
    }

    [Fact]
    public void ShouldRunScopedCatalogEmptySelectionRecovery_detects_broad_category_synthesis()
    {
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je veux expliquer a quelqu un ce qui s est passe entre septembre 2024 et octobre 2025. Fais un resume comprehensible et source.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux reunions ou le diagnostic semble evoluer ou se nuancer.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux réunions où le diagnostic semble évoluer ou se nuancer. Explique si c’est une contradiction réelle ou seulement un changement de contexte.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je crois que le corpus demontre toujours `mode d emploi detaille`. Verifie si c est prouve, limite, recommande ou non demontre.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Je crois que le corpus d?montre toujours `mode d emploi d?taill?`. V?rifie si c?est prouv?, limit?, recommand? ou non d?montr?.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "I think the corpus always proves `detailed operating instructions`. Verify whether it is proven, limited, recommended, or not demonstrated.",
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.False(RagEndpoints.ShouldRunScopedCatalogEmptySelectionRecovery(
            "Trouve deux reunions ou le diagnostic semble evoluer ou se nuancer.",
            hasCategoryFilter: false,
            mode: "balanced"));
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_ignores_broad_scoped_synthesis_questions()
    {
        Assert.Empty(RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé."));
        Assert.Empty(RagEndpoints.ExtractExplicitFileLookupPhrases(
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(
            "Je veux expliquer à quelqu’un qui ne suit pas la Fed ce qui s’est passé entre septembre 2024 et octobre 2025. Fais un résumé compréhensible et sourcé.",
            selections);

        Assert.Single(selections);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_uses_standard_reference_as_document_anchor()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Explique le role du fichier AC par rapport au document principal EN 13135-1.");

        Assert.Contains("en 13135 1", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.Contains("role du fichier", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractTitleLookupPhrases_keeps_compact_document_code_without_quotes()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Donne-moi une statistique precise du WG3 sans citation de tableau.");

        Assert.Contains("wg3", phrases);
        Assert.DoesNotContain("sans", phrases);
    }

    [Fact]
    public void ExtractCompactDocumentCodeProfileRecoveryPhrases_merges_comparison_operands()
    {
        var phrases = RagEndpoints.ExtractCompactDocumentCodeProfileRecoveryPhrases(
            "Compare les annexes IPCC WG3 et le rapport SDG : quelles informations sont chiffrees ?");

        Assert.Contains("wg3", phrases);
        Assert.Contains("sdg", phrases);
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

        Assert.True(RagEndpoints.SelectionsCoverCompactDocumentCodePhrase("wg3", selected));
        Assert.False(RagEndpoints.SelectionsCoverCompactDocumentCodePhrase("sdg", selected));
    }

    [Fact]
    public void ShouldAllowCompactDocumentCodeEmptyRecovery_allows_implicit_document_hint_without_filename_surface()
    {
        Assert.True(RagEndpoints.ShouldAllowCompactDocumentCodeEmptyRecovery(
            "Donne-moi une statistique precise du WG3 sans citation de tableau.",
            hasExplicitFileDocumentHint: true));

        Assert.False(RagEndpoints.ShouldAllowCompactDocumentCodeEmptyRecovery(
            "Donne-moi une statistique precise dans `WG3.pdf`.",
            hasExplicitFileDocumentHint: true));
    }

    [Fact]
    public void ExpandRetrievalQuery_adds_corpus_agnostic_cross_language_progress_terms()
    {
        var expanded = RagEndpoints.ExpandRetrievalQuery(
            "Peux-tu affirmer que tous les pays ont atteint les objectifs ?",
            category: null);

        Assert.Contains("countries", expanded);
        Assert.Contains("achieved", expanded);
        Assert.Contains("goals", expanded);
    }

    [Fact]
    public void BuildLexicalProfileEmptyRecoveryQuery_uses_cross_language_expansion_terms()
    {
        var query = RagEndpoints.BuildLexicalProfileEmptyRecoveryQuery(
            "Peux-tu affirmer que tous les pays ont atteint les objectifs ?",
            category: null);

        Assert.Contains("countries", query);
        Assert.Contains("achieved", query);
        Assert.Contains("goals", query);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_expands_slash_separated_parts()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
            "Quelles sources citer separement pour ISO 13849-1/2 ?");

        Assert.Contains("iso 13849 1", phrases);
        Assert.Contains("iso 13849 2", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_keeps_nist_and_bare_versioned_references()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
            "Compare les procedures NIST 800-53A avec les appendices 800-53r5.");

        Assert.Contains("nist 800 53a", phrases);
        Assert.Contains("800 53a", phrases);
        Assert.Contains("800 53r5", phrases);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_keeps_accented_comparison_surface()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
            "Compare les procédures d’évaluation NIST 800-53A avec les appendices 800-53r5 : quel est le lien entre contrôle et assessment ?");

        Assert.Contains("nist 800 53a", phrases);
        Assert.Contains("800 53a", phrases);
        Assert.Contains("800 53r5", phrases);
    }

    [Fact]
    public void SelectionsCoverStandardReferencePhrases_requires_each_requested_reference()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
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

        Assert.False(RagEndpoints.SelectionsCoverStandardReferencePhrases(phrases, selected));
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

        RagEndpoints.PruneUnrequestedStandardReferenceSelections(
            "Compare les procedures NIST 800-53A avec les appendices 800-53r5.",
            selected);

        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, match => match.DocName == "NIST_SP_800_53A_Assessment_Procedures_Annexes.pdf");
        Assert.Contains(selected, match => match.DocName == "NIST_SP_800_53r5_Appendices.pdf");
        Assert.DoesNotContain(selected, match => match.DocName == "NIST_SP_800_160v1r1_SSE_Appendices.pdf");
    }

    [Fact]
    public void ExtractDocumentStatusOperandLookupTerms_detects_corrections_and_amendments()
    {
        Assert.Contains("berichtigung", RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Le Berichtigung contient-il tout le contenu de la norme complete ?"));
        Assert.Contains("ac", RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Peux-tu appliquer un AC tout seul sans citer la norme principale ?"));
        Assert.Contains("ac", RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Comment signaler qu'il existe un corrigendum ?"));
        var amendmentTerms = RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Le prA1 remplace-t-il automatiquement le document 2008+A1 ?");
        Assert.Contains("pra1", amendmentTerms);
        Assert.Contains("a1", amendmentTerms);
        var baselineTerms = RagEndpoints.ExtractDocumentStatusOperandLookupTerms(
            "Quelle baseline s'applique si je ne donne pas le type de systeme ?");
        Assert.Contains("baseline", baselineTerms);
        Assert.Contains("baselines", baselineTerms);
    }

    [Fact]
    public void BuildDocumentStatusOperandCompanionBackfillQueries_adds_compared_code_operand()
    {
        var baseline = TestMatch(
            "Control baselines.",
            docPath: "Docs/NIST_SP_800_53B_Control_Baselines.pdf",
            score: 0.90);
        var queries = RagEndpoints.BuildDocumentStatusOperandCompanionBackfillQueries(
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
        var queries = RagEndpoints.BuildDocumentStatusOperandCompanionBackfillQueries(
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
        var queries = RagEndpoints.BuildDocumentStatusOperandCompanionBackfillQueries(
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

        var ordered = RagEndpoints.PrioritizeDocumentStatusOperandSelections(
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

        var ordered = RagEndpoints.PrioritizeDocumentStatusOperandSelections(
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

        var ordered = RagEndpoints.PrioritizeDocumentStatusOperandSelections(
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

        var ordered = RagEndpoints.PrioritizeDocumentStatusOperandSelections(
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
            RagEndpoints.BuildMatchDedupKey(main)
        };

        RagEndpoints.ReattachDocumentStatusOperandCompanions(
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
        Assert.True(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu appliquer un AC tout seul sans citer la norme principale ?",
            docId: null,
            docPath: null));
        Assert.True(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu comparer ce document à une ancienne version absente du dossier ?",
            docId: null,
            docPath: null));
    }

    [Fact]
    public void ShouldSuppressUnscopedAmbiguousDocumentReferenceSources_preserves_scoped_or_anchored_queries()
    {
        Assert.False(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Peux-tu comparer ce document à une ancienne version absente du dossier ?",
            docId: "doc-1",
            docPath: null));
        Assert.False(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Le prA1 remplace-t-il automatiquement le document 2008+A1 ?",
            docId: null,
            docPath: null));
        Assert.False(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Je dois appliquer une exigence electrotechnique de levage : faut-il regarder le document principal ou l'AC ?",
            docId: null,
            docPath: null));
        Assert.False(RagEndpoints.ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(
            "Quelles sources citer separement pour ISO 13849-1/2 ?",
            docId: null,
            docPath: null));
    }

    [Fact]
    public void SelectionsCoverStandardReferencePhrases_detects_missing_slash_part()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
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

        Assert.False(RagEndpoints.SelectionsCoverStandardReferencePhrases(phrases, onlyPartOne));
        Assert.True(RagEndpoints.SelectionsCoverStandardReferencePhrases(phrases, bothParts));
    }

    [Fact]
    public void ExtractReferenceVersionGroupKeysFromQuery_expands_slash_separated_parts()
    {
        var keys = RagEndpoints.ExtractReferenceVersionGroupKeysFromQuery(
            "Quelles sources citer separement pour ISO 13849-1/2 ?");

        Assert.Contains("iso 13849 1", keys);
        Assert.Contains("iso 13849 2", keys);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_expands_family_references_to_parts()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?");

        Assert.Contains("iso 13849 1", phrases);
        Assert.Contains("iso 13849 2", phrases);
        Assert.Contains("iso 13849", phrases);
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_trims_family_prefix_from_standard_reference_hint()
    {
        var phrases = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?");

        Assert.Contains("iso 13849", phrases);
        Assert.DoesNotContain("famille iso 13849", phrases);
    }

    [Fact]
    public void ShouldTreatAsBroadDiversityQuery_allows_standard_family_comparison()
    {
        const string query = "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?";

        Assert.True(RagEndpoints.ShouldPreferComparativeDocumentDiversity(query));
        Assert.True(RagEndpoints.ShouldTreatAsBroadDiversityQuery(query));
        Assert.False(RagEndpoints.ShouldTreatAsBroadDiversityQuery(
            "Que dit ISO 13849 sur les principes de conception ?"));
    }

    [Fact]
    public void ResolveRetriever_labels_standard_reference_document_name_route()
    {
        var match = TestMatch(
            "ISO 13849-1 safety-related parts of control systems.",
            embeddingBasis: "standard_reference_document_name_v1");

        Assert.Equal("standard_reference_document_name", RagEndpoints.ResolveRetriever(match));
    }

    [Fact]
    public void ResolveRetriever_labels_reference_version_operand_route()
    {
        var match = TestMatch(
            "EN 1005-3 2002 prA1 amendment text.",
            embeddingBasis: "reference_version_operand_document_name_v1");

        Assert.Equal("reference_version_operand_document_name", RagEndpoints.ResolveRetriever(match));
    }

    [Fact]
    public void ExtractReferenceVersionComparisonOperands_reads_years_and_amendments()
    {
        var operands = RagEndpoints.ExtractReferenceVersionComparisonOperands(
            "Compare la version 2002, le prA1 et la version 2008+A1 sans mélanger les textes.");

        Assert.Contains("2002", operands);
        Assert.Contains("pr a1", operands);
        Assert.Contains("2008", operands);
        Assert.Contains("2008 a1", operands);
    }

    [Fact]
    public void ComputeReferenceVersionOperandTitleSignal_matches_compact_and_spaced_variants()
    {
        const string query = "Compare la version 2002, le prA1 et la version 2008+A1.";

        Assert.True(RagEndpoints.ComputeReferenceVersionOperandTitleSignal(
            query,
            "EN 1005-3 2002 prA1.pdf") > 0);
        Assert.True(RagEndpoints.ComputeReferenceVersionOperandTitleSignal(
            query,
            "EN 1005-3 2002+A1 2008 Safety of machinery.pdf") > 0);
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

        var groupKeys = RagEndpoints.InferReferenceVersionOperandGroupKeys(query, matches);

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

        var groupKeys = RagEndpoints.InferReferenceVersionOperandGroupKeys(query, matches);

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

        RagEndpoints.PruneSupersededReferenceVersionSelections(
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

        RagEndpoints.PruneSupersededReferenceVersionSelections(
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

        RagEndpoints.PruneSupersededReferenceVersionSelections(
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

        RagEndpoints.PruneSupersededReferenceVersionSelections(
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

        RagEndpoints.PruneSupersededReferenceVersionSelections(
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

        RagEndpoints.PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(
            "Retrouve l'ancienne version archivee de Control Manual.",
            selected);
        RagEndpoints.PruneSupersededReferenceVersionSelections(
            "Retrouve l'ancienne version archivee de Control Manual.",
            selected);

        Assert.Contains(selected, match => match.ChunkId == "archived");
        Assert.DoesNotContain(selected, match => match.ChunkId == "current");
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(
            "Quelle difference entre principes de conception et validation dans la famille ISO 13849 ?",
            selected);

        Assert.Equal("standard-part-1", Assert.Single(selected).ChunkId);
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

        RagEndpoints.PruneUnrelatedReferenceVersionGroupSelections(query, selected);
        RagEndpoints.PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(query, selected);
        RagEndpoints.PruneUnrequestedReferenceVersionYearSelections(query, selected);
        RagEndpoints.PruneSupersededReferenceVersionSelections(query, selected);

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

        RagEndpoints.CleanupLateSelectionBackfills(query, selected);
        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Contains(selected, match => match.ChunkId == "nist-53a");
        Assert.Contains(selected, match => match.ChunkId == "nist-53r5");
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_keeps_each_named_file_separate()
    {
        var phrases = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Compare ABC 12.2.pdf et ABC 12.4.pdf si le texte est mal extrait.");

        Assert.Contains("abc 12 2 pdf", phrases);
        Assert.Contains("abc 12 2", phrases);
        Assert.Contains("abc 12 4 pdf", phrases);
        Assert.Contains("abc 12 4", phrases);
        Assert.DoesNotContain("abc 12 5 pdf", phrases);

        var groups = RagEndpoints.ExtractExplicitFileLookupPhraseGroups(
            "Compare ABC 12.2.pdf et ABC 12.4.pdf si le texte est mal extrait.");

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.Contains("abc 12 2 pdf"));
        Assert.Contains(groups, group => group.Contains("abc 12 4 pdf"));

        var longNamedFileGroups = RagEndpoints.ExtractExplicitFileLookupPhraseGroups(
            "Compare NFPA 79 2024 Electrical Standard for Industrial Machinery.pdf et UL 508A 2018 Industrial Control Panels - Scan.pdf sur machine industrielle vs panneaux industriels.");

        Assert.Equal(2, longNamedFileGroups.Count);
        Assert.Contains(longNamedFileGroups, group => group.Contains("nfpa 79 2024 electrical standard for industrial machinery pdf"));
        Assert.Contains(longNamedFileGroups, group => group.Contains("ul 508a 2018 industrial control panels scan pdf"));

        var underscored = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Donne une reponse courte a partir de `US_FAR.pdf` avec une citation exploitable.");

        Assert.Contains("us far pdf", underscored);
        Assert.Contains("us far", underscored);
    }

    [Fact]
    public void ExtractStandardReferenceTitleLookupPhrases_accepts_ul_references()
    {
        var phrases = RagEndpoints.ExtractStandardReferenceTitleLookupPhrases(
            "Peux-tu donner une exigence UL 508A sans citer la page parce que le PDF est scanne ?");

        Assert.Contains("ul 508a", phrases);
    }

    [Fact]
    public void BuildComparativeOperandTitleBackfillQueries_expands_cross_language_technical_phrases()
    {
        var queries = RagEndpoints.BuildComparativeOperandTitleBackfillQueries(
            "Si une valeur est illisible dans salle propre vs documentation technique, peux-tu la deduire depuis l autre document ?");

        Assert.Contains("cleanroom", queries);
        Assert.Contains("documentation technique", queries);
    }

    [Fact]
    public void ExtractExplicitFileLookupPhrases_detects_strong_extensionless_document_names()
    {
        var phrases = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les appendices ?");
        var naturalLanguage = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Dans deux semaines, peux-tu retrouver ou commence le planning ?");
        var roleClassification = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quels documents semblent etre des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?");
        var technicalCertificateSubject = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Que puis-je conclure du certificat USP Class VI pour TF1641 et TF1620, sans extrapoler a tous les PTFE ?");
        var technicalDatasheetSubject = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Que permet de savoir la fiche Siemens Moteur Simotics 1LE1003-0EB42-2FB4-Z ?");
        var technicalMsdsSubject = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quelles informations de securite faut-il extraire de la MSDS 3M PTFE TF1620/TF1641/TF1645 ?");
        var technicalInventorySubject = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quels documents dois-je lire en priorite si je cherche des certificats, des fiches techniques et des modes d emploi ?");
        var quotedProductTopic = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quels documents parlent de `PTFE TF1620 TF1641 TF1645` et que permettent-ils de verifier sans extrapoler ?");
        var quotedEvidenceTopic = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Quels passages du corpus documentent `phthalates DEHP BBP DBP DIBP` et dans quel type de document les trouve-t-on ?");
        var quotedComparisonTopic = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Compare les sources disponibles sur `PTFE TF1620 TF1641 TF1645` entre certificats, data sheets, brochure ou MSDS selon ce qui existe dans le corpus.");
        var documentRelationshipTopic = RagEndpoints.ExtractExplicitFileLookupPhrases(
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

        var possessive = RagEndpoints.ExtractExplicitFileLookupPhrases(
            "Resume uniquement ce que les annexes de ACME_Report_2024_Appendices ajoutent au document principal.");

        Assert.Contains(possessive, phrase =>
            phrase.Contains("acme report 2024 appendices", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_explicit_file_doc_path_over_sibling_text_hits()
    {
        var wrongSibling = new RagMatch(
            1.02,
            "doc-wrong",
            "Docs/ABC 12.5.pdf",
            "ABC 12.5.pdf",
            8,
            8,
            "wrong",
            4,
            "Foreword mentioning ABC 12.2 and ABC 12.4 as related standards.",
            1,
            "hash-wrong",
            "Matched title_anchor_route: ABC 12.2\nForeword mentioning ABC 12.2",
            "title_anchor_route_v1",
            null,
            null,
            "ABC 12.2",
            "ABC 12.2",
            "section_window_v1",
            null,
            null,
            null);
        var explicitTarget = new RagMatch(
            0.82,
            "doc-target",
            "Docs/ABC 12.2.pdf",
            "ABC 12.2.pdf",
            1,
            1,
            "target",
            0,
            "OCR cover page with weak title extraction.",
            1,
            "hash-target",
            "Matched explicit_document_title_route: abc 12 2 pdf\nOCR cover page with weak title extraction.",
            "explicit_document_title_route_v1",
            null,
            null,
            "Cover",
            "Cover",
            "section_window_v1",
            null,
            null,
            null);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [wrongSibling, explicitTarget],
            prioritizeDocumentProfiles: false,
            query: "Fais un resume prudent de ABC 12.2.pdf.");

        Assert.Equal("target", ordered[0].ChunkId);
    }

    [Fact]
    public void ShouldSkipPreciseTitleLookupForDocumentOverview_keeps_quoted_overview_on_profile_path()
    {
        Assert.True(RagEndpoints.ShouldSkipPreciseTitleLookupForDocumentOverview(
            "Quelles sources parlent de \"corrosion interne\" ?",
            hasDocScope: false,
            mode: "balanced"));
        Assert.False(RagEndpoints.ShouldSkipPreciseTitleLookupForDocumentOverview(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_matches_ocr_glued_title_tokens()
    {
        var score = RagEndpoints.ComputeQuotedLookupCandidateScore(
            ["escalope de volaille aux champignons pommes darphin"],
            "Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS, POMMES DARPHIN50 min Ingredients.");

        Assert.True(score >= 20.0);
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_keeps_surface_and_folded_diacritic_forms()
    {
        var tokens = RagEndpoints.BuildTitleLookupSqlTokens(["charlotte aux p\u00eaches"]);

        Assert.Contains("p\u00eaches", tokens);
        Assert.Contains("peches", tokens);
        Assert.Contains("charlotte", tokens);
    }

    [Fact]
    public void BuildTitleLookupSqlTokens_extracts_quoted_multi_token_recipe_title()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");
        var tokens = RagEndpoints.BuildTitleLookupSqlTokens(phrases);

        Assert.Contains("escalope", tokens);
        Assert.Contains("volaille", tokens);
        Assert.Contains("champignons", tokens);
        Assert.Contains("pommes", tokens);
        Assert.Contains("darphin", tokens);
    }

    [Fact]
    public void ExtractTitleLookupPhrases_extracts_short_quoted_structured_sheet_title()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce moutarde \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("sauce moutarde", phrases);
    }

    [Fact]
    public void ShouldSkipQuotedTitleFullScan_skips_unquoted_precise_titles_without_doc_scope()
    {
        Assert.True(RagEndpoints.ShouldSkipQuotedTitleFullScan(
            "Resume-moi le cassoulet toulousain sans oublier les temps.",
            hasDocScope: false));
        Assert.False(RagEndpoints.ShouldSkipQuotedTitleFullScan(
            "Resume-moi le \"cassoulet toulousain\" sans oublier les temps.",
            hasDocScope: false));
        Assert.False(RagEndpoints.ShouldSkipQuotedTitleFullScan(
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
        Assert.Equal(expected, RagEndpoints.ResolveLocalTitleTokenMinimumHits(tokenCount));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_late_body_mentions()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?");
        var bodyMention = string.Join(
            ' ',
            Enumerable.Repeat("Preparation etapes ingredients cuisson", 18))
            + " Servez avec une sauce bolognaise ou une autre sauce.";

        Assert.False(RagEndpoints.HasLocalTitleTokenLeadEvidence(bodyMention, null, null, phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_footer_page_titles()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Donne-moi la charlotte aux peches.");
        var footerTitle = string.Join(
            ' ',
            Enumerable.Repeat("Ingredients preparation materiel technique fruits sucre moule dessert", 24))
            + " Copyright 2003 Charlotte aux peches 330011 fiche dessert Page 1";

        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(footerTitle, null, null, phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_footer_title_followed_by_page_marker()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Boulettes de viande suedoises accompagnees de sauce \u00bb : ingredients, etapes, temps et source ?");
        var footerTitle = string.Join(
            ' ',
            Enumerable.Repeat("Ingredients preparation sauce viande hachee cuisson poele fond boeuf creme", 28))
            + " Boulettes de viande suedoises accompagnees de sauce12 Scandinavie 75g de pain blanc";

        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(footerTitle, null, null, phrases));
        Assert.False(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            footerTitle.Replace(" sauce12 Scandinavie", " sauce250 g"),
            null,
            null,
            phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_lead_titles_and_ocr_joined_titles()
    {
        var shortTitle = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : ingredients, etapes, temps et source ?");
        var joinedTitle = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");

        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            "SAUCE BOLOGNAISE\n\nIngredients puis preparation detaillee.",
            null,
            null,
            shortTitle));
        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            "FLORA LYCEE PROFESSIONNEL Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS POMMES DARPHIN50 min Ingredients.",
            null,
            null,
            joinedTitle));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_measured_ingredient_occurrence_in_lead()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Source pour \u00ab Bouillon de volaille \u00bb ?");

        Assert.False(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            "Ingredients: 75 cl de bouillon de volaille, 2 carottes, thym. Preparation faire mijoter puis filtrer.",
            null,
            null,
            phrases));
        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(
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

        Assert.False(RagEndpoints.MatchedQuotedTitleHasTargetTitleEvidence(scattered));
        Assert.False(RagEndpoints.HasProfileTitleHint(scattered));
    }

    [Fact]
    public void MatchedQuotedTitleHasTargetTitleEvidence_accepts_real_lead_titles()
    {
        var lead = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients puis preparation detaillee.",
            embedText: "Matched quoted title: sauce bolognaise; sauce bolognaise au soja texture\nSAUCE BOLOGNAISE AU SOJA TEXTURE\n\nIngredients puis preparation detaillee.",
            docPath: "Cuisine/Recipe.pdf");

        Assert.True(RagEndpoints.MatchedQuotedTitleHasTargetTitleEvidence(lead));
        Assert.True(RagEndpoints.HasProfileTitleHint(lead));
    }

    [Fact]
    public void ComputeQuotedLookupCandidateScore_prefers_full_ocr_title_over_partial_ingredient_overlap()
    {
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Escalope de volaille aux champignons, pommes Darphin \u00bb : ingredients, etapes, temps et source ?");
        var target = "FLORA LYCEE PROFESSIONNEL Menu ESCALOPEDE VOLAILLE AUX CHAMPIGNONS, POMMES DARPHIN50 min Ingredients.";
        var partial = "Alouettes sans tete. Ingredients: escalopes de volailles, champignons sautes et puree de pomme de terre.";

        var targetScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > partialScore);
        Assert.True(targetScore >= 20.0);
        Assert.Equal(0.0, partialScore);
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

    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 12)]
    [InlineData(8, 48)]
    [InlineData(20, 96)]
    public void ResolveExactMatchCandidateLimit_overfetches_before_selection(int topK, int expected)
    {
        Assert.Equal(expected, RagEndpoints.ResolveExactMatchCandidateLimit(topK));
    }

    [Theory]
    [InlineData(8, false, 32)]
    [InlineData(20, false, 80)]
    [InlineData(64, false, 128)]
    [InlineData(20, true, 20)]
    public void ResolveLocalTitleTokenCandidateLimit_overfetches_global_title_lookups(int topK, bool hasDocScope, int expected)
    {
        Assert.Equal(expected, RagEndpoints.ResolveLocalTitleTokenCandidateLimit(topK, hasDocScope));
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
        Assert.Equal(expected, RagEndpoints.ResolveLocalTitleTokenSqlCandidateLimit(
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
        Assert.Equal(expected, RagEndpoints.ShouldRetryLocalTitleTokenSqlCoverage(
            returnedRowCount,
            initialCandidateLimit,
            fullCandidateLimit,
            matchCount,
            topK,
            hasCategoryScope));
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
        Assert.Contains("etudiant", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Compare les procedures \u00e9tudiantes et les options de travail."));
        Assert.Contains("francais", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Compare les procedures scandinaves, fran\u00e7aises et espagnoles."));
        Assert.Contains("cremeux", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Compare les options cr\u00e9meuses et simples."));
        Assert.Contains("enfants", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Une procedure enfant."));
        Assert.Contains("children", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Une procedure enfant."));
        Assert.DoesNotContain("procedures", RagEndpoints.BuildDocumentProfileLexicalTerms(
            "Compare trois procedures pour enfants : temps, materiel, risque de ratage."));
    }

    [Fact]
    public void BuildDocumentProfileLexicalTerms_adds_generic_cross_language_technical_variants()
    {
        var specificityTokens = RagEndpoints.BuildDocumentProfileSpecificityTokens(
            "exigence electrique issue d un scan avec confiance");
        var terms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "exigence electrique issue d un scan");

        Assert.Contains("electrique", specificityTokens);
        Assert.DoesNotContain("scan", specificityTokens);
        Assert.DoesNotContain("confiance", specificityTokens);
        Assert.Contains("electrical", terms);
        Assert.Contains("requirement", terms);

        var safetyTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "couleur ou pictogramme de securite");
        Assert.Contains("color", safetyTerms);
        Assert.Contains("symbol", safetyTerms);

        var technicalTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "certificats, fiches techniques et modes d emploi pour le modele HPX-2000");
        Assert.Contains("certificate", technicalTerms);
        Assert.Contains("datasheet", technicalTerms);
        Assert.Contains("operating instructions", technicalTerms);
        Assert.Contains("hpx2000", technicalTerms);

        var complianceTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "declaration de conformite ATEX et norme IEC 60079");
        Assert.Contains("conformity", complianceTerms);
        Assert.Contains("compliance", complianceTerms);
        Assert.Contains("standard", complianceTerms);

        var cleanroomTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "salle propre vs documentation technique");
        Assert.Contains("cleanroom", cleanroomTerms);
        Assert.Contains("clean room", cleanroomTerms);

        var safetyDataSheetTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "safety data sheet handling precautions");
        Assert.Contains("msds", safetyDataSheetTerms);
        Assert.Contains("sds", safetyDataSheetTerms);
        Assert.Contains("material safety data sheet", safetyDataSheetTerms);

        var versionTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "compare ancienne version et version courante du certificat");
        Assert.Contains("old version", versionTerms);
        Assert.Contains("current version", versionTerms);
        Assert.Contains("certificate", versionTerms);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_requested_safety_data_sheet_document_type()
    {
        var genericDataSheet = TestMatch(
            text: "PTFE technical data sheet. Handling precautions are summarized in the general instructions for use.",
            docPath: "Docs/FIT-PTFE Technical Data Sheet.pdf",
            chunkId: "data-sheet",
            score: 0.98);
        var safetyDataSheet = TestMatch(
            text: "PTFE material safety data sheet. Handling precautions include storage, disposal and personal protection notes.",
            docPath: "Docs/MSDS-PTFE Safety Data Sheet.pdf",
            chunkId: "safety-data-sheet",
            score: 0.76);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "safety data sheet handling precautions",
            [genericDataSheet, safetyDataSheet]);

        Assert.Equal("safety-data-sheet", calibrated[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prioritizes_document_profile_constraint_coverage()
    {
        var genericSafety = TestMatch(
            text: "Document profile about safety colors, signs, sources and confidence notes.",
            docPath: "Docs/ansi-safety-colors.pdf",
            chunkId: "generic-safety",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.92);
        var electricalRequirement = TestMatch(
            text: "Document profile covering electrical standard requirements for industrial machinery.",
            docPath: "Docs/electrical-standard.pdf",
            chunkId: "electrical-requirement",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.84);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [genericSafety, electricalRequirement],
            prioritizeDocumentProfiles: false,
            query: "exigence electrique issue d un scan");

        Assert.Equal("electrical-requirement", ordered[0].ChunkId);
    }

    [Fact]
    public void ResolveDocumentProfileCandidateCount_keeps_profile_window_bounded()
    {
        Assert.Equal(12, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: true));
        Assert.Equal(12, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 80, topK: 8, profileOnly: false));
        Assert.Equal(10, RagEndpoints.ResolveDocumentProfileCandidateCount(candidates: 10, topK: 8, profileOnly: true));
        Assert.Equal(28, RagEndpoints.ResolveDocumentProfileCandidateCount(
            candidates: 210,
            topK: 14,
            profileOnly: false,
            comparativeProfileAssist: true));
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
        Assert.Equal(expected, RagEndpoints.ResolveSqlHeavyRetrieverCandidateLimit(candidates, topK));
    }

    [Theory]
    [InlineData(600, 100, 600)]
    [InlineData(1200, 100, 600)]
    public void ResolveSqlHeavyRetrieverCandidateLimit_uses_topk_scaled_window_for_large_requests(
        int candidates,
        int topK,
        int expected)
    {
        Assert.Equal(expected, RagEndpoints.ResolveSqlHeavyRetrieverCandidateLimit(candidates, topK));
    }

    [Fact]
    public void ResolveDefaultCandidateCount_overfetches_balanced_comparisons()
    {
        Assert.Equal(210, RagEndpoints.ResolveDefaultCandidateCount(
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
        Assert.Equal(expected, RagEndpoints.ShouldRunBroadDiversityCandidateBackfill(query, selectedCount, topK));
    }

    [Fact]
    public void ShouldRunBroadDiversityCandidateCoverage_keeps_comparative_subject_queries_diverse()
    {
        Assert.True(RagEndpoints.ShouldRunBroadDiversityCandidateCoverage(
            "Compare les plats epices ou inspires du monde dans le corpus.",
            topK: 8));
    }

    [Fact]
    public void ShouldRunBroadDiversityCandidateCoverage_handles_bare_vs_operand_queries()
    {
        Assert.True(RagEndpoints.ShouldRunBroadDiversityCandidateCoverage(
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

        RagEndpoints.PrioritizeResponseDiversityCoverage(
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

        RagEndpoints.PrioritizeResponseDiversityCoverage(
            "Compare les styles de recettes scandinaves, francaises et espagnoles presentes.",
            selected,
            responseTopK: 8);

        Assert.Contains(selected.Take(8), match => match.DocPath == "Docs/I.pdf");
    }

    [Fact]
    public void ShouldAllowSparseAssistForScopedProfileFallback_detects_broad_queries_with_strong_constraints()
    {
        Assert.True(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Compare trois procedures pour apprentis : materiel, risques et consignes."));
        Assert.True(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Quel dessert francais choisir pour un repas chic ?"));
        Assert.False(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Fais une vue d'ensemble des documents disponibles."));
        Assert.False(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Je veux un dossier avec options adaptees pour 15 personnes."));
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
        Assert.Equal(expected, RagEndpoints.ShouldAllowSparseAssistForBroadDiversity(query));
    }

    [Theory]
    [InlineData("Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.", true)]
    [InlineData("Compare les styles de trois procedures presentes.", false)]
    [InlineData("Compare les options.", false)]
    public void ShouldAllowMultipleChunksForComparativeSubject_keeps_evidence_from_same_document_when_subject_is_specific(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldAllowMultipleChunksForComparativeSubject(query));
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

        Assert.Equal(expected, RagEndpoints.ShouldRunFinalComparativeSparseDiversityBackfill(query, selected));
    }

    [Fact]
    public void BuildComparativeSubjectBackfillQueries_prefers_strong_reordered_subject_phrases()
    {
        var queries = RagEndpoints.BuildComparativeSubjectBackfillQueries(
            "Compare trois plats mijot\u00e9s fran\u00e7ais et dis lequel choisir pour un repas de famille.");

        Assert.Equal("mijoter plat", queries[0]);
        Assert.Contains("mijoter plat", queries);
        Assert.Contains("mijoter", queries);
        Assert.DoesNotContain("repas famille", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_title_subject_ahead_of_facets()
    {
        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            "Compare les deux quiches lorraines du corpus : diff\u00e9rences d\u2019ingr\u00e9dients, m\u00e9thode et style.");

        Assert.Contains("quiches lorraines", queries);
        Assert.Contains("quiches lorraine", queries);
        Assert.DoesNotContain("quiches lorraines ingredient", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_can_split_compared_segments_when_profile_assist_is_active()
    {
        const string query = "Compare les recettes \u00e9tudiantes et les recettes d\u00e9jeuner au travail : objectif budget et \u00e9quilibre.";

        Assert.Empty(RagEndpoints.BuildComparativeTitleBackfillQueries(query));

        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            query,
            allowProfileAssistBackfill: true);

        Assert.Contains("recettes etudiantes", queries);
        Assert.Contains(queries, static q => q.StartsWith("recettes dejeuner", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_focuses_bare_vs_operands_on_concept_phrases()
    {
        const string query = "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?";

        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
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
        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("hazardous locations", queries);
        Assert.Contains("machines electriques", queries);
        Assert.Contains("machinery electrical", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_shared_suffix_operands()
    {
        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            "Si les deux documents ne disent pas exactement la meme chose sur performance audit vs compliance audit, comment expliquer la nuance sans creer une contradiction artificielle ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("performance audit", queries);
        Assert.Contains("compliance audit", queries);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_translates_label_safety_operands()
    {
        var queries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            "Si une valeur est illisible dans couleurs vs labels de securite, peux-tu la deduire depuis l'autre document ?",
            allowProfileAssistBackfill: true);

        Assert.Contains("safety labels", queries);
        Assert.Contains("safety colors", queries);
        Assert.True(queries.Count <= 6);
    }

    [Fact]
    public void BuildComparativeTitleBackfillQueries_keeps_compact_document_codes()
    {
        var standardQueries = RagEndpoints.BuildComparativeTitleBackfillQueries(
            "Compare SSDF et control baselines : est-ce la meme granularite d'exigence ?",
            allowProfileAssistBackfill: true);
        var reportQueries = RagEndpoints.BuildComparativeTitleBackfillQueries(
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

        Assert.True(RagEndpoints.ComputeComparativeOperandTitleSignal(query, "GOVUK Supplier Code of Conduct v3") > 0);
        Assert.True(RagEndpoints.ComputeComparativeOperandTitleSignal(query, "EDP Supplier Code of Conduct 2025") > 0);
        Assert.Equal(0, RagEndpoints.ComputeComparativeOperandTitleSignal(query, "US FAR acquisition requirements"));
        Assert.Equal(0, RagEndpoints.ComputeComparativeOperandTitleSignal(query, "EDP Group Portugal General Conditions for the Supply of Goods and Services"));
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

        Assert.False(RagEndpoints.SelectionsCoverComparativeOperandTitleAnchors(query, [genericAudit]));
        Assert.True(RagEndpoints.ShouldRunComparativeOperandProfileBackfill(query, [genericAudit]));
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

        Assert.True(RagEndpoints.SelectionsCoverComparativeOperandTitleAnchors(query, [performance, compliance]));
        Assert.False(RagEndpoints.ShouldRunComparativeOperandProfileBackfill(query, [performance, compliance]));
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

        Assert.True(RagEndpoints.ShouldRunComparativeTitleBackfill(query, sparseOnly));
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

        Assert.True(RagEndpoints.ShouldRunComparativeTitleBackfill(query, [coveredProfileWithoutCards]));
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

        Assert.True(RagEndpoints.ShouldRunComparativeTitleBackfill(
            "Si une valeur est illisible dans hazardous locations vs machines \u00e9lectriques, peux-tu la deduire depuis l'autre document ?",
            selected));
    }

    [Fact]
    public void OrderMatchesForSelection_does_not_put_weak_profiles_before_actionable_content()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var weakProfile = TestMatch(
            "Document profile with generic procedures and setup notes.",
            chunkId: "weak-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.92);
        var content = TestMatch(
            "Procedure for apprentices. Material checklist, failure risk, setup notes and verification steps.",
            chunkId: "actionable-content",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.78);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [weakProfile, content],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("actionable-content", ordered[0].ChunkId);
        Assert.Equal(0.0, RagEndpoints.ComputeDocumentProfileSelectionConstraintCoverage(
            RagEndpoints.BuildDocumentProfileSpecificityTokens(query),
            weakProfile));
    }

    [Fact]
    public void OrderMatchesForSelection_keeps_supported_profiles_ahead_for_broad_comparisons()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var supportedProfile = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            chunkId: "supported-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.70);
        var content = TestMatch(
            "Generic procedure with setup notes but no audience or risk details.",
            chunkId: "generic-content",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [content, supportedProfile],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("supported-profile", ordered[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_promotes_card_page_chunk_before_its_profile()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var supportedProfile = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            docPath: "Docs/Apprentices.pdf",
            chunkId: "supported-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.70) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Alpha beta procedure",
                    PageStart: 4,
                    PageEnd: 4,
                    Kind: "unit_lead",
                    Signals: ["structured_facts"])
            ]
        };
        var actionableChunk = TestMatch(
            "Alpha beta procedure. Material checklist: gloves and tray. Failure risk: overheating. Setup notes: cool before handling.",
            docPath: "Docs/Apprentices.pdf",
            page: 4,
            chunkId: "actionable-chunk",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.64);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [supportedProfile, actionableChunk],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("actionable-chunk", ordered[0].ChunkId);
        Assert.Equal("supported-profile", ordered[1].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_constraint_covered_content_for_situational_choices()
    {
        const string query = "Which field procedure should I choose for a winter deployment?";
        var genericActionable = TestMatch(
            "Field procedure. Materials, steps, validation and deployment notes.",
            docPath: "Docs/GeneralProcedure.pdf",
            chunkId: "generic",
            score: 0.94);
        var constrained = TestMatch(
            "Winter field procedure. Materials, steps, deployment notes and cold-weather validation.",
            docPath: "Docs/WinterFieldProcedure.pdf",
            chunkId: "winter",
            score: 0.72);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [genericActionable, constrained],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("winter", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_document_and_card_constraints_for_broad_choice_queries()
    {
        var genericDessert = TestMatch(
            "Cheesecake with berries. Ingredients, preparation and serving notes.",
            docPath: "Cuisine/student-desserts.pdf",
            chunkId: "generic-dessert",
            score: 0.94) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Cheesecake aux fraises",
                    Signals: ["quantity_list", "structured_facts", "dessert"])
            ]
        };
        var constrainedDessert = TestMatch(
            "Tarte Tatin. Recettes sucrees, preparation au four et service pour un repas.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "french-dessert",
            score: 0.70) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Tarte Tatin",
                    Signals: ["quantity_list", "structured_facts", "recettes sucrees"])
            ]
        };
        var selected = new List<RagMatch> { genericDessert, constrainedDessert };

        RagEndpoints.PrioritizeFinalSelections(
            "Quel dessert francais choisir pour un repas chic ?",
            selected);

        Assert.Equal("french-dessert", selected[0].ChunkId);
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
            .Select(RagEndpoints.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePool = selected.Concat([target]).ToList();

        RagEndpoints.EnsureBroadDiversityCandidateCoverage(
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

        var cards = RagEndpoints.BuildRouteTitleFallbackMatchedContentCards(route);

        Assert.NotNull(cards);
        var card = Assert.Single(cards);
        Assert.Equal("Churros avec sauce au chocolat", card.Title);
        Assert.Equal(90, card.PageStart);
        Assert.Equal("title_anchor_route", card.Kind);
        Assert.Contains("route_title", card.Signals ?? []);
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_accented_query_terms()
    {
        var repaired = RagEndpoints.RepairLikelyMojibakeText(
            "Si je veux un dessert chocolat\u00c3\u0192\u00c2\u00a9 ou cr\u00c3\u0192\u00c2\u00a9meux, lequel est le plus simple ?");

        Assert.Equal("Si je veux un dessert chocolat\u00e9 ou cr\u00e9meux, lequel est le plus simple ?", repaired);
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_single_pass_french_punctuation()
    {
        var repaired = RagEndpoints.RepairLikelyMojibakeText(
            "On doit choisir entre deux fournisseurs : un tr\u00c3\u00a8s bon march\u00c3\u00a9 mais peu transparent. Comment les textes m\u00e2\u20ac\u2122aident ?");

        Assert.Equal("On doit choisir entre deux fournisseurs : un tr\u00e8s bon march\u00e9 mais peu transparent. Comment les textes m'aident ?", repaired);
    }

    [Fact]
    public void RepairLikelyMojibakeText_restores_common_french_comparison_terms()
    {
        var repaired = RagEndpoints.RepairLikelyMojibakeText(
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
            .Select(RagEndpoints.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backfill = BuildRuntimeSelectionMatch(
            0.95,
            "doc-target",
            "Cuisine/Target.pdf",
            4,
            "Coq au vin. Portez le tout a ebullition, laissez mijoter, puis servez ce plat familial.");

        RagEndpoints.MergeComparativeSubjectBackfillMatches(
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
            .Select(RagEndpoints.BuildMatchDedupKey)
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

        RagEndpoints.MergeComparativeSubjectBackfillMatches(
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
            .Select(RagEndpoints.BuildMatchDedupKey)
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

        RagEndpoints.MergeComparativeSubjectBackfillMatches(
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
            .Select(RagEndpoints.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backfill = BuildRuntimeSelectionMatch(
            0.96,
            "doc-b",
            "Docs/B.pdf",
            2,
            "Procedure notes with setup details.");

        RagEndpoints.MergeComparativeSubjectBackfillMatches(
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
            .Select(RagEndpoints.BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actionable = BuildRuntimeSelectionMatch(
            0.95,
            "doc-profile",
            "Docs/Profile.pdf",
            2,
            "Procedure for apprentices. Material checklist, failure risk notes and setup controls.");

        RagEndpoints.MergeComparativeSubjectBackfillMatches(
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

        var matches = RagEndpoints.SafeRegexMatches(
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview(
            query,
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void DocumentOverviewIntent_detects_role_classification_inventory()
    {
        const string query = "Quels documents semblent être des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?";
        const string unaccentedQuery = "Quels documents semblent etre des guides, lesquels sont des conditions/exigences, et lesquels sont surtout des supports techniques ?";

        Assert.True(RagEndpoints.ContainsDocumentOverviewIntent(query));
        Assert.True(RagEndpoints.ContainsDocumentOverviewIntent(unaccentedQuery));
        Assert.True(RagEndpoints.ContainsDocumentRoleClassificationSurface(unaccentedQuery));
        Assert.True(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview(
            query,
            hasDocScope: false,
            mode: "balanced"));
        Assert.True(RagEndpoints.ShouldUseScopedProfileFallback(
            query,
            hasCategoryFilter: true,
            mode: "balanced"));
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForLowCostBroadQuery(query, "balanced"));
    }

    [Fact]
    public void ShouldUseCatalogDocumentOverviewFallback_distinguishes_inventory_from_clarification()
    {
        Assert.True(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
            "Quels documents contient cette categorie et a quoi servent-ils chacun ?"));
        Assert.True(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
            "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?"));
        Assert.True(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
            "Kannst du die wichtigsten Dokumente dieser Kategorie nach Thema gruppieren und kurz erklaren, wofur sie nutzlich sind?"));
        Assert.False(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
            "Je ne sais pas quel document utiliser pour ma question. Pose-moi une clarification courte ou propose un cadrage prudent."));
        Assert.False(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
            "Which documents mention nitrogen blanketing?"));
        Assert.False(RagEndpoints.ShouldRequireDocumentOverviewProfileMatch(
            "Quels documents contient cette categorie et a quoi servent-ils chacun ?"));
    }

    [Fact]
    public void CalibrateFusedMatches_preserves_catalog_overview_profile_floor()
    {
        const string query = "Quels documents contient cette categorie et a quoi servent-ils chacun ?";
        var profileA = TestMatch(
            "Procurement regulation profile summary.",
            docPath: "Achats/A.pdf",
            chunkId: "profile-a",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.08);
        var profileB = TestMatch(
            "Supplier conduct profile summary.",
            docPath: "Achats/B.pdf",
            chunkId: "profile-b",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.07);

        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [profileA, profileB], query);

        Assert.All(calibrated, match => Assert.True(match.Score >= 0.46));
    }

    [Fact]
    public void CalibrateFusedMatches_preserves_catalog_overview_profile_floor_for_english_overview()
    {
        const string query = "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?";
        var profile = TestMatch(
            "Procurement profile summary.",
            docPath: "Achats/A.pdf",
            chunkId: "profile-a",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.05);
        var profileB = TestMatch(
            "Supplier profile summary.",
            docPath: "Achats/B.pdf",
            chunkId: "profile-b",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.05);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Which documents are available in this category?",
            [profile, profileB],
            query);

        Assert.All(calibrated, match => Assert.True(match.Score >= 0.46));
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

        RagEndpoints.PruneUnmatchedPreciseTitleSelections(query, selected);

        Assert.Single(selected);
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Single(selected);
    }

    [Fact]
    public void ShouldSkipChunkRetrieversForDocumentOverview_keeps_chunks_for_quoted_title_source_request()
    {
        Assert.False(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_keeps_full_text_for_topical_document_overview()
    {
        const string query = "I am looking for advice about a roasting probe and doneness levels. Which documents mention this?";

        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(query, category: "generic");

        Assert.Contains("Which documents mention this", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("roasting probe", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_adds_corpus_agnostic_translingual_variants()
    {
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(
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

        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(query, category: "generic");
        var lexicalSurface = RagEndpoints.ResolveLexicalRetrievalSurface(query, retrievalQuery);

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

        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(query, category: "generic");
        var lexicalSurface = RagEndpoints.ResolveLexicalRetrievalSurface(query, retrievalQuery);

        Assert.StartsWith("proprietes electriques", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("electrical", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dielectric", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(retrievalQuery, lexicalSurface);
        Assert.DoesNotContain("Pr?pare", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQueryExpansionTerms_adds_generic_technical_property_variants()
    {
        var electrical = RagEndpoints.BuildQueryExpansionTerms("proprietes electriques");
        var thermal = RagEndpoints.BuildQueryExpansionTerms("proprietes thermiques");
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery("proprietes electriques", category: "generic");

        Assert.Contains("property", electrical);
        Assert.Contains("electrical properties", electrical);
        Assert.Contains("electrical", electrical);
        Assert.Contains("dielectric properties", electrical);
        Assert.Contains("dielectric", electrical);
        Assert.Contains("thermal properties", thermal);
        Assert.Contains("thermal", thermal);
        Assert.Contains("temperature properties", thermal);
        Assert.Contains("melt point", thermal);
        Assert.Equal(retrievalQuery, RagEndpoints.ResolveLexicalRetrievalSurface("proprietes electriques", retrievalQuery));
    }

    [Fact]
    public void BuildShortTechnicalDirectPhraseTerms_targets_property_table_phrases()
    {
        var electrical = RagEndpoints.BuildShortTechnicalDirectPhraseTerms("proprietes electriques");
        var thermal = RagEndpoints.BuildShortTechnicalDirectPhraseTerms("proprietes thermiques");
        var broad = RagEndpoints.BuildShortTechnicalDirectPhraseTerms("temperature de moulage");

        Assert.Contains("electrical properties", electrical);
        Assert.Contains("dielectric strength", electrical);
        Assert.Contains("proprietes electriques", electrical);
        Assert.Contains("thermal properties", thermal);
        Assert.Contains("service temperature", thermal);
        Assert.Contains("melt point", thermal);
        Assert.Empty(broad);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_short_technical_property_phrase_over_temperature_neighbor()
    {
        var processingNeighbor = new RagMatch(
            Score: 0.98,
            DocId: "neighbor",
            DocPath: "Docs/reference.pdf",
            DocName: "reference.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "neighbor",
            ChunkIndex: 2,
            Text: "To achieve optimum properties, compression molding should be carried out within a temperature range of 23 C to 26 C.",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "To achieve optimum properties, compression molding should be carried out within a temperature range of 23 C to 26 C.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Processing recommendations",
            HeadingPath: "Processing recommendations",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            MatchedContentCards: new[]
            {
                new RagMatchedContentCard(
                    Title: "Thermal properties",
                    Kind: "table",
                    Signals: new[] { "thermal", "properties" })
            });
        var directPropertyTable = new RagMatch(
            Score: 0.72,
            DocId: "direct",
            DocPath: "Docs/reference.pdf",
            DocName: "reference.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "direct",
            ChunkIndex: 1,
            Text: "Thermal properties Property Value Unit Test Method. Melt point 342 C. Service Temperature Range -200 C to 260 C.",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "Thermal properties Property Value Unit Test Method. Melt point 342 C. Service Temperature Range -200 C to 260 C.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Thermal properties",
            HeadingPath: "Technical data > Thermal properties",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            MatchedContentCards: new[]
            {
                new RagMatchedContentCard(
                    Title: "Thermal properties",
                    Kind: "table",
                    Signals: new[] { "thermal", "properties", "temperature", "melt" })
            });

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "proprietes thermiques thermal properties temperature properties",
            [processingNeighbor, directPropertyTable]);
        var orderedForSelection = RagEndpoints.OrderMatchesForSelection(
            [processingNeighbor, directPropertyTable],
            prioritizeDocumentProfiles: false,
            query: "proprietes thermiques");

        Assert.Equal("direct", calibrated[0].ChunkId);
        Assert.Equal("direct", orderedForSelection[0].ChunkId);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_focuses_quoted_topical_document_lookup()
    {
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(
            "Retrouve les documents qui parlent de `biens assures` et donne-moi les passages utiles.",
            category: "generic");

        Assert.Contains("biens assures", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("insured property", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Retrouve les documents", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvePrimaryRetrievalQuery_expands_required_evidence_topics()
    {
        var retrievalQuery = RagEndpoints.ResolvePrimaryRetrievalQuery(
            "Retrouve les documents qui parlent de `preuves a fournir` et donne-moi les passages utiles.",
            category: "generic");

        Assert.Contains("preuves a fournir", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supporting evidence", retrievalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supporting documents", retrievalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldSkipChunkRetrieversForDocumentOverview_keeps_chunks_for_quoted_topical_lookup()
    {
        Assert.False(RagEndpoints.ShouldSkipChunkRetrieversForDocumentOverview(
            "Retrouve les documents qui parlent de `preuves a fournir` et donne-moi les passages utiles.",
            hasDocScope: false,
            mode: "balanced"));
    }

    [Fact]
    public void ShouldUseCatalogDocumentOverviewFallback_detects_category_takeaway_summary()
    {
        Assert.True(RagEndpoints.ShouldUseCatalogDocumentOverviewFallback(
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
        Assert.Equal(expected, RagEndpoints.ShouldUseScopedCatalogDocumentOverviewFallback(query, mode));
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
        Assert.True(RagEndpoints.ShouldPrioritizeDocumentProfilesForSelection(
            preferDocumentDiversity: true,
            allowSparseAssistForScopedProfileFallback: true,
            allowComparativeDocumentProfileAssist: true));
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
    public void ComputeDocumentProfileConstraintCoverageScore_weights_audience_and_operational_constraints()
    {
        var tokens = RagEndpoints.BuildDocumentProfileSpecificityTokens(
            "Compare trois procedures pour enfants : temps, materiel, risque de ratage.");
        var genericCoverage = RagEndpoints.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "recettes salees enfants impatientent tarte familiale");
        var targetedCoverage = RagEndpoints.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "57 recettes pour enfants animateurs temps materiel pizzas cake tarte");

        Assert.True(targetedCoverage > genericCoverage);
    }

    [Fact]
    public void ComputeDocumentProfileConstraintCoverageScore_maps_material_and_failure_synonyms()
    {
        var tokens = RagEndpoints.BuildDocumentProfileSpecificityTokens(
            "Compare trois recettes salees pour enfants : temps, materiel, risque de ratage.");
        var genericCoverage = RagEndpoints.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "procedures enfants autour d'un atelier familial");
        var targetedCoverage = RagEndpoints.ComputeDocumentProfileConstraintCoverageScore(
            tokens,
            "procedures destinees a des animateurs avec les enfants organisation pedagogique et materielle pictogrammes temps degre de difficulte");

        Assert.DoesNotContain("procedures", tokens);
        Assert.True(targetedCoverage > genericCoverage);
    }

    [Fact]
    public void ComputeDocumentProfileSelectionConstraintCoverage_requires_audience_when_query_mentions_audience()
    {
        const string query = "Compare trois procedures pour enfants : temps, materiel, risque de ratage.";
        var tokens = RagEndpoints.BuildDocumentProfileSpecificityTokens(query);
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

        Assert.Equal(0.0, RagEndpoints.ComputeDocumentProfileSelectionConstraintCoverage(tokens, genericProfile));
        Assert.True(RagEndpoints.ComputeDocumentProfileSelectionConstraintCoverage(tokens, audienceProfile) > 0.0);
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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

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

        RagEndpoints.PruneUnanchoredFocusedLookupSelections(query, selected);

        Assert.Equal(["quality-profile"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_keeps_single_strong_quoted_title()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00ab Chouquettes \u00bb : ingredients, etapes, temps et source ?");

        Assert.Contains("chouquettes", phrases);
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_repairs_mojibake_french_quote_markers()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \u00c2\u00ab Sauce au poivre \u00c2\u00bb : ingr\u00c3\u00a9dients, \u00c3\u00a9tapes, temps et source ?");

        Assert.Contains("sauce au poivre", phrases);
        Assert.DoesNotContain(phrases, phrase => phrase.EndsWith(" a", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_ignores_quoted_response_shape_columns()
    {
        var query = "Peux-tu faire un tableau \u201cancienne source / nouvelle source / changement / impact\u201d pour objectif 1,5 degres vs synthese AR6 ?";

        Assert.Empty(RagEndpoints.ExtractQuotedLookupPhrases(query));
        Assert.Empty(RagEndpoints.ExtractTitleLookupPhrases(query));
        Assert.True(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query));
    }

    [Fact]
    public void ExtractQuotedLookupPhrases_ignores_quoted_generic_user_question()
    {
        var query = "Donne une reponse prudente a un utilisateur qui demande \u201cquelle version est correcte ?\u201d pour objectif 1,5 degres vs synthese AR6.";

        Assert.Empty(RagEndpoints.ExtractQuotedLookupPhrases(query));
        Assert.True(RagEndpoints.ShouldTreatAsBroadDiversityQuery(query));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_accepts_compact_structured_title_between_time_and_measure()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?");

        Assert.True(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            "SAUCE AU POIVRE\n\n226227 Temps total : 12 min Temps total : 17 min 1 c. a c. de poivre concasse 1 cl de cognac 10 cl de creme liquide 1 c. a c. de fond de veau 1 c. a c. de farine 15 cl d'eau 1 Dans le robot muni du batteur, mettez le poivre, le cognac, la creme liquide, le fond de veau et la farine.",
            sectionTitle: "lc. ac. de farine",
            headingPath: "lc. ac. de farine",
            phrases));
    }

    [Fact]
    public void HasLocalTitleTokenLeadEvidence_rejects_measured_body_phrase_without_title_boundary()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?");

        Assert.False(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            "Ingredients 250 g de viande avec une sauce au poivre maison, sel et herbes. Preparation: saisir puis servir.",
            sectionTitle: null,
            headingPath: null,
            phrases));
    }

    [Theory]
    [InlineData("Donne-moi la recette du coq au vin dans le livre international.")]
    [InlineData("Tu peux me faire une fiche claire pour Patatas Bravas : ingredients, etapes, temps et source ?")]
    public void ShouldBackfillEnumerativeSearch_detects_precise_content_lookup_requests(string query)
    {
        Assert.True(RagEndpoints.ShouldBackfillEnumerativeSearch(query, selectedCount: 0, topK: 8));
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
        Assert.Equal(expected, RagEndpoints.ShouldRelaxDefaultScoreFloorForFocusedStructuredLookup(query));
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
        Assert.Equal(expected, RagEndpoints.ResolveEffectiveSearchMode("focused", query));
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipSparseProfileCardAssist(query, mode));
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipDocumentProfileSearchForLowCostBroadQuery(query, mode));
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
        Assert.Equal(expected, RagEndpoints.ShouldUseDocumentProfileSearchForBroadSynthesis(query, mode));
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipDocumentProfileSearchForPreciseLookup(query));
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForComparativeLookup_keeps_profile_assist_for_reference_comparisons()
    {
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForComparativeLookup(
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
    public void ShouldBackfillFuzzyTitleLead_skips_explicit_file_queries()
    {
        Assert.False(RagEndpoints.ShouldBackfillFuzzyTitleLead(
            "Cite les conditions de paiement dans `Example_General_Conditions.pdf`.",
            []));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_skips_broad_comparative_synthesis()
    {
        Assert.False(RagEndpoints.ShouldBackfillFuzzyTitleLead(
            "Peux-tu faire un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6 ?",
            []));
    }

    [Fact]
    public void ShouldBackfillFuzzyTitleLead_runs_when_content_lacks_focused_title_coverage()
    {
        var weakContent = TestMatch(
            text: "Techniques de cuisson du boeuf hache et conservation.",
            embedText: "Techniques de cuisson du boeuf hache et conservation.");

        Assert.True(RagEndpoints.ShouldBackfillFuzzyTitleLead("Tu as la recette du boeuf bourguingnon ?", [weakContent]));
    }

    [Theory]
    [InlineData("Il me faut la tartiflette, ingrédients + étapes en version claire.", true)]
    [InlineData("Quels livres de recettes tu vois dans la base ?", false)]
    [InlineData("Compare les desserts chocolatés et dis lequel est le plus simple.", false)]
    public void ShouldRunFocusedLookupEmptySelectionRecovery_detects_precise_zero_result_recovery(
        string query,
        bool expected)
    {
        Assert.Equal(expected, RagEndpoints.ShouldRunFocusedLookupEmptySelectionRecovery(query));
    }

    [Fact]
    public void ShouldRunFocusedLookupEmptySelectionRecovery_skips_explicit_file_queries()
    {
        Assert.False(RagEndpoints.ShouldRunFocusedLookupEmptySelectionRecovery(
            "Cite les conditions de paiement dans `Example_General_Conditions.pdf`."));
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
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            "intfloat/multilingual-e5-base",
            "e5_passage_v1");

        Assert.Equal("Chunk snippet", payload["text"]);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", payload["embed_text"]);
        Assert.Equal("contextual_text_v1", payload["embedding_basis"]);
        Assert.Equal("intfloat/multilingual-e5-base", payload["embedding_model"]);
        Assert.Equal("e5_passage_v1", payload["embedding_input_format"]);
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
            RagEndpoints.BuildMatchDedupKey(first),
            RagEndpoints.BuildMatchDedupKey(duplicatePath));
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
    public void IsNearDuplicatePageOverlap_uses_stable_content_hash_across_different_paths()
    {
        var stableHash = new string('b', 64);
        var text = "This page contains a detailed shared operational procedure with enough text to qualify for near duplicate overlap checks across copied documents and import paths.";
        var first = new RagMatch(
            Score: 0.82,
            DocId: "doc-1",
            DocPath: "Knowledge/Guide.pdf",
            DocName: "Guide.pdf",
            PageStart: 4,
            PageEnd: 4,
            ChunkId: "first",
            ChunkIndex: 3,
            Text: text,
            IngestionVersion: 2,
            HashDoc: stableHash,
            EmbedText: text,
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 5,
            SectionTitle: "Procedure",
            HeadingPath: "Procedure",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var duplicatePath = first with
        {
            DocId = "doc-2",
            DocPath = "Knowledge/Imported/Guide.pdf",
            ChunkId = "second",
            Text = text + " Additional local footer.",
            EmbedText = text + " Additional local footer."
        };

        Assert.True(RagEndpoints.IsNearDuplicatePageOverlap(first, duplicatePath));
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
    public void CalibrateFusedMatches_prefers_primary_text_title_tokens_over_synthetic_route_title()
    {
        var syntheticRouteNeighbor = new RagMatch(
            0.918,
            "doc-a",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            30,
            30,
            "synthetic-route-neighbor",
            30,
            "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            1,
            "hash-a",
            "Matched title_anchor_route: Salade de pates\nIngredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            "title_anchor_route_v1",
            1,
            1,
            "Fromage en des",
            "Fromage en des",
            "unit_exact_v1",
            null,
            null,
            null);
        var primaryTitleTokens = syntheticRouteNeighbor with
        {
            ChunkId = "primary-title-tokens",
            ChunkIndex = 29,
            PageStart = 29,
            PageEnd = 29,
            Text = "Ingredients 150 g de pates, surimi, feta, olives, poivron, huile, vinaigre, sel et poivre. Materiel casserole passoire saladier. Technique faire cuire les pates, les egoutter, laisser refroidir puis melanger.",
            EmbedText = "Ingredients 150 g de pates, surimi, feta, olives, poivron, huile, vinaigre, sel et poivre. Materiel casserole passoire saladier. Technique faire cuire les pates, les egoutter, laisser refroidir puis melanger.",
            EmbeddingBasis = "local_title_token_route_v1"
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [syntheticRouteNeighbor, primaryTitleTokens]);

        Assert.Equal("primary-title-tokens", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_caps_sparse_neighbors_with_partial_quoted_title_coverage()
    {
        var mexicanSalad = new RagMatch(
            0.918,
            "doc-a",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            30,
            30,
            "mexican-salad",
            30,
            "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            1,
            "hash-a",
            "document_name: si-on-cuisinait.pdf\nprevious_context:\nSalade de pates ingredients pates surimi feta olives poivron.\ncontext:\nIngredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre.",
            "sparse_bm25_v1",
            1,
            1,
            "Fromage en des",
            "Fromage en des",
            "unit_exact_v1",
            null,
            null,
            null) with
        {
            MatchedContentCards = [new RagMatchedContentCard("Salade de pates", Signals: ["structured_facts"])]
        };
        var lentilSalad = mexicanSalad with
        {
            Score = 0.8145,
            ChunkId = "lentil-salad",
            PageStart = 27,
            PageEnd = 27,
            ChunkIndex = 27,
            Text = "Salade de lentilles. Ingredients lentilles artichauts poivrons oignon citron huile moutarde sel poivre. Technique ouvrir les boites et melanger.",
            EmbedText = "Salade de lentilles. Ingredients lentilles artichauts poivrons oignon citron huile moutarde sel poivre. Technique ouvrir les boites et melanger."
        };
        var pastaTimbale = mexicanSalad with
        {
            Score = 0.5985,
            ChunkId = "pasta-timbale",
            PageStart = 52,
            PageEnd = 52,
            ChunkIndex = 52,
            Text = "Ingredients 300 g de pates champignons beurre jambon gruyere. Technique preparer une sauce tomate et cuire au four.",
            EmbedText = "Ingredients 300 g de pates champignons beurre jambon gruyere. Technique preparer une sauce tomate et cuire au four."
        };
        var targetPastaSalad = mexicanSalad with
        {
            Score = 0.918,
            ChunkId = "target-pasta-salad",
            PageStart = 29,
            PageEnd = 29,
            ChunkIndex = 29,
            Text = "Ingredients 150 g de pates surimi feta olives poivron huile vinaigre sel poivre. Materiel casserole passoire saladier. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur. © Cemea 2003 Salade de pates3300110033",
            EmbedText = "Ingredients 150 g de pates surimi feta olives poivron huile vinaigre sel poivre. Materiel casserole passoire saladier. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur. © Cemea 2003 Salade de pates3300110033",
            EmbeddingBasis = "local_title_token_route_v1",
            ContentDensityScore = 0.88
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [mexicanSalad, lentilSalad, pastaTimbale, targetPastaSalad]);

        Assert.Equal("target-pasta-salad", calibrated[0].ChunkId);
        Assert.True(Array.FindIndex(calibrated.ToArray(), match => match.ChunkId == "target-pasta-salad")
            < Array.FindIndex(calibrated.ToArray(), match => match.ChunkId == "mexican-salad"));
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
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?");

        Assert.False(RagEndpoints.HasLocalTitleTokenLeadEvidence(
            mexicanSalad.Text,
            mexicanSalad.SectionTitle,
            mexicanSalad.HeadingPath,
            phrases));
        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, mexicanSalad));
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

        Assert.True(RagEndpoints.ShouldRunPreciseTitleLinkedBackfill(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [contextOnlyTop]));
        Assert.False(RagEndpoints.ShouldRunPreciseTitleLinkedBackfill(
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

        var prioritized = RagEndpoints.PrioritizePreciseTitleBackfillSelections(
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

        var prioritized = RagEndpoints.PrioritizePreciseTitleBackfillSelections(
            "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?",
            [genericSauce, otherGeneric, exactLocalTitle],
            [exactLocalTitle]);

        Assert.Equal("exact-local-title", prioritized[0].ChunkId);
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
    public void BuildExactMatchLookupTerms_keeps_quoted_title_for_glued_heading_lookup()
    {
        var terms = RagEndpoints.BuildExactMatchLookupTerms(
            "Give me a clear sheet for \"Safety valve inspection\".");

        Assert.Contains("safety valve inspection", terms);
    }

    [Fact]
    public void BuildExactMatchLookupTerms_keeps_standard_reference_without_year()
    {
        var terms = RagEndpoints.BuildExactMatchLookupTerms(
            "Explique le role du fichier AC par rapport au document principal EN 13135-1.");

        Assert.Contains("en 13135 1", terms);
    }

    [Fact]
    public void ComputeExactMatchScore_keeps_prefix_heading_match_below_short_circuit_threshold()
    {
        var score = RagEndpoints.ComputeExactMatchScore(
            "verbatim_excerpt",
            "safety valve inspection",
            "Safety valve inspectionThe inspection schedule starts here.");

        Assert.InRange(score, 0.96, 0.969);
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
    public void ComputeQuotedLookupCandidateScore_requires_leading_signal_for_long_partial_title()
    {
        var phrases = RagEndpoints.ExtractQuotedLookupPhrases(
            "Give me a clear sheet for \"Alpha Beta Gamma Delta\".");
        var target = "Alpha Beta Gamma Delta procedure. Steps and controls follow.";
        var partial = "Beta, gamma and delta are referenced together in a glossary row.";

        var targetScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, target);
        var partialScore = RagEndpoints.ComputeQuotedLookupCandidateScore(phrases, partial);

        Assert.True(targetScore > 0);
        Assert.Equal(0.0, partialScore);
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
    public void ShouldSupplementSparseWithLexicalFallback_allows_scoped_comparative_choice_queries()
    {
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "Compare trois procedures stables et dis laquelle choisir pour une equipe."));
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille."));

        Assert.False(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(
            null,
            "Compare trois procedures stables et dis laquelle choisir pour une equipe."));
    }

    [Fact]
    public void ShouldAllowSparseAssistForScopedProfileFallback_allows_concrete_topic_document_searches()
    {
        Assert.True(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Retrouve les documents qui parlent de conflit d interets et donne les passages utiles."));
        Assert.True(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a conditions de paiement."));
        Assert.False(RagEndpoints.ShouldAllowSparseAssistForScopedProfileFallback(
            "Je ne sais pas quel document utiliser pour ma question."));
    }

    [Fact]
    public void ShouldTreatQuotedLookupAsTopic_detects_yes_no_and_corpus_topic_quotes()
    {
        Assert.True(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Je veux une reponse oui/non sur `conditions de paiement`. Si les PDF ne permettent pas un oui/non clair, refuse la simplification."));
        Assert.True(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Retrouve les documents qui parlent de `conflit d interets` et donne les passages utiles."));
        Assert.True(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Cherche dans le corpus les tableaux, valeurs, criteres ou listes associes a `conditions de paiement`."));
        Assert.True(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Quels passages du corpus documentent `phthalates DEHP BBP DBP DIBP` et dans quel type de document les trouve-t-on ?"));
        Assert.True(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Je crois que le corpus impose toujours `conditions de paiement`. Verifie si c est vraiment une obligation generale ou si ce n est pas demontre."));

        Assert.False(RagEndpoints.ShouldTreatQuotedLookupAsTopic(
            "Tu peux me faire une fiche claire pour `Patatas Bravas` : ingredients, etapes, temps et source ?"));
    }

    [Fact]
    public void BuildSupplementalSparseLexicalFallbackTerms_prefers_comparative_subject_over_choice_context()
    {
        var terms = RagEndpoints.BuildSupplementalSparseLexicalFallbackTerms(
            "Compare trois plats mijotes francais et dis lequel choisir pour un repas de famille.");
        var operationalChoiceTerms = RagEndpoints.BuildSupplementalSparseLexicalFallbackTerms(
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
        Assert.True(RagEndpoints.ShouldSupplementSparseWithLexicalFallback(
            "generic",
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RagEndpoints.ShouldUseChoiceBetweenLexicalRoute(
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.Equal(
            "deux fournisseurs un tres bon marche mais peu transparent et un plus cher mais mieux documente",
            RagEndpoints.BuildChoiceBetweenRetrievalQuery(
                "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "On doit choisir entre deux fournisseurs : un tres bon marche mais peu transparent et un plus cher mais mieux documente. Comment les textes m aident a arbitrer ?"));
        Assert.True(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "On doit choisir entre deux fournisseurs : un tr\u00e8s bon march\u00e9 mais peu transparent et un plus cher mais mieux document\u00e9. Comment les textes m\u2019aident \u00e0 arbitrer ?"));
        Assert.True(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Peux-tu faire un tableau ancienne source / nouvelle source / changement / impact pour objectif 1,5 degres vs synthese AR6 ?"));
        Assert.True(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Build a table with old source, new source, change and impact for the 2030 baseline vs the updated synthesis."));
        Assert.False(RagEndpoints.ShouldUseChoiceBetweenLexicalRoute(
            "On doit choisir entre US_FAR.pdf et WorldBank.pdf."));
        Assert.False(RagEndpoints.ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(
            "Si les deux documents ne disent pas exactement la meme chose sur exigences de conduite fournisseur UK vs EDP, comment expliquer la nuance sans creer une contradiction artificielle ?"));
    }

    [Fact]
    public void ShouldAllowNavigationCatalogRouteForBroadExploration_requires_scope_and_broad_request()
    {
        Assert.True(RagEndpoints.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Je cherche a avoir un plan pour la semaine avec des options variees.",
            hasScope: true));
        Assert.True(RagEndpoints.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Donne-moi juste une liste d'elements disponibles dans ce dossier.",
            hasScope: true));

        Assert.False(RagEndpoints.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Je cherche a avoir un plan pour la semaine avec des options variees.",
            hasScope: false));
        Assert.False(RagEndpoints.ShouldAllowNavigationCatalogRouteForBroadExploration(
            "Combien de pieces faut-il pour cet assemblage ?",
            hasScope: true));
    }

    [Fact]
    public void BuildSupplementalSparseLexicalFallbackTerms_preserves_accented_subject_terms()
    {
        var terms = RagEndpoints.BuildSupplementalSparseLexicalFallbackTerms(
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
    public void BuildDocumentProfileLexicalTerms_expands_hygienic_food_coupling_terms()
    {
        var terms = RagEndpoints.BuildDocumentProfileLexicalTerms(
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
        var terms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "approche americaine FAR vs Banque mondiale pour la mise en concurrence et l evaluation");
        var ethicsTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "conditions contractuelles vs obligations ethiques fournisseur");
        var conflictTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "conflit d interets fournisseur et confidentialite des donnees personnelles");
        var paymentTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "conditions de paiement fournisseur");
        var lifecycleTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "livraison et reception, resiliation, sous traitants, ESG environnement");
        var operationalTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
            "fournisseur rabais exception regles conformite verifier bon marche transparent mieux documente");
        var supplierConductTerms = RagEndpoints.BuildDocumentProfileLexicalTerms(
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

    [Fact]
    public void CalibrateFusedMatches_uses_supplemental_comparative_phrase_terms()
    {
        const string query = "Compare trois plats mijot\u00e9s fran\u00e7ais et dis lequel choisir pour un repas de famille.";
        var generic = new RagMatch(
            Score: 0.70,
            DocId: "doc-generic",
            DocPath: "Generic/Overview.pdf",
            DocName: "Overview.pdf",
            PageStart: 12,
            PageEnd: 12,
            ChunkId: "generic",
            ChunkIndex: 12,
            Text: "Guide familial avec plusieurs plats fran\u00e7ais et des conseils de service.",
            IngestionVersion: 1,
            HashDoc: "hash-generic",
            EmbedText: "Guide familial avec plusieurs plats fran\u00e7ais et des conseils de service.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Guide",
            HeadingPath: "Guide",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: "content",
            ContentDensityScore: 0.72);
        var target = generic with
        {
            Score = 0.69,
            DocId = "doc-target",
            DocPath = "Target/Recipe.pdf",
            DocName = "Recipe.pdf",
            PageStart = 34,
            PageEnd = 34,
            ChunkId = "target",
            ChunkIndex = 34,
            Text = "Preparation du plat francais: couvrez et laissez mijoter le plat 45 minutes a petit feu. Ingredients: viande, vin, aromates.",
            EmbedText = "Preparation du plat francais: couvrez et laissez mijoter le plat 45 minutes a petit feu. Ingredients: viande, vin, aromates."
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [generic, target]);

        Assert.Equal("target", calibrated[0].ChunkId);
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
    public void CalibrateFusedMatches_prefers_domain_anchor_over_generic_requirement_overlap()
    {
        const string query = "On installe une sorbonne en laboratoire : quelles exigences et quels tests sur site dois-je verifier ?";
        var genericRequirements = TestMatch(
            text: "General structural requirements, calculation basis and durability requirements for equipment.",
            docPath: "Standards/General.pdf",
            chunkId: "generic-requirements",
            score: 0.64);
        var anchoredProcedure = TestMatch(
            text: "Fume cupboards for laboratories: safety and performance requirements. On site test methods define checks to be performed by the user.",
            docPath: "Standards/LabEquipment.pdf",
            chunkId: "anchored-procedure",
            score: 0.60);

        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [genericRequirements, anchoredProcedure]);

        Assert.Equal("anchored-procedure", calibrated[0].ChunkId);
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

        var context = RagEndpoints.BuildContextInfo(content);

        Assert.False(RagEndpoints.LooksLikeNavigationalChunk(content));
        Assert.Equal(RetrievalContentClassifier.ContentRole, context.ContentRole);
        Assert.Null(context.NavigationReason);
        Assert.Equal(0.0, context.NavigationScore);
        Assert.True(context.ContentDensityScore >= 0.70);
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

        RagEndpoints.CleanupLateSelectionBackfills("which evidence documents regulatory compliance", selected);

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

        RagEndpoints.CleanupLateSelectionBackfills(
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

        RagEndpoints.PruneResidualNavigationalSelectionsWhenContentExists("role du fichier AC", selected);

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
        Assert.True(RagEndpoints.ShouldConstrainPreciseTitleLookup("Saumon avec sauce yaourt-menthe"));

        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("Une procedure enfant."));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("comment preparer des concombres pour la semaine"));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("compare concombres romaine et tomates printanieres"));
        Assert.False(RagEndpoints.ShouldConstrainPreciseTitleLookup("Quelles recettes avec des lentilles corail existent dans les PDF ?"));
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
        Assert.Equal(0, hints.ActionabilityScore);
        Assert.True(hints.FragmentScore > 0);
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
    public void ResolveDenseContentCardAttachmentLimit_caps_large_candidate_sets()
    {
        Assert.Equal(0, RagEndpoints.ResolveDenseContentCardAttachmentLimit(0));
        Assert.Equal(20, RagEndpoints.ResolveDenseContentCardAttachmentLimit(20));
        Assert.Equal(64, RagEndpoints.ResolveDenseContentCardAttachmentLimit(64));
        Assert.Equal(48, RagEndpoints.ResolveDenseContentCardAttachmentLimit(200));
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

        RagEndpoints.ApplyAutocut(
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

        Assert.True(RagEndpoints.ContainsDocumentCompanionRelationIntent(
            "Explique le role du fichier AC par rapport au document principal EN 13135-2."));
        Assert.False(RagEndpoints.ShouldShortCircuitAfterExact(
            "Explique le role du fichier AC par rapport au document principal EN 13135-2.",
            [exact]));
    }

    [Fact]
    public void ContainsDocumentCompanionRelationIntent_does_not_treat_bare_ac_as_companion()
    {
        Assert.False(RagEndpoints.ContainsDocumentCompanionRelationIntent(
            "Cherche document AC dans le corpus."));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_search_open_when_exact_results_are_ambiguous()
    {
        var top = new RagMatch(0.98, "doc-1", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "docmeta:1", -1, "CEN TR 15281 2006.pdf", 1, "hash1", "CEN TR 15281 2006.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var second = new RagMatch(0.955, "doc-2", "ATEX/Other 15281.pdf", "Other 15281.pdf", null, null, "docmeta:2", -1, "Other 15281.pdf", 1, "hash2", "Other 15281.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);

        Assert.False(RagEndpoints.ShouldShortCircuitAfterExact([top, second]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_allows_overfetched_clear_winner()
    {
        var top = new RagMatch(1.02, "doc-1", "Ops/Primary.pdf", "Primary.pdf", 4, 4, "exact:1", 1, "Primary reference", 1, "hash1", "Primary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);
        var second = new RagMatch(0.90, "doc-2", "Ops/Secondary.pdf", "Secondary.pdf", 8, 8, "exact:2", 2, "Secondary reference", 1, "hash2", "Secondary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);
        var third = new RagMatch(0.84, "doc-3", "Ops/Tertiary.pdf", "Tertiary.pdf", 9, 9, "exact:3", 3, "Tertiary reference", 1, "hash3", "Tertiary reference", "exact_match_v1", null, null, null, null, "exact_match_entry", null, null, null);

        Assert.True(RagEndpoints.ShouldShortCircuitAfterExact([top, second, third]));
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
    public void ShouldShortCircuitAfterQuotedTitle_prefers_single_strong_quoted_content_hit()
    {
        var hit = TestMatch(
            text: "Gratin dauphinois ingredients potatoes cream garlic. Preparation steps and timing.",
            embedText: "Matched quoted title: Gratin dauphinois\nGratin dauphinois ingredients potatoes cream garlic.",
            docPath: "Cuisine/Top30.pdf",
            chunkId: "quoted",
            score: 0.92);

        Assert.True(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterQuotedTitle(
            "Tu peux me faire une fiche claire pour \"Salade de pates\" : ingredients, etapes, temps et source ?",
            [bodyMention]));
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_detects_precise_unquoted_title_without_broad_intent()
    {
        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Saumon avec sauce yaourt-menthe source",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Saumon avec sauce yaourt-menthe",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche « gateau chocolat courgette ».",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Quelles recettes avec du chocolat peux-tu proposer pour la semaine ?",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Je cherche des recettes pour organiser les repas de la semaine.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.False(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Une procedure enfant.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_keeps_precise_title_before_situational_broad_shape()
    {
        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
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
        Assert.Equal(expected, RagEndpoints.ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(
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

        Assert.True(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
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

        Assert.True(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
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

        Assert.False(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(ingredientMention));
        Assert.True(RagEndpoints.IsWeakResolvedRouteTarget(ingredientMention));
        Assert.False(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
            "Bouillon de volaille source",
            [ingredientMention]));
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

        Assert.True(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(cardBackedHit));
        Assert.False(RagEndpoints.IsWeakResolvedRouteTarget(cardBackedHit));
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

        Assert.True(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(leadInTitle));
        Assert.False(RagEndpoints.IsWeakResolvedRouteTarget(leadInTitle));
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

        Assert.True(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(directRoute));
        Assert.True(RagEndpoints.HasResolvedPreciseTitleSelection(
            ["nouilles sautees legumes-crevettes"],
            directRoute));
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

        Assert.True(RagEndpoints.HasSufficientPreciseContentSelection(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            ["nouilles sautees legumes-crevettes"],
            [directRoute]));

        var accentedQuery = "Donne-moi les nouilles sautées légumes-crevettes du PDF santé travail.";
        Assert.True(RagEndpoints.HasSufficientPreciseContentSelection(
            accentedQuery,
            RagEndpoints.ExtractTitleLookupPhrases(accentedQuery),
            [directRoute]));
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

        var reusableRoutes = RagEndpoints.BuildReusablePreciseTitleRouteMatches([sparseNoise, directRoute]);
        var reusableBackfill = RagEndpoints.BuildReusablePreciseTitleBackfillMatches(
            query,
            phrases,
            reusableRoutes);

        var match = Assert.Single(reusableBackfill);
        Assert.Equal("nouilles-direct", match.ChunkId);
        Assert.True(RagEndpoints.HasReusablePreciseTitleBackfillSelection(
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

        var reusableRoutes = RagEndpoints.BuildReusablePreciseTitleRouteMatches([staleAnchor]);
        var reusableBackfill = RagEndpoints.BuildReusablePreciseTitleBackfillMatches(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            ["nouilles sautees legumes-crevettes"],
            reusableRoutes);

        Assert.Empty(reusableBackfill);
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

        Assert.True(RagEndpoints.HasSufficientPreciseContentSelection(
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

        Assert.False(RagEndpoints.HasSufficientPreciseContentSelection(
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

        Assert.False(RagEndpoints.HasSufficientPreciseContentSelection(
            query,
            ["Sauce moutarde"],
            [weakExactTitle]));
        Assert.False(RagEndpoints.HasSufficientPreciseContentSelection(
            query,
            ["Sauce moutarde"],
            [structuredIngredientMention]));
        Assert.False(RagEndpoints.HasSufficientPreciseContentSelection(
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

        Assert.True(RagEndpoints.HasSufficientPreciseContentSelection(
            query,
            ["Sauce chocolat"],
            [standaloneTitleLead]));
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

        var reusableRoutes = RagEndpoints.BuildReusablePreciseTitleRouteMatches([ingredientMention]);
        var reusableBackfill = RagEndpoints.BuildReusablePreciseTitleBackfillMatches(
            "Donne-moi le bouillon de volaille.",
            ["bouillon de volaille"],
            reusableRoutes);

        Assert.Empty(reusableBackfill);
        Assert.False(RagEndpoints.HasReusablePreciseTitleBackfillSelection(
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

        Assert.False(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(proseMention));
        Assert.True(RagEndpoints.IsWeakResolvedRouteTarget(proseMention));
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
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

        Assert.False(RagEndpoints.ShouldShortCircuitAfterTitleAnchorRoute(
            "Je cherche le gateau chocolat courgette.",
            [borderlineNavigation]));
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

    [Theory]
    [InlineData("VX-12 calibration limits")]
    [InlineData("controle interverrouillage VX-12")]
    [InlineData("Pump maintenance threshold")]
    public void BuildAnswerGuidance_marks_empty_retrieval_as_no_source_match_for_generic_queries(string query)
    {
        var guidance = RagEndpoints.BuildAnswerGuidance(query, Array.Empty<RagMatch>());

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Equal("no_relevant_source_found", guidance.Reason);
        Assert.Equal("no_source_match", guidance.ResponseShape);
        Assert.False(string.IsNullOrWhiteSpace(guidance.QualificationNote));
        Assert.DoesNotContain("recette", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ingredient", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuisine", guidance.QualificationNote, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Un controle standard.", "Quel")]
    [InlineData("A standard control.", "Which")]
    [InlineData("Un control estandar.", "Que")]
    [InlineData("Um controle padrao.", "Qual")]
    [InlineData("Eine Standardkontrolle.", "Welches")]
    [InlineData("Un controllo standard.", "Quale")]
    public void BuildAnswerGuidance_asks_localized_clarification_for_ambiguous_generic_fragment(
        string query,
        string expectedMarker)
    {
        var matches = new[]
        {
            TestMatch(
                text: "Standard control procedure: verify sensor, record result, approve deviation.",
                docPath: "Knowledge/control-procedure.pdf")
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(query, matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.Equal("ambiguous_bare_fragment_requires_scope", guidance.Reason);
        Assert.Equal("clarify", guidance.ResponseShape);
        Assert.Contains(expectedMarker, guidance.ClarifyingQuestion!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnswerGuidance_asks_clarification_for_audience_constrained_generic_fragment()
    {
        var matches = new[]
        {
            TestMatch(
                text: "Document profile with several options designed for children and beginner groups.",
                docPath: "Knowledge/group-activities.pdf",
                embeddingBasis: "document_profile_v1",
                chunkType: "document_profile")
        };

        var guidance = RagEndpoints.BuildAnswerGuidance("Une procedure enfant.", matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.Equal("ambiguous_bare_fragment_requires_scope", guidance.Reason);
        Assert.Equal("clarify", guidance.ResponseShape);
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

        RagEndpoints.PrioritizeExactTitleSelections("alpha beta", selected);

        Assert.Equal("actionable-page", selected[0].ChunkId);
        Assert.True(RagEndpoints.ComputeContentEvidencePriority(actionablePage)
            > RagEndpoints.ComputeContentEvidencePriority(titleList));
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
            RagEndpoints.ComputeContentEvidencePriority(navigationWithoutCard),
            RagEndpoints.ComputeContentEvidencePriority(navigationWithWeakCard),
            precision: 6);
        Assert.True(
            RagEndpoints.ComputeContentEvidencePriority(navigationWithStrongCard)
            > RagEndpoints.ComputeContentEvidencePriority(navigationWithoutCard));
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

        RagEndpoints.PrioritizeExactTitleSelections("Alpha Beta Procedure", selected);

        Assert.Equal("routed-content", selected[0].ChunkId);
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

        RagEndpoints.PromoteActionableSameDocumentEvidence(selected);

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

        RagEndpoints.PromoteActionableSameDocumentEvidence(selected);

        Assert.Equal("short-header", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_local_actionable_content_over_route_title_list()
    {
        var titleList = TestMatch(
            text: "Contents Safety Valves 12 Gamma Delta 18 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            embedText: "Matched title_anchor_route: Safety Valves\nContents Safety Valves 12 Gamma Delta 18 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            page: 2,
            score: 1.02,
            chunkId: "title-list",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.76,
            ContentDensityScore = 0.30
        };
        var actionablePage = TestMatch(
            text: "Safety Valve. Materials: gasket wrench sealant. Procedure: 1. Isolate pressure. 2. Replace the valve. 3. Leak-test the assembly.",
            embedText: "Matched direct_title_token_route: safety valves\nSafety Valve. Materials: gasket wrench sealant. Procedure: 1. Isolate pressure. 2. Replace the valve. 3. Leak-test the assembly.",
            page: 12,
            score: 0.98,
            chunkId: "actionable-page",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { titleList, actionablePage };

        RagEndpoints.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Safety Valves\".", selected);

        Assert.Equal("actionable-page", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_exact_local_phrase_over_dense_token_overlap()
    {
        var denseTokenOverlap = TestMatch(
            text: "Procedure. The pressure circuit uses a calibrated relief spring and valve body. Materials: wrench gasket sealant. Steps: isolate, replace, test.",
            embedText: "Matched direct_title_token_route: pressure relief\nProcedure. The pressure circuit uses a calibrated relief spring and valve body.",
            page: 4,
            score: 1.02,
            chunkId: "dense-token-overlap",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 1.0
        };
        var exactLocalPhrase = TestMatch(
            text: "PRESSURE RELIEF. Materials: gauge valve gasket. Procedure: 1. Depressurize. 2. Inspect the relief path. 3. Record the setting.",
            embedText: "Matched title_anchor_route: Pressure Relief\nPRESSURE RELIEF. Materials: gauge valve gasket. Procedure: 1. Depressurize.",
            page: 9,
            score: 0.94,
            chunkId: "exact-local-phrase",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { denseTokenOverlap, exactLocalPhrase };

        RagEndpoints.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Pressure Relief\".", selected);

        Assert.Equal("exact-local-phrase", selected[0].ChunkId);
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

        Assert.False(RagEndpoints.HasProfileTitleHint(unconfirmedDirectRoute));
        Assert.False(RagEndpoints.IsResolvedTitleOrNavigationRoute(unconfirmedDirectRoute));
        Assert.False(RagEndpoints.HasResolvedPreciseTitleSelection(["Alpha Beta"], unconfirmedDirectRoute));
        Assert.True(RagEndpoints.HasProfileTitleHint(confirmedDirectRoute));
        Assert.True(RagEndpoints.DirectTitleTokenRouteHasTargetTitleEvidence(confirmedDirectRoute));
        Assert.True(RagEndpoints.IsResolvedTitleOrNavigationRoute(confirmedDirectRoute));
        Assert.True(RagEndpoints.HasResolvedPreciseTitleSelection(["Alpha Beta"], confirmedDirectRoute));
        Assert.True(RagEndpoints.HasProfileTitleHint(stemmedDirectRoute));
        Assert.True(RagEndpoints.DirectTitleTokenRouteHasTargetTitleEvidence(stemmedDirectRoute));
        Assert.True(RagEndpoints.HasResolvedPreciseTitleSelection(["gateau chocolat courgett"], stemmedDirectRoute));
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
    public void CalibrateFusedMatches_penalizes_direct_title_route_when_title_is_only_ingredient()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var titleChunk = TestMatch(
            text: "Bouillon de volaille. Ingredients: carcasse de volaille, carotte, oignon, celeri et bouquet garni. Preparation: couvrir d'eau, mijoter, filtrer puis refroidir.",
            embedText: "Bouillon de volaille. Ingredients and preparation.",
            docPath: "Cuisine/Base.pdf",
            chunkId: "title-chunk",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Bouillon de volaille source",
            [ingredientMention, titleChunk]);

        Assert.Equal("title-chunk", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_card_confirmed_direct_title_route_over_ingredient_mention()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var cardBackedRoute = TestMatch(
            text: "Grains de poivre. Ingredients: carcasse de volaille, legumes, aromates et eau froide. Preparation: couvrir, mijoter, filtrer puis refroidir.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nGrains de poivre and bouillon sauce content card.",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            chunkId: "card-backed-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.87) with
        {
            MatchedContentCards = [new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES")]
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Bouillon de volaille source",
            [ingredientMention, cardBackedRoute]);

        Assert.Equal("card-backed-route", calibrated[0].ChunkId);
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

        RagEndpoints.PrioritizeExactTitleSelections("Saumon avec sauce yaourt-menthe", selected);

        Assert.False(RagEndpoints.DirectTitleTokenRouteHasStrongLeadEvidence(weakDirectRoute));
        Assert.Equal("exact-title", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_short_suffix_title_phrase()
    {
        var generic = TestMatch(
            text: "Alpha procedure materials and steps from a generic guide.",
            docPath: "Docs/general.pdf",
            chunkId: "generic",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var shortSuffix = TestMatch(
            text: "ALPHA A JD. Materials: gasket and wrench. Procedure: inspect and record.",
            embedText: "Matched title_anchor_route: Alpha a JD\nALPHA A JD. Materials: gasket and wrench. Procedure: inspect and record.",
            docPath: "Docs/short-suffix.pdf",
            chunkId: "short-suffix",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.91);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "alpha a jd",
            [generic, shortSuffix],
            "C'est quoi l'alpha a JD ?");

        Assert.Equal("short-suffix", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_terminal_short_title_disambiguator()
    {
        var modeB = TestMatch(
            text: "Access Mode B. Procedure: validate the alternate channel and record the mode B result.",
            embedText: "Matched title_anchor_route: Access Mode B\nAccess Mode B. Procedure: validate the alternate channel.",
            docPath: "Docs/mode-b.pdf",
            chunkId: "mode-b",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var modeA = TestMatch(
            text: "Access Mode A. Procedure: enable the primary channel and record the mode A result.",
            embedText: "Matched title_anchor_route: Access Mode A\nAccess Mode A. Procedure: enable the primary channel.",
            docPath: "Docs/mode-a.pdf",
            chunkId: "mode-a",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.91);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "access mode a",
            [modeB, modeA],
            "Give me the procedure for access mode A from the manual.");

        Assert.Equal("mode-a", calibrated[0].ChunkId);
    }

    [Fact]
    public void ResolveRetriever_maps_fuzzy_title_lead_separately_from_dense_qdrant()
    {
        var fuzzy = TestMatch(
            text: "Alpha beta target body.",
            embedText: "Matched fuzzy_title_lead: Alpha Beta\nAlpha beta target body.",
            embeddingBasis: "fuzzy_title_lead_v1",
            chunkType: "section_window_v1");
        var localTitle = TestMatch(
            text: "Alpha beta target body.",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1");

        Assert.Equal("fuzzy_title_lead", RagEndpoints.ResolveRetriever(fuzzy));
        Assert.Equal("local_title_token_route", RagEndpoints.ResolveRetriever(localTitle));
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
    public void CalibrateFusedMatches_prefers_document_hint_after_document_type_reference()
    {
        var genericGuide = TestMatch(
            text: "Compote de pommes ingredients and steps from a generic guide.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var hintedGuide = TestMatch(
            text: "Compote de pommes ingredients and steps from the named guide.",
            docPath: "Cuisine/facilitemps.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "compote de pommes",
            [genericGuide, hintedGuide],
            "Comment faire la compote de pommes du guide Facilitemps ?");

        Assert.Equal("hinted-guide", calibrated[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_document_hint_over_homonymous_exact_title()
    {
        var genericExact = TestMatch(
            text: "COMPOTE DE POMMES Ingredients and steps from another recipe book.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-exact",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 1.02);
        var hintedGuide = TestMatch(
            text: "COMPOTE DE POMMES DE BASE Ingredients and steps from the named guide.",
            docPath: "Cuisine/facilitemps.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918);
        var selected = new List<RagMatch> { genericExact, hintedGuide };

        RagEndpoints.PrioritizeFinalSelections(
            "Comment faire la compote de pommes du guide Facilitemps ?",
            selected);

        Assert.Equal("hinted-guide", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_candidate_document_name_as_implicit_hint()
    {
        var otherRobot = TestMatch(
            text: "PAIN SANS GLUTEN AUX GRAINES ET HOUMOUS AUX PETITS POIS Lancez le programme pate.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "other-robot",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var hintedButOffTopic = TestMatch(
            text: "BECHAMEL Ingredients lait, farine, beurre et noix de muscade. Placer le fouet dans le recipient.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "hinted-off-topic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.94);
        var chefbot = TestMatch(
            text: "HOUMOUS DE BETTERAVE Preparation au robot culinaire avec pois chiches, tahini, ail, citron vert et eau glacee.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "chefbot",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.86);
        var selected = new List<RagMatch> { otherRobot, hintedButOffTopic, chefbot };

        RagEndpoints.PrioritizeFinalSelections(
            "Je veux faire le houmous au Chefbot, tu me sors les etapes ?",
            selected);

        Assert.Equal("chefbot", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_treat_common_title_words_as_implicit_document_hints()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz basmati et 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/curry-basics.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var focusedCurry = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Servez le curry de crevettes avec du riz basmati.",
            docPath: "Cuisine/robot-guide.pdf",
            chunkId: "focused-curry",
            embeddingBasis: "linked_context_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericRice, focusedCurry };

        RagEndpoints.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("focused-curry", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_linked_context_that_starts_mid_procedure()
    {
        var continuation = TestMatch(
            text: "BOEUF BOURGUIGNON Paris et cuire encore 30 min. 6 Degustez chaud avec des pommes vapeur.",
            docPath: "Cuisine/moulinex.pdf",
            chunkId: "linked-continuation",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.918);
        var complete = TestMatch(
            text: "Boeuf bourguignon Pour 4 personnes ingredients. Preparation: degraisser la viande, saisir, ajouter les legumes, le vin et laisser mijoter.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "complete-recipe",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var selected = new List<RagMatch> { continuation, complete };

        RagEndpoints.PrioritizeFinalSelections(
            "C'est quoi les grandes etapes du boeuf bourguignon ?",
            selected);

        Assert.Equal("complete-recipe", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_trailing_acronym_as_implicit_document_hint()
    {
        var genericGuide = TestMatch(
            text: "Alpha beta ingredients and settings from a generic guide.",
            docPath: "Docs/general-guide.pdf",
            chunkId: "generic-guide",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91);
        var hintedGuide = TestMatch(
            text: "Alpha beta ingredients and settings from the named guide.",
            docPath: "Docs/VND-service-guide.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.88);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "alpha beta",
            [genericGuide, hintedGuide],
            "Pour les alpha beta VND, quels sont les reglages ?");

        Assert.Equal("hinted-guide", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_tolerates_single_letter_document_hint_typo()
    {
        var genericProfile = TestMatch(
            text: "Document profile for a broad guide with desserts, planning and quick meals.",
            docPath: "Docs/family-planning-guide.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.97);
        var hintedProfile = TestMatch(
            text: "Document profile for a short labelled collection with desserts and balanced meals.",
            docPath: "Docs/livre-recette-sist-2025-web.pdf",
            chunkId: "hinted-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.82);

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "compare desserts sante",
            [genericProfile, hintedProfile],
            "Compare les desserts sante du PDF SIIST.");

        Assert.Equal("hinted-profile", calibrated[0].ChunkId);
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

        RagEndpoints.PrioritizeDocumentHintSelections("Comment appliquer alpha du guide Acme ?", selected);

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

        RagEndpoints.PrioritizeDocumentHintSelections("Comment appliquer alpha du guide Acme ?", selected);

        Assert.Equal("content", selected[0].ChunkId);
        Assert.Equal("profile", selected[1].ChunkId);
    }

    [Fact]
    public void ShouldSkipDocumentProfileSearchForPreciseLookup_keeps_profile_assist_for_document_hint()
    {
        Assert.False(RagEndpoints.ShouldSkipDocumentProfileSearchForPreciseLookup(
            "Donne-moi la procedure Alpha Beta du guide Acme."));
    }

    [Fact]
    public void BuildDocumentHintScopedBackfillQuery_requires_hint_and_focused_subject()
    {
        var focused = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la procedure Alpha Beta du guide Acme.",
            "Alpha Beta");
        var naturalLanguageHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la recette du coq au vin dans le livre international.",
            "coq au vin");
        var explicitFileHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Dans ANSI Z535.1.pdf, trouve un tableau, une liste ou une section structuree et explique ce que tu peux lire avec confiance.",
            "trouve tableau liste section structuree");
        var bareFileMentionHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Fais un resume prudent de ANSI Z535.2.pdf en distinguant ce qui est sur de ce qui depend de la qualite OCR.",
            "resume prudent qualite OCR");
        var extensionlessStrongDocumentHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            "retrouver annexes appendices");
        var possessiveExtensionlessStrongDocumentHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Resume uniquement ce que les annexes de ACME_Report_2024_Appendices ajoutent au document principal.",
            "resume annexes document principal");
        var noHint = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Donne-moi la procedure Alpha Beta.",
            "Alpha Beta");
        var noSubject = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
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
    public void ShouldUseExplicitDocumentScopedBackfillRoute_detects_file_scoped_sourcing_query()
    {
        const string query = "Donne une reponse tres courte sur `conditions generales de fourniture EDP : commande, livraison, prix, paiement, responsabilite, resiliation` a partir de `EDP_Group_Portugal_ENG_General_Conditions_for_the_Supply_of_Goods_and_Services_Sept25.pdf`, mais avec une citation exploitable et une phrase indiquant la limite de la preuve.";
        var scopedQuery = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            query,
            "conditions generales de fourniture EDP commande livraison prix paiement responsabilite resiliation");

        Assert.False(string.IsNullOrWhiteSpace(scopedQuery));
        Assert.True(RagEndpoints.ShouldUseExplicitDocumentScopedBackfillRoute(query, scopedQuery));
        var extensionlessScopedQuery = RagEndpoints.BuildDocumentHintScopedBackfillQuery(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            "retrouver annexes appendices");
        Assert.True(RagEndpoints.ShouldUseExplicitDocumentScopedBackfillRoute(
            "Dans NIST_SP_800_160v1r1_SSE_Appendices, peux-tu retrouver ou commencent les annexes ou appendices ?",
            extensionlessScopedQuery));
        Assert.False(RagEndpoints.ShouldUseExplicitDocumentScopedBackfillRoute(
            "Donne-moi la procedure Alpha Beta du guide Acme.",
            "Alpha Beta"));
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

        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(query, [genericContent]));
        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(query, [hintedProfile]));
        Assert.False(RagEndpoints.ShouldRunDocumentHintScopedBackfill(query, [genericContent, hintedContent]));
        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, []));
        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RagEndpoints.ShouldRunDocumentHintScopedBackfill(explicitFileQuery, [explicitFileContent]));
        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(bareExplicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RagEndpoints.ShouldRunDocumentHintScopedBackfill(bareExplicitFileQuery, [explicitFileContent]));
        Assert.True(RagEndpoints.ShouldRunDocumentHintScopedBackfill(extensionlessExplicitFileQuery, [wrongSiblingFileContent]));
        Assert.False(RagEndpoints.ShouldRunDocumentHintScopedBackfill(extensionlessExplicitFileQuery, [extensionlessExplicitFileContent]));
    }

    [Fact]
    public void PrioritizeFinalSelections_reorders_late_high_confidence_title_evidence()
    {
        var partialIngredientHit = TestMatch(
            text: "Alpha vegetable cream mentions musquee as an ingredient in a different preparation.",
            docPath: "Docs/generic-guide.pdf",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var exactTitleHit = TestMatch(
            text: "Tarte fine a la courge musquee. Ingredients: dough, squash and seasoning. Preparation: bake until crisp.",
            docPath: "Docs/specific-guide.pdf",
            chunkId: "exact-title",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.91);
        var selected = new List<RagMatch> { partialIngredientHit, exactTitleHit };

        RagEndpoints.PrioritizeFinalSelections("Comment faire la tarte fine a la courge musquee ?", selected);

        Assert.Equal("exact-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_promotes_comparative_profile_with_constraint_coverage()
    {
        var genericProfile = TestMatch(
            text: "Document profile with recettes salees and a passing mention of enfants around a family meal.",
            docPath: "Cuisine/generic-recipes.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 1.01);
        var targetedProfile = TestMatch(
            text: "Document profile with 57 recettes pour enfants, animateurs, temps, materiel, pizzas rigolotes, cake olives and tarte tomate.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            chunkId: "target-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.72);
        var selected = new List<RagMatch> { genericProfile, targetedProfile };

        RagEndpoints.PrioritizeFinalSelections(
            "Compare trois recettes salees pour enfants : temps, materiel, risque de ratage.",
            selected);

        Assert.Equal("target-profile", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_document_hint_over_generic_comparative_profile()
    {
        var genericProfile = TestMatch(
            text: "Document profile with desserts sante, anti gaspillage, chocolat, collations, prevention and several comparison cues.",
            docPath: "Cuisine/family-desserts-guide.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.91);
        var hintedProfile = TestMatch(
            text: "Document profile for a broad labelled workplace cookbook with balanced meals and varied preparations.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "hinted-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.59);
        var selected = new List<RagMatch> { genericProfile, hintedProfile };

        RagEndpoints.PrioritizeFinalSelections(
            "Compare les desserts sante/anti-gaspi du PDF SIIST.",
            selected);

        Assert.Equal("hinted-profile", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_weak_direct_title_token_route_for_broad_comparison()
    {
        var weakDirectRoute = TestMatch(
            text: "This body mentions dessert and chocolate, but the route title is not a standalone title.",
            embedText: "Matched direct_title_token_route: dessert chocolat\nThis body mentions dessert and chocolate, but not as a heading.",
            docPath: "Cuisine/generic-desserts.pdf",
            chunkId: "weak-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.94);
        var concreteDessert = TestMatch(
            text: "Fondant au chocolat Pour 6 personnes. Preparation simple, ingredients and cooking time.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "concrete-dessert",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90);
        var selected = new List<RagMatch> { weakDirectRoute, concreteDessert };

        RagEndpoints.PrioritizeFinalSelections(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?",
            selected);

        Assert.Equal("concrete-dessert", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_only_weak_content_card_title_for_broad_comparison()
    {
        var weakCardTitle = TestMatch(
            text: "Ingredients: 200 g chocolat, farine, butter, sugar and eggs. Bake, let cool, then cut into portions.",
            docPath: "Cuisine/children-desserts.pdf",
            chunkId: "weak-card",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Laisser refroidir dans le plat et couper en",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var concreteDessertCard = TestMatch(
            text: "Profiteroles au chocolat. Pour 8 personnes. Preparation simple, ingredients, cooking time and serving notes.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "concrete-card",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Profiteroles au chocolat",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { weakCardTitle, concreteDessertCard };

        RagEndpoints.PrioritizeFinalSelections(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?",
            selected);

        Assert.Equal("concrete-card", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_use_weak_card_signals_as_title_evidence()
    {
        var weakCardSignal = TestMatch(
            text: "Ingredients and procedure notes mention alpha beta once, but this is not the named item.",
            docPath: "Docs/weak-card.pdf",
            chunkId: "weak-card-signal",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Prepare before opening the cabinet",
                    Signals: ["alpha", "beta", "procedure"])
            ]
        };
        var concreteTitle = TestMatch(
            text: "Alpha Beta Procedure. Materials: gloves and tray. Failure risk: overheating. Setup notes: cool before handling.",
            docPath: "Docs/concrete-title.pdf",
            chunkId: "concrete-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Alpha Beta Procedure",
                    Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { weakCardSignal, concreteTitle };

        RagEndpoints.PrioritizeFinalSelections(
            "Compare alpha beta procedure options: material, risks and setup.",
            selected);

        Assert.Equal("concrete-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_moves_concrete_card_ahead_of_procedural_card_in_broad_dessert_choice()
    {
        var muffins = TestMatch(
            text: "Bake muffins with fruit and sugar. Six servings, short preparation and baking time.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "muffins",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "MUFFINS AUX CERISES ET AMANDES",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var truffles = TestMatch(
            text: "TRUFFES CHOCOLAT. Bake biscuits and finish chocolate truffles.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "truffles",
            score: 0.918,
            page: 2) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "TRUFFES CHOCOLAT",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var proceduralCard = TestMatch(
            text: "Ingredients: 200 g chocolat, farine, beurre, sucre, eggs and nuts. Let cool then cut into pieces.",
            docPath: "Cuisine/children-desserts.pdf",
            chunkId: "procedural-card",
            score: 0.90,
            page: 84) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            SectionTitle = "FICHE-DESSERT CHOCOLAT",
            HeadingPath = "FICHE-DESSERT CHOCOLAT",
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Laisser refroidir dans le plat et couper en",
                    Signals: ["scale_basis", "quantity_list", "structured_facts", "chocolat", "plat", "beurre"])
            ]
        };
        var profiteroles = TestMatch(
            text: "Profiteroles au chocolat Pour 8 personnes. Preparation simple, ingredients, cooking time and serving notes.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "profiteroles",
            score: 0.905,
            page: 29) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Profiteroles au chocolat",
                    Signals: ["quantity_list", "structured_facts", "Profiteroles au chocolat", "chocolat"])
            ]
        };
        var selected = new List<RagMatch> { muffins, truffles, proceduralCard, profiteroles };

        RagEndpoints.PrioritizeFinalSelections(
            "Si je veux un dessert chocolaté ou crémeux, lequel est le plus simple ?",
            selected);

        Assert.Equal(["muffins", "profiteroles", "truffles", "procedural-card"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_unqualified_title_over_unrequested_qualified_variant()
    {
        var qualifiedVariant = TestMatch(
            text: "Creme brulee aux champignons. Ingredients: mushrooms, onion, cream and parmesan.",
            embedText: "Matched direct_title_token_route: Creme brulee aux champignons\nCreme brulee aux champignons.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee aux champignons",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var exactTitle = TestMatch(
            text: "Creme brulee. Ingredients: egg yolks, sugar, cream and vanilla.",
            embedText: "Matched direct_title_token_route: Creme brulee\nCreme brulee.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.90) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { qualifiedVariant, exactTitle };

        RagEndpoints.PrioritizeFinalSelections(
            "Il y a plusieurs cremes brulees ? Compare-les si oui.",
            selected);

        Assert.Equal("exact", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_unqualified_direct_route_over_unrequested_qualified_variant()
    {
        var qualifiedVariant = TestMatch(
            text: "Champignons a la grecque. Creme brulee aux champignons. Ingredients: mushrooms, onion, cream and parmesan.",
            embedText: "Matched direct_title_token_route: Creme brulee aux champignons\nChampignons a la grecque. Creme brulee aux champignons.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee aux champignons",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var exactTitle = TestMatch(
            text: "Creme brulee. Ingredients: egg yolks, sugar, cream and vanilla.",
            embedText: "Matched direct_title_token_route: Creme brulee\nCreme brulee.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.90) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Il y a plusieurs cremes brulees ? Compare-les si oui.",
            [qualifiedVariant, exactTitle]);

        Assert.Equal("exact", calibrated[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_overqualified_homonym_for_short_comparison_subject()
    {
        var overqualified = TestMatch(
            text: "Clafoutis de legumes sans gluten. Ingredients: vegetables, eggs, cream and cheese.",
            docPath: "Cuisine/vegetables.pdf",
            chunkId: "vegetables",
            score: 0.91) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Clafoutis de legumes sans gluten",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var focusedVariant = TestMatch(
            text: "Clafoutis aux cerises. Ingredients: cherries, sugar, flour, milk and eggs.",
            docPath: "Cuisine/cherries.pdf",
            chunkId: "cherries",
            score: 0.87) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Clafoutis aux cerises",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { overqualified, focusedVariant };

        RagEndpoints.PrioritizeFinalSelections(
            "Compare les clafoutis et dis ce qui change.",
            selected);

        Assert.Equal("cherries", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_is_noop_without_specialized_signal()
    {
        var structurallyClean = TestMatch(
            text: "Ingredients: alpha beta gamma. Preparation: mix and serve.",
            docPath: "Docs/clean.pdf",
            chunkId: "clean",
            score: 0.72);
        var rerankedFirst = TestMatch(
            text: "Operational summary that should stay first for a broad query.",
            docPath: "Docs/reranked.pdf",
            chunkId: "reranked-first",
            score: 0.68);
        var selected = new List<RagMatch> { rerankedFirst, structurallyClean };

        RagEndpoints.PrioritizeFinalSelections("Que peux-tu me dire sur ce dossier ?", selected);

        Assert.Equal("reranked-first", selected[0].ChunkId);
        Assert.Equal("clean", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_quoted_phrase_in_section_title_over_body_mention()
    {
        var bodyMention = TestMatch(
            text: "Tarte au citron. Pendant ce temps, preparez la creme au citron pour garnir la tarte.",
            docPath: "Cuisine/body-mention.pdf",
            chunkId: "body-mention",
            score: 1.02);
        var sectionTitle = TestMatch(
            text: "AU CITRON Mixez en vitesse 6 pendant 1 min. Repartissez la creme dans des ramequins.",
            docPath: "Cuisine/title-hit.pdf",
            chunkId: "section-title",
            score: 0.91) with
        {
            SectionTitle = "CREME AU CITRON",
            HeadingPath = "CREME AU CITRON"
        };
        var selected = new List<RagMatch> { bodyMention, sectionTitle };

        RagEndpoints.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \"Creme au citron\" : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("section-title", selected[0].ChunkId);
        Assert.True(RagEndpoints.ComputeQuotedTitleAnchorSignal(
            "fiche pour \"Creme au citron\"",
            sectionTitle) > 0.0);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_quoted_phrase_lead_over_generic_same_token_body()
    {
        var genericSauce = TestMatch(
            text: "SAUCE CHOCOLAT. Ingredients: creme fraiche, chocolat noir. Suggestion: servir avec une sauce au fromage blanc.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "generic",
            score: 1.02);
        var exactLead = TestMatch(
            text: "12 min SAUCE AU POIVRE SAUCE AUX 4 FROMAGES. Ingredients: parmesan, pecorino, comte, gorgonzola. Preparation: mixer puis cuire.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            score: 0.91,
            chunkType: "exact_match_entry",
            embeddingBasis: "exact_match_v1");
        var selected = new List<RagMatch> { genericSauce, exactLead };

        RagEndpoints.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Sauce aux 4 fromages \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("exact", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_near_exact_ocr_title_over_qualified_prefix_title()
    {
        const string query = "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : materials, etapes, temps et source ?";
        var qualifiedPrefix = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE Ingredients: soja texture, tomates, carottes. Preparation: hydrater le soja puis mijoter.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "local_title_token_route_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var linkedQualified = TestMatch(
            text: "Preparation: ajouter le soja texture et cuire 10 minutes.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "linked-qualified",
            embeddingBasis: "linked_context_v1",
            score: 1.00) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            MatchedContentCards = [new RagMatchedContentCard("SAUCE BOLOGNAISE AU SOJA TEXTURE", Signals: ["structured_facts"])]
        };
        var ocrTitle = TestMatch(
            text: "SAUCE BOLOGNANE Accompagnements Viandes Boeuf Modes de preparation Frire Cuire Categories de recettes Pour 4 portions SAUCE BOLOGNANE",
            docPath: "Cuisine/target.pdf",
            chunkId: "ocr-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.8775) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.545
        };
        var selected = new List<RagMatch> { qualifiedPrefix, linkedQualified, ocrTitle };
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.True(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, ocrTitle));

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("ocr-title", selected[0].ChunkId);
    }

    [Fact]
    public void DocumentOverview_precise_title_lookup_keeps_quoted_pdf_filename()
    {
        const string query = "Fais-moi une synthese utile de `ISO 19011 2011 Lignes directrices pour l'audit des systemes de management.pdf` : a quoi sert ce document ?";

        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.Contains(phrases, phrase =>
            phrase.Contains("19011", StringComparison.OrdinalIgnoreCase)
            && phrase.Contains("pdf", StringComparison.OrdinalIgnoreCase));
        Assert.False(RagEndpoints.ShouldSkipPreciseTitleLookupForDocumentOverview(query, hasDocScope: false, mode: "balanced"));
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

        Assert.True(RagEndpoints.ShouldRunStructuredContinuationBackfill(query, [anchor]));

        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, [anchor], limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            [continuation],
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(continuationId, linked.ChunkId);
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

        RagEndpoints.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);

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

        RagEndpoints.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);

        Assert.Single(selected);
        Assert.Equal("qualified-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_local_title_footer_over_linked_ingredient_mention()
    {
        var linkedIngredient = TestMatch(
            text: "RISOTTO PETITS POIS ET JAMBON. Ingredients: 300 g de riz, 90 cl de bouillon de volaille, parmesan. Preparation: cuire le risotto.",
            docPath: "Cuisine/ingredient.pdf",
            chunkId: "linked-ingredient",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var localTitleFooter = TestMatch(
            text: "Laisser mijoter une carcasse avec aromates. Passer le bouillon au tamis. Ingredients: carcasse de volaille, oignons, ail, laurier, thym, carottes. BOUILLON DE VOLAILLE SAUCES 3334 PREPARATION.",
            docPath: "Cuisine/title.pdf",
            chunkId: "local-title",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.9767
        };
        var selected = new List<RagMatch> { linkedIngredient, localTitleFooter };

        RagEndpoints.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Bouillon de volaille \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("local-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_navigation_route_page_over_previous_linked_context_for_footer_title()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Salade mexicaine\" : ingredients, etapes, temps et source ?";
        var previousLinkedRecipe = TestMatch(
            text: "Ingredients 150 g de pates, surimi, feta, olives, poivron. Materiel casserole passoire saladier. Technique faire cuire les pates, egoutter, laisser refroidir puis melanger. Cemea 2003 Salade de pates3300110033",
            embedText: "Matched linked_anchor_title: Salade mexicaine\nIngredients 150 g de pates, surimi, feta, olives, poivron. Technique cuire les pates. Cemea 2003 Salade de pates3300110033",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 29,
            chunkId: "previous-linked",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.882) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.88
        };
        var routedRecipePage = TestMatch(
            text: "Ingredients 1 boite 4/4 de haricots rouges, ananas, mais, poivron, olives, tomates, oignon, huile, vinaigre, moutarde, persil, sel et poivre. Materiel ouvre-boites passoire saladier fourchette couteau planche. Technique nettoyer les boites de conserve, les ouvrir, rincer et egoutter les haricots rouges, egoutter les ananas, laver le poivron et les tomates, couper les legumes, melanger le tout dans le saladier, preparer une vinaigrette relevee et saupoudrer de persil hache. Suggestions la quantite des ingredients peut varier suivant les gouts. Cemea 2003 Salade mexicaine 440044",
            embedText: "Matched navigation_route: Salade mexicaine\nIngredients 1 boite 4/4 de haricots rouges, ananas, mais, poivron, olives, tomates, oignon, huile, vinaigre, moutarde, persil, sel et poivre. Materiel ouvre-boites passoire saladier fourchette couteau planche. Technique nettoyer les boites de conserve, les ouvrir, rincer et egoutter les haricots rouges, egoutter les ananas, laver le poivron et les tomates, couper les legumes, melanger le tout dans le saladier, preparer une vinaigrette relevee et saupoudrer de persil hache. Cemea 2003 Salade mexicaine 440044",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 30,
            chunkId: "mexican-navigation",
            embeddingBasis: "navigation_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { previousLinkedRecipe, routedRecipePage };
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, previousLinkedRecipe));
        Assert.True(RagEndpoints.NavigationRouteHasTargetTitleEvidence(routedRecipePage));
        Assert.False(RagEndpoints.IsWeakResolvedRouteTarget(routedRecipePage));
        Assert.True(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, routedRecipePage));

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("mexican-navigation", selected[0].ChunkId);
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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { wrongPrevious, continuation },
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(continuationId, linked.ChunkId);

        RagEndpoints.MergeStructuredContinuationSelections(query, selected, continuations, topK: 2);

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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous, earlyIngredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == previousId);
        Assert.Contains(continuations, match => match.ChunkId == earlyIngredientsId);

        RagEndpoints.MergeStructuredContinuationSelections(query, selected, continuations, topK: 3);

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

        Assert.True(RagEndpoints.ShouldRunStructuredContinuationBackfill(query, [anchor]));

        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, [anchor], limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { preparation, ingredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == preparationId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);

        RagEndpoints.MergeStructuredContinuationSelections(query, selected, continuations, topK: 3);

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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
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
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { steps, ingredients },
            limit: 4);

        Assert.Contains(continuations, match => match.ChunkId == stepsId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_nobilia_same_page_ingredients_after_title_route()
    {
        const string anchorId = "e95cf988-88c4-7c1d-9d1f-0cd599ca64f6";
        const string previousId = "c61abeee-c8d1-1cbf-c007-f4db33d8b7cc";
        const string prepId = "18f89121-2ce3-7dd9-0984-2a5da4b7464b";
        const string sauceId = "3c0b9e09-1502-d48f-aff1-5191245d8856";
        const string ingredientsId = "ad15c7b7-9545-6360-3302-be198033ac7b";
        const string sectionTitle = "4 Homes ila, Egouttez-les et coupez-les en rondelles epaisses. Mettez-les dans un saladier, ajoutez-y les";
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Brochettes de poulet grille a l'indonesienne. L'Indonesie actuelle appartenait aux colonies neerlandaises d'Extreme-Orient. Pour 6 personnes 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched local title token route",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = prepId,
            SameSectionChunkId = prepId
        };
        var prep = TestMatch(
            text: "Mettez tous les ingredients necessaires a la marinade dans un grand saladier et melangez avec une cuillere. Ajoutez la viande et badigeonnez-la delicatement de marinade. Recouvrez d'un film alimentaire et mettez au refrigerateur 3-12 heures.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: prepId,
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.873) with
        {
            ChunkIndex = 52,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };
        var previous = TestMatch(
            text: "Pour 4 personnes poivre noir du moulin. Coupez les filets de harengs en petits morceaux et poivrez-les. Pressez le jus des citrons verts.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 40,
            chunkId: previousId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.882) with
        {
            ChunkIndex = 50,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = anchorId,
            SameSectionChunkId = anchorId
        };
        var ingredients = TestMatch(
            text: "CONSEIL Faites mariner les brochettes des le matin pour les servir le soir. INGREDIENTS 1,5 kg de blancs de poulet huile pour la grille. Pour la marinade 3 echalotes, 2 gousses d'ail, piment en poudre, coriandre, gingembre, sauce de soja, vinaigre et huile.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.871) with
        {
            ChunkIndex = 54,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70
        };
        var sauce = TestMatch(
            text: "Mettez les cacahouetes, l'oignon, l'ail, les flocons de piment, le gingembre, le sucre et le jus de citron dans un mixeur et reduisez en puree tres lisse. Incorporez environ 375 ml d'eau chaude afin d'obtenir une sauce bien liquide.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: sauceId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.889) with
        {
            ChunkIndex = 53,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = prepId
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous, prep, sauce, ingredients },
            limit: 4);

        Assert.DoesNotContain(continuations, match => match.ChunkId == previousId);
        Assert.Contains(continuations, match => match.ChunkId == prepId);
        Assert.Contains(continuations, match => match.ChunkId == sauceId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_previous_chunk_when_linked_context_starts_mid_procedure()
    {
        const string titleId = "455c86c5-d8ab-b160-bb5c-08f9ebaadf84";
        const string previousId = "7841da55-0997-aa1d-0c4d-f0a5e34e60ef";
        const string continuationId = "66bdcae6-3438-5489-9a8c-f0561c606a73";
        const string query = "Tu peux me faire une fiche claire pour \"Salade de harengs\" : ingredients, etapes, temps et source ?";
        var title = TestMatch(
            text: "Salade de harengs superposee Preparee 1 ou 2 jours a l'avance et gardee au frais, cette salade n'en sera que meilleure. 1.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: titleId,
            embeddingBasis: "exact_match_v1",
            score: 0.94) with
        {
            ChunkIndex = 3,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = previousId
        };
        var continuation = TestMatch(
            text: "Procedez de la meme facon avec les ingredients restants. 3. Recouvrez hermetiquement la salade avec un film alimentaire et mettez-la au frais pendant 5 heures minimum. INGREDIENTS 1 oignon doux, 250 g de creme aigre, 120 g de yaourt, 300 g de filets de harengs.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.88) with
        {
            ChunkIndex = 5,
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = previousId
        };
        var previous = TestMatch(
            text: "Recouvrez les oignons d'eau froide et laissez-les tremper 15 minutes. Egouttez-les puis melangez-les avec la creme aigre, le yaourt, le jus de citron et le sucre. 2. Mettez la moitie des filets de harengs dans un plat et recouvrez-les avec la moitie du melange a base de creme.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: previousId,
            embeddingBasis: "linked_context_v1",
            score: 0.86) with
        {
            ChunkIndex = 4,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            NextChunkId = continuationId
        };

        var selected = new List<RagMatch> { title, continuation };
        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(query, selected, limit: 4);

        Assert.Contains(anchors, match => match.ChunkId == continuationId);

        var continuations = RagEndpoints.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous },
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(previousId, linked.ChunkId);
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

        var anchors = RagEndpoints.SelectStructuredContinuationAnchors(
            query,
            [directTitle, linkedContinuation],
            limit: 1);

        var anchor = Assert.Single(anchors);
        Assert.Equal(continuationId, anchor.ChunkId);
    }

    [Fact]
    public void ReattachStructuredContinuationCompanions_keeps_same_page_chunks_next_to_anchor_after_final_sort()
    {
        var anchorId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Brochettes de poulet grille a l'indonesienne. Pour 6 personnes. 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched local title token route",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            SameSectionChunkId = ingredientsId
        };
        var unrelated = TestMatch(
            text: "INGREDIENTS 6 cuisses de poulet et tomates. PREPARATION faire cuire le poulet.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 58,
            chunkId: Guid.NewGuid().ToString(),
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918) with
        {
            ChunkIndex = 200,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var ingredients = TestMatch(
            text: "INGREDIENTS 1,5 kg de blancs de poulet. Pour la marinade 3 echalotes, 2 gousses d'ail, gingembre, sauce de soja, vinaigre et huile.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 54,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };
        var selected = new List<RagMatch> { anchor, unrelated, ingredients };

        RagEndpoints.ReattachStructuredContinuationCompanions(query, selected, topK: 3);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(ingredientsId, second.ChunkId),
            third => Assert.Equal(unrelated.ChunkId, third.ChunkId));
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

        RagEndpoints.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

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

        RagEndpoints.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(stepsId, second.ChunkId));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_recipe_card_over_title_only_ingredient_chunk()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Taboule\" : ingredients, etapes, temps et source ?";
        var titleOnlyIngredients = TestMatch(
            text: "TABOULE Pour 5 personnes 250 g de boulghour tomates menthe persil citron huile. Le taboule est un plat oriental.",
            docPath: "Cuisine/chefbot.pdf",
            page: 58,
            chunkId: "chefbot-taboule",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeFooterTitledCard = TestMatch(
            text: "Ingredients 200 g de semoule, tomates, concombre, oignon, persil, menthe, citrons, huile et sel. Materiel saladier, presse-citron et planche. Technique laver les tomates et le concombre, les couper en des, faire gonfler la semoule, ajouter les legumes, mettre au refrigerateur pendant 1 heure, ajouter huile et citron puis decorer avec la menthe. Notes de page et fragments OCR sans titre en tete. Encore du texte de liaison pour que le titre ne ressemble pas a un titre de debut. Cemea Taboule330011220055.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 31,
            chunkId: "si-on-taboule",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.756) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { titleOnlyIngredients, completeFooterTitledCard };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("si-on-taboule", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_keeps_local_title_route_above_generic_complete_card()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?";
        var genericCompleteCard = TestMatch(
            text: "INGREDIENTS PREPARATION 1. Placer la lame dans le robot. 2. Ajouter l'oignon et une pincee de poivre. 3. Verser la sauce chaude sur la preparation.",
            docPath: "Cuisine/chefbot.pdf",
            page: 54,
            chunkId: "chefbot-generic-sauce",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var localTitleRoute = TestMatch(
            text: "SAUCE AU POIVRE Temps total : 17 min. 1 c. a c. de poivre concasse, cognac, creme liquide, fond de veau et farine. Versez dans le robot, lancez la cuisson puis servez.",
            docPath: "Cuisine/moulinex.pdf",
            page: 121,
            chunkId: "moulinex-sauce-poivre",
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { genericCompleteCard, localTitleRoute };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("moulinex-sauce-poivre", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_exact_footer_title_window_over_token_only_direct_route()
    {
        var tokenOnlyDirectRoute = TestMatch(
            text: "Alpha with beta procedure and extra notes. Materials: wrench, seal, gauge. Procedure: isolate the equipment, replace the seal, test the line.",
            embedText: "Matched direct_title_token_route: alpha beta procedure\nAlpha with beta procedure and extra notes. Materials: wrench, seal, gauge.",
            docPath: "Docs/token-overlap.pdf",
            chunkId: "token-overlap",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var exactFooterWindow = TestMatch(
            text: "ALPHA BETA PROCEDURE 20 min Materials: valve gasket sealant. Steps: 1. Isolate the device. 2. Replace the gasket. 3. Verify the pressure.",
            embedText: "Matched direct_title_token_route: alpha beta procedure\nALPHA BETA PROCEDURE 20 min Materials: valve gasket sealant.",
            docPath: "Docs/exact-footer.pdf",
            chunkId: "exact-footer",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "footer_titled_item_window_v1",
            score: 0.666) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { tokenOnlyDirectRoute, exactFooterWindow };

        RagEndpoints.PrioritizeFinalSelections(
            "Give me a clear sheet for \"Alpha Beta Procedure\": materials, steps, time and source.",
            selected);

        Assert.Equal("exact-footer", selected[0].ChunkId);
        Assert.True(RagEndpoints.LooksLikeStructuredAnswerUnit(exactFooterWindow));
    }

    [Fact]
    public void PrioritizeFinalSelections_falls_back_to_quality_order_for_precise_cards_without_title_signal()
    {
        var weakerInsertedFirst = TestMatch(
            text: "Procedure entree. Ingredients: pois chiches, tahini. Preparation: mixer.",
            docPath: "Cuisine/weaker.pdf",
            chunkId: "weaker",
            score: 0.68) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.74
        };
        var strongerSpecificContent = TestMatch(
            text: "Ingredients: legumes cuits, petits pois, pommes de terre. Technique: cuire, couper, melanger, puis incorporer la sauce.",
            docPath: "Cuisine/stronger.pdf",
            chunkId: "stronger",
            score: 0.76) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { weakerInsertedFirst, strongerSpecificContent };

        RagEndpoints.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Alpha Beta \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("stronger", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_let_partial_title_overlap_override_score()
    {
        var partialLowerScore = TestMatch(
            text: "HOUMOUS DE BETTERAVE. Ingredients: betterave, pois chiches, tahini. Preparation: mixer.",
            docPath: "Cuisine/partial.pdf",
            chunkId: "partial",
            score: 0.68) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.90
        };
        var strongerPartialContent = TestMatch(
            text: "Ingredients: 700 g de betteraves rouges cuites, petits pois, pommes de terre. Technique: cuire, couper, melanger, puis incorporer la sauce.",
            docPath: "Cuisine/stronger.pdf",
            chunkId: "stronger",
            score: 0.76) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { partialLowerScore, strongerPartialContent };

        RagEndpoints.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Betteraves caucasiennes \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("stronger", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_preserves_operational_setting_intent()
    {
        var genericDessert = TestMatch(
            text: "Tarte au citron meringuee. Ingredients: farine, beurre, citron, sucre. Robot: vitesse 6 pendant 8 min.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "generic",
            score: 1.02);
        var robotSettings = TestMatch(
            text: "CREME AU CITRON. Dans le bol du robot muni du batteur, mettez les oeufs et le sucre. Lancez vitesse 6 a 50 C pendant 8 min.",
            docPath: "Cuisine/robot.pdf",
            chunkId: "robot",
            score: 0.87,
            chunkType: "exact_match_entry",
            embeddingBasis: "exact_match_v1");
        var selected = new List<RagMatch> { genericDessert, robotSettings };

        RagEndpoints.PrioritizeFinalSelections(
            "Je veux la creme au citron, avec les parametres robot.",
            selected);

        Assert.Equal("robot", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_short_duration_for_fast_title_request()
    {
        var longMachineRecipe = TestMatch(
            text: "FONDANT AU CHOCOLAT Temps total : 1 h. Robot mode dessert vitesse 6. Ingredients chocolat, beurre, oeufs. Preparation complete.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "long-machine",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var shortRecipe = TestMatch(
            text: "Fondant au chocolat Pour 4 personnes ingredients chocolat beurre oeufs. Preparation: prechauffez le four, melangez et enfournez pendant 7 min.",
            docPath: "Cuisine/classic.pdf",
            chunkId: "short-classic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.98);
        var selected = new List<RagMatch> { longMachineRecipe, shortRecipe };

        RagEndpoints.PrioritizeFinalSelections(
            "Donne-moi la recette du fondant au chocolat en mode rapide.",
            selected);

        Assert.Equal("short-classic", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_short_duration_for_generic_fast_procedure_request()
    {
        var longProcedure = TestMatch(
            text: "ALPHA BETA PROCEDURE Total time: 2 h. Materials: valve, gasket and sealant. Steps: isolate, replace, verify.",
            docPath: "Docs/long-guide.pdf",
            chunkId: "long-procedure",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var shortProcedure = TestMatch(
            text: "Alpha beta procedure. Materials: valve and gasket. Steps: isolate, replace and verify in 9 min.",
            docPath: "Docs/short-guide.pdf",
            chunkId: "short-procedure",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.98);
        var selected = new List<RagMatch> { longProcedure, shortProcedure };

        RagEndpoints.PrioritizeFinalSelections(
            "Give me the Alpha Beta Procedure quickly.",
            selected);

        Assert.Equal("short-procedure", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_focused_title_head_over_broad_qualifier_match()
    {
        var broadChocolate = TestMatch(
            text: "Mousse au chocolat express Pour 4 personnes ingredients chocolat et oeufs. Preparation avec chocolat fondu rapide.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "broad-chocolate",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.01);
        var focusedFondue = TestMatch(
            text: "SAUCE CHOCOLAT Cette sauce est excellente avec de la glace, des fruits au sirop, en fondue. Materiel et ingredients pour un groupe.",
            docPath: "Cuisine/children-guide.pdf",
            chunkId: "focused-fondue",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var selected = new List<RagMatch> { broadChocolate, focusedFondue };

        RagEndpoints.PrioritizeFinalSelections(
            "Je cherche la fondue au chocolat pour un groupe d'enfants.",
            selected);

        Assert.Equal("focused-fondue", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_structured_content_covering_specific_request_tokens()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz long 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var focusedCurry = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Servez le curry de crevettes avec du riz basmati. Ingredients et preparation completes.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "focused-curry",
            embeddingBasis: "linked_context_v1",
            score: 0.918);
        var selected = new List<RagMatch> { genericRice, focusedCurry };

        RagEndpoints.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("focused-curry", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_same_document_specific_coverage_for_linked_chunks()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz long 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var currySteps = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Lancez le programme mijote.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-steps",
            embeddingBasis: "linked_context_v1",
            score: 0.918);
        var curryServing = TestMatch(
            text: "Servez le curry de crevettes saupoudre de coriandre. Accompagnez ce plat de riz basmati.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-serving",
            embeddingBasis: "linked_context_v1",
            score: 0.807);
        var selected = new List<RagMatch> { genericRice, currySteps, curryServing };

        RagEndpoints.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("curry-steps", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_specific_document_coverage_over_generic_structured_body_match()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz basmati et 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var currySteps = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Lancez le programme mijote.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-steps",
            embeddingBasis: "linked_context_v1",
            chunkType: "linked_context_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var curryServing = TestMatch(
            text: "Servez le curry de crevettes saupoudre de coriandre. Accompagnez ce plat de riz basmati.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-serving",
            embeddingBasis: "linked_context_v1",
            chunkType: "linked_context_v1",
            score: 0.807,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericRice, currySteps, curryServing };

        RagEndpoints.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("curry-steps", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_generic_focused_head_over_body_variant()
    {
        var bodyVariant = TestMatch(
            text: "Access mode alpha explains credentials, permissions and fallback routes for the system.",
            docPath: "Docs/access-guide.pdf",
            chunkId: "body-variant",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.01);
        var focusedHead = TestMatch(
            text: "MODULE ALPHA Access mode, setup checklist and operating notes for the module.",
            docPath: "Docs/module-guide.pdf",
            chunkId: "focused-head",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var selected = new List<RagMatch> { bodyVariant, focusedHead };

        RagEndpoints.PrioritizeFinalSelections(
            "Show Module Alpha access mode.",
            selected);

        Assert.Equal("focused-head", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_prefers_machine_settings_when_requested()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Put the ingredients in the machine bowl. Use speed 6 at 50 C for 8 minutes.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RagEndpoints.PrioritizeOperationalSettingSelections("Give me alpha with machine settings.", selected);

        Assert.Equal("settings", selected[0].ChunkId);
        Assert.Equal("plain", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_does_not_change_plain_lookup()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Use speed 6 at 50 C for 8 minutes.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RagEndpoints.PrioritizeOperationalSettingSelections("Give me alpha.", selected);

        Assert.Equal("plain", selected[0].ChunkId);
        Assert.Equal("settings", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_ignores_fast_mode_without_device_context()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation. Bake for 7 min.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Machine mode dessert with speed 6 at 50 C for 1 h.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RagEndpoints.PrioritizeOperationalSettingSelections("Give me alpha in quick mode.", selected);

        Assert.Equal("plain", selected[0].ChunkId);
        Assert.Equal("settings", selected[1].ChunkId);
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
    public void CalibrateFusedMatches_prefers_linked_structured_anchor_title_over_exact_advisory_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Croquettes de poulet\" : ingredients, etapes, temps et source ?";
        var advisoryMention = TestMatch(
            text: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            embedText: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "advisory",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentDensityScore = 0.20
        };
        var linkedStructured = TestMatch(
            text: "INGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            embedText: "Matched linked_anchor_title: Croquettes de poulet\nINGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            docPath: "Cuisine/structured.pdf",
            chunkId: "structured",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.74) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.92
        };
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, advisoryMention));
        Assert.True(RagEndpoints.ComputeQuotedTitleAnchorSignal(query, linkedStructured) > 0.0);

        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [advisoryMention, linkedStructured]);

        Assert.Equal("structured", calibrated[0].ChunkId);
        Assert.True(RagEndpoints.HasProfileTitleHint(linkedStructured));
        Assert.True(RagEndpoints.ComputeExactTitleCandidateScore(query, linkedStructured) > 0.0);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_linked_structured_anchor_title_over_exact_advisory_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Croquettes de poulet\" : ingredients, etapes, temps et source ?";
        var advisoryMention = TestMatch(
            text: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            embedText: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "advisory",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentDensityScore = 0.20
        };
        var titleMetadata = TestMatch(
            text: "Volailles Poule Modes de preparation Frire Categories de recettes Volailles Pour 4 portions, env. 20 pieces",
            embedText: "Matched title_anchor_route: Croquettes de poulet\nVolailles Poule Modes de preparation Frire Categories de recettes Volailles Pour 4 portions, env. 20 pieces",
            docPath: "Cuisine/structured.pdf",
            chunkId: "metadata",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var linkedStructured = TestMatch(
            text: "INGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            embedText: "Matched linked_anchor_title: Croquettes de poulet\nINGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            docPath: "Cuisine/structured.pdf",
            chunkId: "structured",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.74) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.92
        };
        var selected = new List<RagMatch> { advisoryMention, titleMetadata, linkedStructured };
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, advisoryMention));
        Assert.True(RagEndpoints.ComputeQuotedTitleAnchorSignal(query, linkedStructured) > 0.0);

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("structured", selected[0].ChunkId);
        Assert.True(Array.FindIndex(selected.ToArray(), match => match.ChunkId == "structured")
            < Array.FindIndex(selected.ToArray(), match => match.ChunkId == "advisory"));
        Assert.True(Array.FindIndex(selected.ToArray(), match => match.ChunkId == "structured")
            < Array.FindIndex(selected.ToArray(), match => match.ChunkId == "metadata"));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_structured_linked_recipe_over_mixed_navigation_phrase_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Boulettes de poulet a la sauce tomate\" : ingredients, etapes, temps et source ?";
        var mixedNavigationMention = TestMatch(
            text: "MES INDISPENSABLES Qu'elles soient entreposees au refrigerateur, au congelateur ou dans les armoires, il est primordial d'avoir des denrees de base: poulet, sauce tomate, chapelure et epices pour preparer rapidement des boulettes.",
            embedText: "Matched quoted title: boulettes de poulet a la sauce tomate\nMES INDISPENSABLES Qu'elles soient entreposees au refrigerateur, au congelateur ou dans les armoires, il est primordial d'avoir des denrees de base: poulet, sauce tomate, chapelure et epices pour preparer rapidement des boulettes.",
            docPath: "Cuisine/pantry.pdf",
            chunkId: "mixed-navigation",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 0.50) with
        {
            NavigationScore = 0.64
        };
        var linkedStructuredRecipe = TestMatch(
            text: "50 min PREPARATION Placer l'accessoire lame dans le recipient. Ajouter l'oignon et l'ail puis programmer 10 secondes. Ajouter la viande de poulet, former les boulettes, les cuire avec la sauce tomate et servir chaud.",
            embedText: "103 BE A MASTER. BECOME A CHEF 50 min PREPARATION Placer l'accessoire lame dans le recipient. Ajouter l'oignon et l'ail puis programmer 10 secondes. Ajouter la viande de poulet, former les boulettes, les cuire avec la sauce tomate et servir chaud.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            page: 103,
            chunkId: "linked-structured",
            embeddingBasis: "linked_context_v1",
            score: 0.84,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var selected = new List<RagMatch> { mixedNavigationMention, linkedStructuredRecipe };
        var phrases = RagEndpoints.ExtractTitleLookupPhrases(query);

        Assert.NotEmpty(phrases);
        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, mixedNavigationMention.ContentRole);
        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(phrases, mixedNavigationMention));
        Assert.True(RagEndpoints.IsWeakStructuredQuotedNavigationMentionForTesting(query, mixedNavigationMention));

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("linked-structured", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_title_route_over_partial_homonymous_recipe()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Terrines de legumes\" : ingredients, etapes, temps et source ?";
        var partialHomonym = TestMatch(
            text: "Menu TERRINEDE LEGUMES20 min 4 Ingredients Quelques feuilles de salade 250 g de restes de legumes Huile d'olive 2 echalotes 50 g emmental rape 2 oeufs 60 g de creme 60 g de pain rassis 4 cuilleres a soupe de lait Sel, poivre Noix de muscade Sauce tartare La terrine peut etre degustee tiede ou froide.",
            embedText: "Menu TERRINEDE LEGUMES20 min 4 Ingredients Quelques feuilles de salade 250 g de restes de legumes Huile d'olive 2 echalotes 50 g emmental rape 2 oeufs 60 g de creme 60 g de pain rassis 4 cuilleres a soupe de lait Sel, poivre Noix de muscade Sauce tartare La terrine peut etre degustee tiede ou froide.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "partial-homonym",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.98) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeTitleRoute = TestMatch(
            text: "TERRINE DE LEGUMES Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot. Deposez les legumes dans le panier vapeur et lancez le programme vapeur P1 pour 15 min. Ajoutez les carottes et les petits pois. Prechauffez le four a 180 C. Enfournez-la pour 1 h. 8 personnes 18 min 1 h 20 min TERRINE DE LEGUMES",
            embedText: "Matched title_anchor_route: Terrine de legumes\nTERRINE DE LEGUMES Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot. Deposez les legumes dans le panier vapeur et lancez le programme vapeur P1 pour 15 min. Ajoutez les carottes et les petits pois. Prechauffez le four a 180 C. Enfournez-la pour 1 h. 8 personnes 18 min 1 h 20 min TERRINE DE LEGUMES",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "complete-title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { partialHomonym, completeTitleRoute };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("complete-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_local_quoted_title_content_before_title_catalog()
    {
        var titleCatalog = TestMatch(
            text: "Contents Alpha Beta Procedure 12 Alpha Gamma Procedure 18 Alpha Delta Procedure 24",
            embedText: "Matched direct_title_token_route: Alpha Beta Procedure\nContents Alpha Beta Procedure 12 Alpha Gamma Procedure 18 Alpha Delta Procedure 24",
            chunkId: "catalog",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.76,
            ContentDensityScore = 0.50
        };
        var content = TestMatch(
            text: "Alpha Beta Procedure. Materials: valve gasket sealant. Procedure: isolate, replace and verify the assembly.",
            embedText: "Alpha Beta Procedure. Materials: valve gasket sealant. Procedure: isolate, replace and verify the assembly.",
            chunkId: "content",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.72
        };
        var partial = TestMatch(
            text: "Alpha and beta are separate entries in this glossary section.",
            embedText: "Alpha and beta are separate entries in this glossary section.",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [titleCatalog, partial, content],
            prioritizeDocumentProfiles: false,
            query: "Give me a clear sheet for \"Alpha Beta Procedure\".");

        Assert.Equal("content", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_title_lead_over_exact_body_mention()
    {
        var bodyMention = TestMatch(
            text: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            embedText: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            docPath: "Docs/body-mention.pdf",
            chunkId: "body-mention",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.20
        };
        var titleLead = TestMatch(
            text: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            embedText: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            docPath: "Docs/title-lead.pdf",
            chunkId: "title-lead",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { bodyMention, titleLead };

        RagEndpoints.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Alpha Beta Procedure\".", selected);

        Assert.Equal("title-lead", selected[0].ChunkId);
        Assert.False(RagEndpoints.HasQuotedTitlePlacementEvidence(["Alpha Beta Procedure"], bodyMention));
        Assert.True(RagEndpoints.HasQuotedTitlePlacementEvidence(["Alpha Beta Procedure"], titleLead));
    }

    [Fact]
    public void CalibrateFusedMatches_does_not_treat_exact_body_phrase_as_title_evidence()
    {
        var bodyMention = TestMatch(
            text: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            embedText: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            docPath: "Docs/body-mention.pdf",
            chunkId: "body-mention",
            embeddingBasis: "exact_match_v1",
            score: 0.97) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.20
        };
        var titleLead = TestMatch(
            text: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            embedText: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            docPath: "Docs/title-lead.pdf",
            chunkId: "title-lead",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };

        var calibrated = RagEndpoints.CalibrateFusedMatches(
            "Give me a clear sheet for \"Alpha Beta Procedure\".",
            [bodyMention, titleLead]);

        Assert.Equal("title-lead", calibrated[0].ChunkId);
        Assert.Equal(0.0, RagEndpoints.ComputeExactTitleCandidateScore(
            "Give me a clear sheet for \"Alpha Beta Procedure\".",
            bodyMention));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_placement_over_dense_body_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Aioli epice\" : ingredients, etapes, temps et source ?";
        var denseBodyMention = TestMatch(
            text: "Conseils sauces: aoli classique, mayonnaise, ail, citron, piment doux et variante epicee pour accompagner les legumes. Ingredients generiques et etapes rapides pour servir une sauce froide.",
            embedText: "Conseils sauces: aoli classique, mayonnaise, ail, citron, piment doux et variante epicee pour accompagner les legumes. Ingredients generiques et etapes rapides pour servir une sauce froide.",
            docPath: "Cuisine/generic-sauces.pdf",
            chunkId: "dense-body",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.04,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.96);
        var cardTitleHit = TestMatch(
            text: "SAUCES AIOLI EPICE\nIngredients: ail, jaune d'oeuf, huile, citron, piment. Preparation: monter la sauce, assaisonner et servir avec les legumes. Temps: 10 min.",
            embedText: "Matched content_card: SAUCES AIOLI EPICE\nIngredients: ail, jaune d'oeuf, huile, citron, piment. Preparation: monter la sauce, assaisonner et servir avec les legumes. Temps: 10 min.",
            docPath: "Cuisine/jecuisine.pdf",
            chunkId: "card-title-hit",
            embeddingBasis: "linked_context_v1",
            score: 0.74,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.91) with
        {
            MatchedContentCards = [new RagMatchedContentCard("SAUCES AIOLI EPICE", Signals: ["structured_facts"])]
        };
        var selected = new List<RagMatch> { denseBodyMention, cardTitleHit };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-hit", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_full_compact_card_title_over_partial_structured_title()
    {
        const string query = "Tu peux me faire une fiche claire pour « Aïoli épicé » : ingrédients, étapes, temps et source ?";
        var partialStructuredTitle = TestMatch(
            text: "Pour 4 personnes Cette mayonnaise a l'ail se marie tres bien avec les legumes nouveaux. Aioli 1. Mettez le vinaigre, l'oeuf, les jaunes d'oeuf, la moutarde et le sucre dans un robot. Assaisonnez puis versez lentement l'huile. Ingredients: vinaigre, oeuf, moutarde, huile, ail, jus de citron.",
            embedText: "Pour 4 personnes Cette mayonnaise a l'ail se marie tres bien avec les legumes nouveaux. Aioli 1. Mettez le vinaigre, l'oeuf, les jaunes d'oeuf, la moutarde et le sucre dans un robot. Assaisonnez puis versez lentement l'huile. Ingredients: vinaigre, oeuf, moutarde, huile, ail, jus de citron.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 79,
            chunkId: "partial-aioli",
            embeddingBasis: "dense_qdrant",
            score: 0.374109,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.70);
        var compactFullTitle = TestMatch(
            text: "Ajouter tous les ingredients dans un cul-de-poule a l'exception de l'huile. En fouettant vigoureusement, ajouter l'huile en filet jusqu'a ce que la mayonnaise soit lisse et homogene. Ingredients: 5 gousses d'ail roties, citron, jaune d'oeuf, moutarde, huile, sauce piquante, sel et poivre. SAUCESAIOLI EPICE",
            embedText: "Matched profile title: SAUCESAIOLI EPICE; Sel et poivreSAUCESAIOLI EPICE\nAjouter tous les ingredients dans un cul-de-poule a l'exception de l'huile. En fouettant vigoureusement, ajouter l'huile en filet jusqu'a ce que la mayonnaise soit lisse et homogene. Ingredients: 5 gousses d'ail roties, citron, jaune d'oeuf, moutarde, huile, sauce piquante, sel et poivre. SAUCESAIOLI EPICE",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 21,
            chunkId: "compact-full-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("SAUCESAIOLI EPICE", Signals: ["structured_facts"]),
                new RagMatchedContentCard("Sel et poivreSAUCESAIOLI EPICE", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { partialStructuredTitle, compactFullTitle };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("compact-full-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_direct_token_route_over_partial_sparse_body_match()
    {
        const string query = "Tu peux me faire une fiche claire pour « Endives au jambon » : materials, étapes, temps et source ?";
        var partialSparse = TestMatch(
            text: "Preparation 1. Cuire les pates puis les egoutter. Couper l'endive en fines lamelles, ajouter les tomates et melanger. Servir avec une vinaigrette.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            page: 16,
            chunkId: "partial-sparse",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.62,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 1.0);
        var directRoute = TestMatch(
            text: "Pour la sauce bechamel\n\nPour 4 personnes Pour ce plat traditionnel, les endives baignent avec le jambon dans une sauce cremeuse et relevee. Gratin d'endives 1. Prechauffez le four a 180 C. Mettez les endives dans un moule. 2. Faites chauffer le lait avec l'oignon. 3. Enveloppez chaque tete d'endive d'une tranche de jambon. Nappez avec la sauce bechamel. 4. Faites gratiner 20-25 minutes. Ingredients: 8 tetes d'endives, 8 tranches de jambon cuit, gouda, lait, beurre, farine.",
            embedText: "Matched direct_title_token_route: endives au jambon\nPour la sauce bechamel\n\nPour 4 personnes Pour ce plat traditionnel, les endives baignent avec le jambon dans une sauce cremeuse et relevee. Gratin d'endives 1. Prechauffez le four a 180 C. Mettez les endives dans un moule. 2. Faites chauffer le lait avec l'oignon. 3. Enveloppez chaque tete d'endive d'une tranche de jambon. Nappez avec la sauce bechamel. 4. Faites gratiner 20-25 minutes. Ingredients: 8 tetes d'endives, 8 tranches de jambon cuit, gouda, lait, beurre, farine.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 37,
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.774,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 0.595);
        var selected = new List<RagMatch> { partialSparse, directRoute };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("direct-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_route_over_ingredient_phrase_mentions()
    {
        const string query = "Tu peux me faire une fiche claire pour « Bouillon de volaille » : ingrédients, étapes, temps et source ?";
        var ingredientMention = TestMatch(
            text: "RISOTTO PETITS POIS ET JAMBON Temps total: 23 min. 300 g de riz arborio, echalote, vin blanc, 90 cl de bouillon de volaille, petits pois, parmesan. Preparation: verser le bouillon de volaille puis cuire le risotto.",
            docPath: "Cuisine/robot-rice.pdf",
            page: 96,
            chunkId: "ingredient-mention",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72) with
        {
            MatchedContentCards = [new RagMatchedContentCard("RISOTTO PETITS POIS ET JAMBON", Signals: ["structured_facts"])]
        };
        var cardTitleRoute = TestMatch(
            text: "TRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. SAUCE AUX TOMATES SAUCES PREPARATION Dans une grande casserole, faire revenir les parties de la volaille, l'oignon et l'ail dans l'huile. Ajouter les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            embedText: "Matched direct_title_token_route: bouillon de volaille\nTRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. SAUCE AUX TOMATES SAUCES PREPARATION Dans une grande casserole, faire revenir les parties de la volaille, l'oignon et l'ail dans l'huile. Ajouter les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 18,
            chunkId: "card-title-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.895) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Grains de poivreBOUILLON DE VOLAILLE SAUCES"),
                new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { ingredientMention, cardTitleRoute };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_route_over_procedure_lead_ingredient_phrase()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Bouillon de volaille\" : ingredients, etapes, temps et source ?";
        var procedureLeadIngredientMention = TestMatch(
            text: "Lorsqu'il ne reste que 1 min, ajoutez le vin blanc. A la fin du programme, versez le bouillon de volaille et lancez le programme mijote pour 20 min. Rectifiez l'assaisonnement. Servez sans attendre. RISOTTO D'ETE Astuce: ne pas laisser au chaud.",
            embedText: "Matched quoted title: Lorsque le minuteur indique qu'il reste; Lorsqu'il ne reste que; RISOTTO CLASSIQUE",
            docPath: "Cuisine/robot-rice.pdf",
            page: 95,
            chunkId: "procedure-lead-ingredient",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Lorsque le minuteur indique qu'il reste", Signals: ["structured_facts"]),
                new RagMatchedContentCard("Lorsqu'il ne reste que", Signals: ["structured_facts"]),
                new RagMatchedContentCard("RISOTTO CLASSIQUE", Signals: ["structured_facts"])
            ]
        };
        var cardTitleRoute = TestMatch(
            text: "TRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. Ajouter les parties de volaille, les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            embedText: "Matched direct_title_token_route: bouillon de volaille\nTRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. Ajouter les parties de volaille, les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 18,
            chunkId: "card-title-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.895) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Grains de poivreBOUILLON DE VOLAILLE SAUCES"),
                new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { procedureLeadIngredientMention, cardTitleRoute };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_trailing_quoted_title_over_partial_head_title()
    {
        const string query = "Donne-moi le one pot pasta brocoli dinde bacon.";
        Assert.Equal("one pot pasta brocoli dinde bacon", RagEndpoints.BuildFocusedLexicalBackfillQuery(query));

        var partialHeadTitle = TestMatch(
            text: "ONE POT PASTA AUX CREVETTES ET MASCARPONE Ajouter un peu de parmesan au moment de servir. Ingredients: pates, crevettes, mascarpone. Preparation: cuire et melanger.",
            embedText: "Matched profile title: ONE POT PASTA AUX CREVETTES ET MASCARPONE\nONE POT PASTA AUX CREVETTES ET MASCARPONE Ajouter un peu de parmesan au moment de servir. Ingredients: pates, crevettes, mascarpone. Preparation: cuire et melanger.",
            docPath: "Cuisine/partial.pdf",
            page: 94,
            chunkId: "partial-head-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.549,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.94) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("ONE POT PASTA AUX CREVETTES ET MASCARPONE", Signals: ["structured_facts"])
            ]
        };
        var trailingTitle = TestMatch(
            text: "TRUCS CULINAIRES Ajouter le reste des ingredients, a l'exception de la dinde et des bouquets de brocoli. Porter a ebullition, reduire la puissance et laisser mijoter pour 10 minutes. En fin de cuisson, ajouter la dinde afin de la rechauffer. Couper le bacon en laniere et preparer le service. Ingredients 3 tranches de bacon, hachees 1 brocoli, bouquet et pied separes 5 gousses d'ail, hachees finement 1.5 L d'eau, spaghettini, sel, poivre frais, sauce piquante au choix, 2 tasses de dinde, cuite\u00ab ONE POT PASTA \u00bb BROCOLI, DINDE ET BACONPLATS PRINCI- PAUX",
            docPath: "Cuisine/target.pdf",
            page: 44,
            chunkId: "trailing-quoted-title",
            embeddingBasis: "linked_context_v1",
            score: 0.918,
            prevChunkId: "target-prev",
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.6485) with
        {
            NavigationScore = 0.69,
            MatchedContentCards =
            [
                new RagMatchedContentCard("HUITRES TOMATEESPLATS PRINCI"),
                new RagMatchedContentCard("DINDE ET BACONPLATS PRINCI- PAUX", Signals: ["structured_facts"]),
                new RagMatchedContentCard("TOMATEESPLATS PRINCI")
            ]
        };
        var selected = new List<RagMatch> { partialHeadTitle, trailingTitle };

        RagEndpoints.PrioritizeFinalSelections(query, selected);
        Assert.Equal("trailing-quoted-title", selected[0].ChunkId);
        RagEndpoints.ReattachStructuredContinuationCompanions(query, selected, topK: 20);
        RagEndpoints.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);
        RagEndpoints.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

        Assert.Equal("trailing-quoted-title", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_deprioritizes_route_when_title_only_appears_as_trailing_lead()
    {
        var trailingRoute = TestMatch(
            text: "Materials: gasket and wrench. Procedure: inspect the previous assembly and close the checklist. Index entry Alpha Beta Procedure",
            embedText: "Matched title_anchor_route: Alpha Beta Procedure\nMaterials: gasket and wrench. Procedure: inspect the previous assembly and close the checklist. Index entry Alpha Beta Procedure",
            chunkId: "trailing-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.95
        };
        var denseNeighbor = TestMatch(
            text: "Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            chunkId: "dense-neighbor",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.84) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.90
        };

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [trailingRoute, denseNeighbor],
            prioritizeDocumentProfiles: false,
            query: "Give me Alpha Beta Procedure.");

        Assert.True(RagEndpoints.IsWeakResolvedRouteTarget(trailingRoute));
        Assert.Equal("dense-neighbor", ordered[0].ChunkId);
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

        RagEndpoints.PruneUnpagedProfileSelectionsForPreciseLookup(
            "Compare three procedures for apprentices: material, risks and setup notes.",
            selected);

        Assert.Equal(2, selected.Count);
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

    [Fact]
    public void PrioritizeFinalSelections_prefers_unquoted_complete_title_over_generic_body_match()
    {
        const string query = "Comment je fais le magret de canard au miel ?";
        var genericBodyMatch = TestMatch(
            text: "Les alternatives a la viande. Les legumineuses constituent une solution de remplacement interessante a la viande. Pour cuisiner les legumineuses seches, consultez le guide.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "generic-body",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.36) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeTitle = TestMatch(
            text: "Magret de canard au miel Pour 4 personnes. Ingredients : 2 magrets de canard, 3 c. a soupe de miel, sauce soja, vinaigre de riz. Preparation : laisser mariner puis cuire au four 30 min.",
            embedText: "Matched title_anchor_route: Magret de canard au miel\nMagret de canard au miel Pour 4 personnes. Ingredients : 2 magrets de canard, 3 c. a soupe de miel, sauce soja, vinaigre de riz. Preparation : laisser mariner puis cuire au four 30 min.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "complete-title",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { genericBodyMatch, completeTitle };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal(["magret"], RagEndpoints.ExtractFocusedLookupPrimaryHeadTokens(query));
        Assert.Equal("complete-title", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_content_covering_two_distinctive_terms_over_single_generic_mention()
    {
        const string query = "Comment controler le module vert avec la sonde de rotissage ?";
        var partialHighScore = TestMatch(
            text: "Module vert. Le module vert doit etre nettoye puis inspecte avant lancement.",
            docPath: "Knowledge/module-generic.pdf",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var directTarget = TestMatch(
            text: "Module vert. Procedure : brancher la sonde de rotissage, controler le module vert, puis enregistrer la mesure de stabilisation.",
            docPath: "Knowledge/module-procedure.pdf",
            chunkId: "target",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.64,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [partialHighScore, directTarget],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("target", ordered[0].ChunkId);
    }

    [Fact]
    public void BuildDirectSpecificContentCoverageTokens_keeps_distinctive_terms_without_generic_facets()
    {
        var tokens = RagEndpoints.BuildDirectSpecificContentCoverageTokens(
            "Je veux les reglages de rotissage pour le module vert avec la sonde principale.");

        Assert.Contains("module", tokens);
        Assert.Contains("sonde", tokens);
        Assert.DoesNotContain("veux", tokens);
        Assert.DoesNotContain("pour", tokens);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_specific_setting_target_over_generic_context_match()
    {
        const string query = "Quels reglages de rotissage pour le materiau composite haute densite ?";
        var genericSettingMatch = TestMatch(
            text: "Guide de calibration thermique. Les reglages de rotissage et de chauffe doivent etre controles avant usage. Le document mentionne plusieurs materiaux sans procedure specifique.",
            docPath: "Knowledge/generic-settings.pdf",
            chunkId: "generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var specificTarget = TestMatch(
            text: "Materiau composite haute densite. Procedure : verifier le support, appliquer la temperature de reference, puis ajuster la sonde de rotissage selon la densite mesuree.",
            docPath: "Knowledge/specific-settings.pdf",
            chunkId: "specific",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.63,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericSettingMatch, specificTarget };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("specific", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_focused_item_covering_two_distinctive_terms()
    {
        const string query = "Comment controler le module vert au miel avec la sonde de rotissage ?";
        var firstPartial = TestMatch(
            text: "Module vert. Le module vert doit etre nettoye puis inspecte avant lancement.",
            docPath: "Knowledge/module-generic.pdf",
            chunkId: "module-generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var secondPartial = TestMatch(
            text: "Controle au miel. Le miel est cite comme exemple de matiere collante dans la procedure de nettoyage.",
            docPath: "Knowledge/honey-generic.pdf",
            chunkId: "honey-generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var focusedTarget = TestMatch(
            text: "Module vert au miel. Procedure : placer le module vert sur le banc, regler la sonde de rotissage, puis controler le depot de miel apres stabilisation.",
            docPath: "Knowledge/focused.pdf",
            chunkId: "focused",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.64,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { firstPartial, secondPartial, focusedTarget };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("focused", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_promote_low_evidence_two_anchor_noise()
    {
        const string query = "Comment controler le module vert avec la sonde de rotissage ?";
        var supportedContent = TestMatch(
            text: "Module vert. Procedure detaillee : verifier le module vert, preparer le banc de test, puis executer le controle.",
            docPath: "Knowledge/supported.pdf",
            chunkId: "supported",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.95);
        var noisyNavigation = TestMatch(
            text: "Index module vert sonde rotissage autres chapitres annexes contacts revision sommaire.",
            docPath: "Knowledge/noisy.pdf",
            chunkId: "noise",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.04,
            contentRole: RetrievalContentClassifier.NavigationRole,
            contentDensityScore: 0.05);
        var selected = new List<RagMatch> { noisyNavigation, supportedContent };

        RagEndpoints.PrioritizeFinalSelections(query, selected);

        Assert.Equal("supported", selected[0].ChunkId);
    }

    [Theory]
    [InlineData("Prepare a documented onboarding plan for 4 people from the corpus.", "pour4")]
    [InlineData("Compare documented options for 4 people and pick the best candidate.", "pour 4")]
    [InlineData("Build a weekly plan for 6 people from the corpus.", "for 6 people")]
    public void Weak_count_exact_terms_are_pruned_for_broad_synthesis_queries(string query, string term)
    {
        Assert.True(RagEndpoints.ShouldPruneWeakExactLookupTermsForQuery(query));
        Assert.True(RagEndpoints.IsWeakCountQuantityExactLookupTerm(term));
    }

    [Fact]
    public void BuildExactMatchLookupTerms_prunes_weak_count_terms_but_keeps_specific_references()
    {
        var terms = RagEndpoints.BuildExactMatchLookupTerms(
            "Compare documented options XR-42 for 4 people and pick the best candidate.");

        Assert.DoesNotContain(terms, RagEndpoints.IsWeakCountQuantityExactLookupTerm);
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
        Assert.Equal(expected, RagEndpoints.ShouldUseContainsExactMatchLookupTerm(term));
    }

    [Fact]
    public void BuildContainsExactMatchLookupTerms_filters_short_exact_terms()
    {
        var terms = RagEndpoints.BuildContainsExactMatchLookupTerms(
            ["en 15281", "mode a jd", "safety valve inspection"]);

        Assert.DoesNotContain("en 15281", terms);
        Assert.Contains("mode a jd", terms);
        Assert.Contains("safety valve inspection", terms);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_short_count_exact_match_for_broad_planning_query()
    {
        const string query = "Compare documented options for 4 people and pick the best candidate.";
        var shortCountExact = TestMatch(
            "Pour4",
            chunkId: "weak-count",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.99);
        var planningContext = TestMatch(
            "Document profile: comparison planning, budget constraints, documented options and selection criteria.",
            chunkId: "planning-context",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.78);

        var calibrated = RagEndpoints.CalibrateFusedMatches(query, [shortCountExact, planningContext]);

        Assert.True(RagEndpoints.ShouldPenalizeWeakExactMatchForBroadQuery(query, shortCountExact));
        Assert.Equal("planning-context", calibrated[0].ChunkId);
        Assert.Equal("weak-count", calibrated[1].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_places_weak_count_exact_after_profiles_for_broad_query()
    {
        const string query = "Prepare a documented onboarding plan for 4 people from the corpus.";
        var shortCountExact = TestMatch(
            "Pour4",
            chunkId: "weak-count",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.99);
        var profile = TestMatch(
            "Document profile: onboarding plan, audience constraints, documented tasks and preparation steps.",
            chunkId: "profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.72);

        var ordered = RagEndpoints.OrderMatchesForSelection(
            [shortCountExact, profile],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("profile", ordered[0].ChunkId);
        Assert.Equal("weak-count", ordered[1].ChunkId);
    }

    [Fact]
    public void ShouldProbeUnquotedTitleAnchorRoute_detects_instructional_recipe_titles()
    {
        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Explique-moi les churros sauce chocolat au Companion.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Donne-moi les nouilles sautees legumes-crevettes du PDF sante travail.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));

        Assert.True(RagEndpoints.ShouldProbeUnquotedTitleAnchorRoute(
            "Donne-moi les nouilles sautées légumes-crevettes du PDF santé travail.",
            skipChunkRetrieversForDocumentOverview: false,
            useScopedProfileFallback: false));
    }

    private static RagMatch TestMatch(
        string text,
        string? embedText = null,
        string docPath = "Cuisine/Test.pdf",
        int page = 1,
        string chunkId = "chunk-1",
        string embeddingBasis = "sparse_bm25_v1",
        string chunkType = "unit_exact_v1",
        double score = 0.93,
        string? prevChunkId = null,
        string? contentRole = null,
        double? contentDensityScore = null)
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
            PrevChunkId: prevChunkId,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: contentRole,
            ContentDensityScore: contentDensityScore);

}
