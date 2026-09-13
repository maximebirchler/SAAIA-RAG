using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedTerminalLocalizationTests
{
    [Theory]
    [InlineData("fr", "Les sources consultees", "J'ai besoin", "Je n'ai pas pu", "La recherche documentaire", "Le pipeline source-backed")]
    [InlineData("en", "The consulted sources", "I need", "I could not", "The document search", "The source-backed pipeline")]
    [InlineData("es", "Las fuentes consultadas", "Necesito", "No pude", "La búsqueda documental", "No se pudo")]
    [InlineData("pt", "As fontes consultadas", "Preciso", "Não consegui", "A pesquisa documental", "Não foi possível")]
    [InlineData("de", "Die konsultierten Quellen", "Ich benötige", "Ich konnte", "Die Dokumentensuche", "Die Quellenprüfung")]
    [InlineData("it", "Le fonti consultate", "Ho bisogno", "Non sono riuscito", "La ricerca documentale", "Non è stato possibile")]
    public void Safe_terminals_keep_the_response_language_across_every_failure_kind(
        string language, string insufficient, string clarification, string verification, string timeout, string failure)
    {
        var result = EmptyResult(language);
        Assert.StartsWith(insufficient, SourceBackedTerminalAnswer.Build(result), StringComparison.Ordinal);
        Assert.StartsWith(clarification, SourceBackedTerminalAnswer.Build(result with
        {
            JudgeDecision = result.JudgeDecision with { Decision = "clarify" }
        }), StringComparison.Ordinal);
        Assert.StartsWith(verification, SourceBackedTerminalAnswer.Build(result with
        {
            Verification = new SourceVerificationResult(false, Array.Empty<SourceVerificationError>(), Array.Empty<EvidenceItem>())
        }), StringComparison.Ordinal);
        var timedOut = result.EvidenceBundle with
        {
            RetrievalAttempts = new[]
            {
                new SourceBackedRetrievalAttempt(1, "rag.search", new[] { "reference" }, null,
                    "timeout", "rag_search_query_timeout", false, Array.Empty<string>(), 1000, 0)
            }
        };
        Assert.StartsWith(timeout, SourceBackedTerminalAnswer.Build(result with { EvidenceBundle = timedOut }), StringComparison.Ordinal);
        Assert.StartsWith(failure, SourceBackedTerminalAnswer.BuildFailure(language), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("EN-gb", "en")]
    [InlineData("es-MX", "es")]
    [InlineData("pt-BR", "pt")]
    [InlineData("de-DE", "de")]
    [InlineData("it-IT", "it")]
    public void Regional_language_tags_do_not_fall_back_to_english(string regional, string canonical)
    {
        Assert.Equal(SourceBackedTerminalAnswer.Build(EmptyResult(canonical)), SourceBackedTerminalAnswer.Build(EmptyResult(regional)));
        Assert.Equal(SourceBackedTerminalAnswer.BuildFailure(canonical), SourceBackedTerminalAnswer.BuildFailure(regional));
    }

    [Theory]
    [InlineData("fr", "Je n'ai pas trouvé")]
    [InlineData("en", "I did not find")]
    [InlineData("es", "No encontré")]
    [InlineData("pt", "Não encontrei")]
    [InlineData("de", "Ich habe")]
    [InlineData("it", "Non ho trovato")]
    public void Proven_missing_document_keeps_its_identity_in_every_language(string language, string prefix)
    {
        const string document = "Reference-2026.pdf";
        var result = EmptyResult(language);
        result = result with
        {
            Intake = result.Intake with
            {
                NamedReferenceKind = "document",
                RequestedDocumentName = document,
                RequestedDocumentResolution = new SourceBackedDocumentResolutionObservation(
                    document, SourceBackedDocumentResolutionStatus.NotFound, true,
                    Array.Empty<SourceBackedDocumentResolutionCandidate>(), "not_found", 1, 0, false, 1)
            }
        };
        var answer = SourceBackedTerminalAnswer.Build(result);
        Assert.StartsWith(prefix, answer, StringComparison.Ordinal);
        Assert.Contains(document, answer, StringComparison.Ordinal);
    }

    private static SourceBackedPipelineResult EmptyResult(string language)
    {
        var intake = new SourceBackedIntake("A direct documentary question", "rag.answer",
            Array.Empty<string>(), Array.Empty<string>(), false, language);
        return new SourceBackedPipelineResult("localization-test", intake, null,
            EvidenceBundle.Empty(intake.UserQuestion),
            new EvidenceJudgeDecision("insufficient_evidence", Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(), Array.Empty<string>()),
            null, null, null, false, Array.Empty<SourceBackedTraceEvent>());
    }
}
