using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedConversationMemoryTests
{
    [Fact]
    public void Factory_records_exact_actions_used_items_and_llm_rejections()
    {
        var intake = Intake("Construis un premier plan.");
        var request = new RetrievalRequest(
            "rag.multi_search",
            "option concrete",
            "Knowledge",
            "find distinct options");
        var used = Evidence("E1", "content-card:option-1", "Option Alpha");
        var rejected = Evidence("E2", "content-card:option-2", "Option Beta");
        var bundle = EvidenceBundle.Empty(intake.UserQuestion) with
        {
            Items = new[] { used, rejected }
        };

        var memory = SourceBackedConversationMemoryFactory.Create(
            "turn-1",
            intake,
            new[] { request },
            bundle,
            new[] { "E1" },
            new[] { "E2" },
            clarificationRequested: false,
            answerReady: true,
            new[] { "Option Beta ne correspond pas a la demande." });

        var action = Assert.Single(memory.ExecutedActions);
        Assert.Equal("option concrete", action.Query);
        var usedItem = Assert.Single(memory.UsedItems);
        Assert.Equal("content-card:option-1", usedItem.ItemIdentity);
        Assert.Equal("Option Alpha", usedItem.DisplayLabel);
        Assert.Equal(
            "used",
            Assert.Single(memory.EvidenceDecisions, item => item.EvidenceIdAtTurn == "E1").Decision);
        Assert.Equal(
            "rejected",
            Assert.Single(memory.EvidenceDecisions, item => item.EvidenceIdAtTurn == "E2").Decision);
    }

    [Fact]
    public void Factory_records_cited_canonical_chunk_as_used_item()
    {
        var intake = Intake("Résume l'information de sécurité du manuel.");
        var used = CanonicalChunkEvidence();
        var bundle = EvidenceBundle.Empty(intake.UserQuestion) with
        {
            Items = new[] { used }
        };

        var memory = SourceBackedConversationMemoryFactory.Create(
            "turn-canonical-chunk",
            intake,
            Array.Empty<RetrievalRequest>(),
            bundle,
            new[] { "E1" },
            Array.Empty<string>(),
            clarificationRequested: false,
            answerReady: true,
            Array.Empty<string>());

        var evidence = Assert.Single(memory.EvidenceDecisions);
        Assert.Equal("used", evidence.Decision);
        var usedItem = Assert.Single(memory.UsedItems);
        Assert.StartsWith("evidence:", usedItem.ItemIdentity);
        Assert.Contains("chunk-safety-4", usedItem.ItemIdentity);
        Assert.Equal("manual.pdf p.4", usedItem.DisplayLabel);
        Assert.Equal(evidence.StableEvidenceKey, usedItem.StableEvidenceKey);
    }

    [Fact]
    public void Source_payload_persists_memory_rehydrates_idempotently_and_keeps_source_cards_parseable()
    {
        var turn = Turn("turn-persisted", "content-card:option-1", "Option Alpha");
        var sources = new List<ToolMemory.SourceRef>
        {
            new()
            {
                EvidenceId = "E1",
                DocId = "doc-1",
                DocPath = "Knowledge/options.pdf",
                DocName = "options.pdf",
                PageStart = 7,
                PageEnd = 7,
                ContentCardId = "option-1",
                Label = "options.pdf p.7",
                SourceHash = new string('a', 64),
                RevisionId = "rev-1"
            }
        };
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "BuildSourcesPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(string),
                typeof(List<ToolMemory.SourceRef>),
                typeof(SourceBackedConversationTurnMemory)
            },
            modifiers: null);

        var payload = method!.Invoke(null, new object?[] { "rag.answer", sources, turn });
        var json = JsonSerializer.Serialize(payload);
        var cards = SourceCardParser.Parse(json);
        var card = Assert.Single(cards);
        Assert.Equal("Knowledge/options.pdf", card.DocPath);
        Assert.Equal(7, card.PageStart);

        var memory = new ToolMemory();
        var history = new[]
        {
            new ToolMemory.ConversationMemoryMessage(
                "user",
                "Construis un premier plan.",
                null),
            new ToolMemory.ConversationMemoryMessage(
                "assistant",
                "Voici le premier plan.",
                json)
        };
        memory.RehydrateConversationState(history);
        memory.RehydrateConversationState(history);

        var restoredTurn = Assert.Single(memory.SourceBackedConversationTurns);
        Assert.Equal("turn-persisted", restoredTurn.TurnId);
        Assert.Equal("Construis un premier plan.", memory.LastUserMessage);
        Assert.Equal("Voici le premier plan.", memory.LastAssistantAnswer);
        var restoredSource = Assert.Single(memory.LastSourcesUsed);
        Assert.Equal("E1", restoredSource.EvidenceId);
        Assert.Equal("rev-1", restoredSource.RevisionId);
        Assert.Equal("option-1", restoredSource.ContentCardId);
        Assert.Null(restoredSource.ChunkId);
        Assert.Equal("Knowledge/options.pdf", restoredSource.DocPath);
        Assert.Equal(7, restoredSource.PageStart);
    }

    [Fact]
    public void V2_prompt_exposes_prior_used_items_actions_and_rejections_as_non_evidence()
    {
        var first = Turn("turn-1", "content-card:option-1", "Option Alpha");
        var second = Turn(
            "turn-2",
            "content-card:option-3",
            "Option Gamma",
            rejectedIdentity: "content-card:option-2",
            rejectedLabel: "Option Beta");
        var memory = new SourceBackedMemoryContext(
            "cdc-v3-m1lite-m3-m6",
            "fr",
            "professionnel",
            "auto",
            "Construis un premier plan.",
            "Voici le premier plan.",
            "rag.answer",
            "source_backed_pipeline",
            null,
            null,
            null,
            "Knowledge",
            null,
            null,
            Array.Empty<SourceBackedMemorySourceAnchor>(),
            Array.Empty<SourceBackedMemoryResearchNote>(),
            new[] { first, second });
        var intake = Intake("Donne-moi un autre plan.") with
        {
            MemoryContext = memory
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildUserContext",
            BindingFlags.Static | BindingFlags.NonPublic);

        var prompt = Assert.IsType<string>(
            method!.Invoke(null, new object?[] { intake, 4096 }));

        Assert.Contains("MEMOIRE RAG STRUCTUREE PERSISTANTE", prompt);
        Assert.Contains("contexte seulement, jamais preuve", prompt);
        Assert.Contains("content-card:option-1", prompt);
        Assert.Contains("Option Alpha", prompt);
        Assert.Contains("content-card:option-3", prompt);
        Assert.Contains("Option Gamma", prompt);
        Assert.Contains("content-card:option-2", prompt);
        Assert.Contains("Option Beta", prompt);
        Assert.Contains("requete=option concrete turn-2", prompt);
    }

    [Fact]
    public void V2_memory_prompt_is_bounded_at_4k_and_keeps_the_newest_used_identity()
    {
        var turns = Enumerable.Range(1, 40)
            .Select(index => Turn(
                $"turn-{index:D2}",
                $"content-card:option-{index:D2}-with-a-deliberately-long-stable-identity",
                $"Option concrete {index:D2} avec un libelle volontairement detaille"))
            .ToArray();
        var memory = new SourceBackedMemoryContext(
            "cdc-v3-m1lite-m2-m3-m5-m6",
            "fr",
            "professionnel",
            "auto",
            "Question precedente",
            string.Join(' ', Enumerable.Repeat("ancienne reponse volumineuse", 100)),
            "rag.answer",
            "source_backed_pipeline",
            null,
            null,
            null,
            "Knowledge",
            null,
            null,
            Array.Empty<SourceBackedMemorySourceAnchor>(),
            Array.Empty<SourceBackedMemoryResearchNote>(),
            turns);
        var intake = Intake("Donne-moi une nouvelle serie.") with
        {
            MemoryContext = memory
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildUserContext",
            BindingFlags.Static | BindingFlags.NonPublic);

        var prompt = Assert.IsType<string>(
            method!.Invoke(null, new object?[] { intake, 4096 }));

        Assert.True(prompt.Length <= 3_500, $"Prompt length was {prompt.Length}.");
        Assert.Contains("MEMORY_CONTEXT_TRUNCATED", prompt);
        Assert.Contains("content-card:option-40", prompt);
    }

    private static SourceBackedIntake Intake(string question)
        => new(
            question,
            "source_backed_answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "fr");

    private static SourceBackedConversationTurnMemory Turn(
        string turnId,
        string usedIdentity,
        string usedLabel,
        string? rejectedIdentity = null,
        string? rejectedLabel = null)
    {
        var usedEvidence = new SourceBackedConversationEvidenceMemory(
            "stable|" + usedIdentity,
            "E1",
            "used",
            "cited_in_verified_answer",
            "canonical_content_card",
            usedIdentity,
            usedLabel,
            "doc-1",
            "Knowledge/options.pdf",
            new string('a', 64),
            "rev-1",
            usedIdentity,
            7,
            7);
        var rejected = string.IsNullOrWhiteSpace(rejectedIdentity)
            ? Array.Empty<SourceBackedConversationEvidenceMemory>()
            : new[]
            {
                new SourceBackedConversationEvidenceMemory(
                    "stable|" + rejectedIdentity,
                    "E2",
                    "rejected",
                    "llm_semantic_rejection",
                    "canonical_content_card",
                    rejectedIdentity,
                    rejectedLabel,
                    "doc-1",
                    "Knowledge/options.pdf",
                    "hash-1",
                    "rev-1",
                    rejectedIdentity,
                    8,
                    8)
            };
        return new SourceBackedConversationTurnMemory(
            1,
            turnId,
            DateTimeOffset.UtcNow,
            "source_backed_answer",
            "Question " + turnId,
            "answer_ready",
            new[]
            {
                new SourceBackedConversationActionMemory(
                    "action|" + turnId,
                    "rag.multi_search",
                    "option concrete " + turnId,
                    "Knowledge",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    10,
                    "find alternatives")
            },
            new[] { usedEvidence }.Concat(rejected).ToArray(),
            new[]
            {
                new SourceBackedConversationUsedItemMemory(
                    usedIdentity,
                    usedLabel,
                    usedEvidence.StableEvidenceKey,
                    usedEvidence.EvidenceIdAtTurn,
                    usedEvidence.DocPath,
                    usedEvidence.PageStart,
                    usedEvidence.PageEnd)
            },
            Array.Empty<string>());
    }

    private static EvidenceItem Evidence(
        string evidenceId,
        string itemIdentity,
        string title)
    {
        using var cards = JsonDocument.Parse(
            $$"""[{"title":{{JsonSerializer.Serialize(title)}}}]""");
        return new EvidenceItem(
            evidenceId,
            "canonical_content_card",
            "rag.multi_search",
            "option concrete",
            "doc-1",
            "options.pdf",
            "Knowledge/options.pdf",
            "hash-1",
            "rev-1",
            7,
            7,
            null,
            "Source excerpt",
            "source excerpt",
            1,
            1,
            "Knowledge",
            "fr",
            "fr",
            "ok",
            cards.RootElement.Clone(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>())
        {
            ContentCardId = itemIdentity.StartsWith(
                "content-card:",
                StringComparison.OrdinalIgnoreCase)
                ? itemIdentity["content-card:".Length..]
                : itemIdentity
        };
    }

    private static EvidenceItem CanonicalChunkEvidence()
        => new(
            "E1",
            "content",
            "documents.context",
            string.Empty,
            "doc-manual",
            "manual.pdf",
            "Knowledge/manual.pdf",
            new string('b', 64),
            "revision-manual-1",
            4,
            4,
            "chunk-safety-4",
            "Lock the cover before maintenance.",
            "lock the cover before maintenance",
            1,
            1,
            "Knowledge",
            "en",
            "en",
            "ok",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
}
