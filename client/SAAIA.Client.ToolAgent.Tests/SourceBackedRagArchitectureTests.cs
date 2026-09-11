using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedRagArchitectureTests
{
    [Fact]
    public void Rag_hit_normalization_preserves_the_indexed_revision_identity()
    {
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(
            """
            {
              "items": [
                {
                  "docId": "doc-1",
                  "docPath": "Knowledge/reference.pdf",
                  "docName": "reference.pdf",
                  "revisionId": "revision-active",
                  "sourceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "pageStart": 7,
                  "pageEnd": 7,
                  "chunkId": "chunk-7",
                  "text": "Canonical source-backed evidence."
                }
              ]
            }
            """);

        var hit = normalized.GetProperty("hits")[0];
        Assert.Equal("revision-active", hit.GetProperty("revisionId").GetString());
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            hit.GetProperty("sourceHash").GetString());
    }

    [Fact]
    public void Assistant_messages_render_their_own_canonical_source_cards_inline()
    {
        var message = new SAAIA.Client.WinUI.Models.ChatMessageItem
        {
            SourcesJson =
                """
                {
                  "sources": [
                    {
                      "evidenceId": "E1",
                      "docId": "doc-1",
                      "docPath": "Knowledge/reference.pdf",
                      "docName": "reference.pdf",
                      "revisionId": "revision-active",
                      "sourceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "pageStart": 7,
                      "pageEnd": 7,
                      "chunkId": "chunk-7"
                    }
                  ]
                }
                """
        };

        var parsedSourcesProperty = typeof(SAAIA.Client.WinUI.Models.ChatMessageItem)
            .GetProperty("ParsedSources");
        Assert.NotNull(parsedSourcesProperty);
        var parsedSources = Assert.IsAssignableFrom<IList<SAAIA.Client.WinUI.Models.SourceCard>>(
            parsedSourcesProperty!.GetValue(message));
        var source = Assert.Single(parsedSources);
        Assert.Equal("E1", source.EvidenceId);
        Assert.Equal("revision-active", source.RevisionId);

        var xaml = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "MainWindow.xaml"));
        Assert.Contains(
            "<controls:SourcesCardsControl Items=\"{x:Bind ParsedSources, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_agent_is_mandatory_and_has_no_runtime_disable_switch()
    {
        Assert.NotNull(SourceBackedAgentV2Options.ResolveFromEnvironment());
        Assert.Null(typeof(SourceBackedAgentV2Options).GetProperty("Enabled"));
        Assert.Null(typeof(SourceBackedAgentV2Options).GetField(
            "EnabledEnvironmentVariable",
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic));

        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "ToolAgentOrchestrator.SourceBackedAgentV2.cs"));

        Assert.Contains("_llm is ISourceBackedAgentLlmClient", source);
        Assert.Contains("requires native LLM tool support", source);
        Assert.DoesNotContain("return null", source);
        Assert.DoesNotContain("SAAIA_SOURCE_BACKED_AGENT_V2\"", source);
    }

    [Fact]
    public void Production_defaults_enable_llm_candidate_definition_and_precision_batches()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag",
            "SourceBackedAgentV2Options.cs"));

        Assert.Contains(
            "\"SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_DEFINITION\",\n                defaultValue: true",
            source.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains(
            "\"SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATES_PER_AUDIT_BATCH\",\n                6,\n                1,\n                80",
            source.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_source_backed_runtime_families_are_absent()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");

        Assert.Empty(Directory.EnumerateFiles(
            directory,
            "SourceBackedRagPipeline*.cs"));
        Assert.Empty(Directory.EnumerateFiles(
            directory,
            "SourceBackedRagPrompts*.cs"));
        Assert.Empty(Directory.EnumerateFiles(
            directory,
            "SourceBackedRagJson*.cs"));
        Assert.False(File.Exists(Path.Combine(
            directory,
            "SourceBackedOrchestrationProfile.cs")));
    }

    [Fact]
    public void Canonical_source_backed_files_stay_modular()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");
        var limits = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["SourceBackedAgentV2Runner.cs"] = 2500,
            ["SourceBackedAgentV2Prompts.cs"] = 800,
            ["SourceBackedAgentCompactFollowUp.cs"] = 550,
            ["SourceBackedAgentSemanticPlan.cs"] = 550
        };
        var files = Directory
            .EnumerateFiles(directory, "*.cs")
            .Select(path => new
            {
                Path = path,
                Lines = File.ReadLines(path).Count()
            })
            .ToArray();

        Assert.NotEmpty(files);
        Assert.DoesNotContain(
            files,
            file => file.Lines > limits.GetValueOrDefault(
                Path.GetFileName(file.Path),
                500));
    }

    [Fact]
    public void Canonical_agent_executor_bypasses_the_legacy_tool_pipeline()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "ToolAgentOrchestrator.SourceBackedRagToolExecutor.cs"));
        var nativeStart = source.IndexOf(
            "public Task<ToolResults> ExecuteToolCallAsync(",
            StringComparison.Ordinal);
        var nativeEnd = source.IndexOf(
            "private JsonElement PrepareNativeSourceBackedArguments(",
            nativeStart,
            StringComparison.Ordinal);

        Assert.True(nativeStart >= 0 && nativeEnd > nativeStart);
        var nativeArea = source[nativeStart..nativeEnd];
        Assert.Contains("ExecuteNativeSourceBackedToolAsync(", nativeArea);
        Assert.Contains("ExecRagSearchAsync(", nativeArea);
        Assert.Contains("ExecDocumentsNavigationAsync(", nativeArea);
        Assert.DoesNotContain("ExecuteToolsAsync(", nativeArea);
    }

    [Fact]
    public void Canonical_multi_search_is_mechanical_and_domain_neutral()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "ToolAgentOrchestrator.SourceBackedCanonicalMultiSearch.cs");
        var source = File.ReadAllText(path);

        Assert.True(File.ReadLines(path).Count() <= 400);
        Assert.Contains("BuildRagHitDedupeKey", source);
        Assert.DoesNotContain("NormalizeRagQueryForRetrieval", source);
        Assert.DoesNotContain("TryInferCategoryScope", source);
        Assert.DoesNotContain("cuisine", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("meal", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_source_contract_uses_typed_axes_not_domain_regexes()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");
        var source = File.ReadAllText(Path.Combine(
            directory,
            "SourceContractVerifier.Structured.cs"));

        Assert.Contains(
            "SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake",
            source);
        Assert.DoesNotContain(
            "monday|tuesday|wednesday",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cuisine", source);
    }

    [Fact]
    public void Structured_writer_uses_local_repair_and_has_a_terminal_failure_path()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");
        var writer = File.ReadAllText(Path.Combine(
            directory,
            "SourceBackedAgentStructuredCandidateWriter.cs"));
        var runner = File.ReadAllText(Path.Combine(
            directory,
            "SourceBackedAgentV2Runner.cs"));

        Assert.Contains("TryPrepareStructuredCandidateTableRepair", writer);
        Assert.Contains("structured_candidate_cells_repaired", writer);
        Assert.DoesNotContain(
            "BuildStructuredCandidateTableRepairInstruction",
            writer);
        Assert.DoesNotContain(
            "CompleteStructuredCandidateColumnsAsync",
            writer);
        Assert.Contains(
            "source_backed_agent_v2.structured_candidate_writer.mechanical_repair_exhausted",
            runner);
        Assert.Contains(
            "(\"decision\", \"terminal_insufficient_evidence\")));\n                break;",
            runner.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Structured_semantic_review_is_batched_by_layout_and_revision_is_batched()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag");
        var review = File.ReadAllText(Path.Combine(
            directory,
            "SourceBackedAgentStructuredAssignmentReview.cs"));
        var batchReview = File.ReadAllText(Path.Combine(
            directory,
            "SourceBackedAgentStructuredAssignmentBatchReview.cs"));
        var revision = File.ReadAllText(Path.Combine(
            directory,
            "SourceBackedAgentStructuredAssignmentRevision.cs"));

        Assert.Contains("ReviewBatchAsync", review);
        Assert.Contains("CompleteStructuredAssignmentBatchReviewAsync", review);
        Assert.Contains("JUGE CHAQUE CELLULE", batchReview);
        Assert.False(File.Exists(Path.Combine(
            directory,
            "SourceBackedAgentStructuredAssignmentColumnReview.cs")));
        Assert.False(File.Exists(Path.Combine(
            directory,
            "SourceBackedAgentStructuredAssignmentGlobalReview.cs")));
        Assert.Contains("structured_assignment_batch_revision", revision);
        Assert.DoesNotContain(
            "TryParseSingleStructuredAssignmentRevision",
            revision);
        Assert.DoesNotContain(
            "var remainingEvidenceIds = availableEvidenceIds.ToList()",
            revision);
    }

    [Fact]
    public void Document_context_preserves_active_revision_and_chunk_lineage()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.context",
            Result = JsonSerializer.SerializeToElement(new
            {
                document = new
                {
                    docId = "doc-1",
                    docName = "sample.pdf",
                    docPath = "Knowledge/sample.pdf",
                    revisionId = "revision-active",
                    sourceHash = "source-hash-active"
                },
                items = new[]
                {
                    new
                    {
                        chunkId = "chunk-1",
                        pageStart = 7,
                        pageEnd = 7,
                        text = "Current indexed proof."
                    }
                }
            })
        });

        var evidence = Assert.Single(
            EvidenceBundleBuilder.FromToolResults(results, "Question").Items);
        Assert.Equal("revision-active", evidence.RevisionId);
        Assert.Equal("source-hash-active", evidence.SourceHash);
        Assert.Equal("chunk-1", evidence.ChunkId);
    }

    [Fact]
    public void Canonical_evidence_contract_has_explicit_specialized_identity_fields()
    {
        Assert.NotNull(typeof(EvidenceItem).GetProperty("AnchorId"));
        Assert.NotNull(typeof(EvidenceItem).GetProperty("ContentCardId"));
        Assert.NotNull(typeof(ToolMemory.SourceRef).GetProperty("AnchorId"));
        Assert.NotNull(typeof(ToolMemory.SourceRef).GetProperty("ContentCardId"));
        Assert.NotNull(typeof(SAAIA.Client.WinUI.Models.SourceCard).GetProperty("AnchorId"));
        Assert.NotNull(typeof(SAAIA.Client.WinUI.Models.SourceCard).GetProperty("ContentCardId"));
    }

    [Fact]
    public void Content_card_builder_does_not_overload_chunk_identity()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.content_cards",
            Result = JsonSerializer.SerializeToElement(new
            {
                items = new[]
                {
                    new
                    {
                        docId = "doc-1",
                        docName = "sample.pdf",
                        docPath = "Knowledge/sample.pdf",
                        revisionId = "revision-active",
                        sourceHash = new string('a', 64),
                        contentCardId = "card-1",
                        pageStart = 7,
                        pageEnd = 7,
                        title = "Source-backed fact"
                    }
                }
            })
        });

        var evidence = Assert.Single(
            EvidenceBundleBuilder.FromToolResults(results, "Question").Items);
        var contentCardId = typeof(EvidenceItem)
            .GetProperty("ContentCardId")?
            .GetValue(evidence) as string;

        Assert.Equal("card-1", contentCardId);
        Assert.Null(evidence.ChunkId);
    }

    [Fact]
    public void Navigation_builder_keeps_anchor_and_chunk_as_distinct_identities()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.navigation",
            Result = JsonSerializer.SerializeToElement(new
            {
                items = new[]
                {
                    new
                    {
                        docId = "doc-1",
                        docName = "sample.pdf",
                        docPath = "Knowledge/sample.pdf",
                        revisionId = "revision-active",
                        sourceHash = new string('a', 64),
                        label = "Safety requirements",
                        targetPageStart = 7,
                        targetPageEnd = 8,
                        targetChunkId = "chunk-7",
                        targetAnchorId = "anchor-7"
                    }
                }
            })
        });

        var evidence = Assert.Single(
            EvidenceBundleBuilder.FromToolResults(results, "Question").Items);
        var anchorId = typeof(EvidenceItem)
            .GetProperty("AnchorId")?
            .GetValue(evidence) as string;

        Assert.Equal("anchor-7", anchorId);
        Assert.Equal("chunk-7", evidence.ChunkId);
    }

    [Fact]
    public void Verified_ui_payload_preserves_specialized_evidence_identity()
    {
        var evidence = CreateCanonicalEvidenceForIdentityTests();
        typeof(EvidenceItem).GetProperty("AnchorId")?.SetValue(evidence, "anchor-7");
        typeof(EvidenceItem).GetProperty("ContentCardId")?.SetValue(evidence, "card-7");
        var intake = new SourceBackedIntake(
            "Question",
            "answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            "fr");
        var bundle = new EvidenceBundle(
            "bundle-1",
            intake.UserQuestion,
            new[] { evidence },
            Array.Empty<SourceBackedTraceEvent>());
        var draft = new WriterDraft("Answer [E1]", new[] { "E1" });
        var result = new SourceBackedPipelineResult(
            "result-1",
            intake,
            null,
            bundle,
            new EvidenceJudgeDecision(
                "write",
                new[] { "E1" },
                Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(),
                Array.Empty<string>()),
            draft,
            draft,
            new SourceVerificationResult(
                true,
                Array.Empty<SourceVerificationError>(),
                new[] { evidence }),
            false,
            Array.Empty<SourceBackedTraceEvent>());

        var source = Assert.Single(
            SourceBackedUiPayloadMapper.FromVerifiedResult(result).Sources);
        Assert.Equal(
            "anchor-7",
            typeof(ToolMemory.SourceRef).GetProperty("AnchorId")?.GetValue(source));
        Assert.Equal(
            "card-7",
            typeof(ToolMemory.SourceRef).GetProperty("ContentCardId")?.GetValue(source));
    }

    [Fact]
    public void Ui_sources_keep_distinct_evidence_on_the_same_page()
    {
        var cards = SourceCardParser.Parse(
            """
            {
              "sources": [
                {
                  "evidenceId": "E1",
                  "docId": "doc-1",
                  "docPath": "Knowledge/sample.pdf",
                  "docName": "sample.pdf",
                  "pageStart": 7,
                  "pageEnd": 7,
                  "chunkId": "card-1",
                  "revisionId": "revision-active",
                  "sourceHash": "hash-active"
                },
                {
                  "evidenceId": "E2",
                  "docId": "doc-1",
                  "docPath": "Knowledge/sample.pdf",
                  "docName": "sample.pdf",
                  "pageStart": 7,
                  "pageEnd": 7,
                  "chunkId": "card-2",
                  "revisionId": "revision-active",
                  "sourceHash": "hash-active"
                }
              ]
            }
            """);

        Assert.Equal(2, cards.Count);
        Assert.Equal(
            new[] { "E1", "E2" },
            cards.Select(static card => card.EvidenceId)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Ui_payload_mapper_keeps_each_cited_chunk_on_the_same_page()
    {
        var first = CreateCanonicalEvidenceForIdentityTests() with
        {
            SourceKind = "document_chunk",
            ContentCardId = null,
            ChunkId = "chunk-features",
            Excerpt = "Documented features.",
            NormalizedExcerpt = "documented features"
        };
        var second = first with
        {
            EvidenceId = "E2",
            ChunkId = "chunk-properties",
            Excerpt = "Documented properties.",
            NormalizedExcerpt = "documented properties",
            Rank = 2
        };
        var intake = new SourceBackedIntake(
            "Question",
            "answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            "fr");
        var bundle = new EvidenceBundle(
            "bundle-same-page",
            intake.UserQuestion,
            new[] { first, second },
            Array.Empty<SourceBackedTraceEvent>());
        var draft = new WriterDraft(
            "Feature [E1]. Property [E2].",
            new[] { "E1", "E2" });
        var result = new SourceBackedPipelineResult(
            "result-same-page",
            intake,
            null,
            bundle,
            new EvidenceJudgeDecision(
                "write",
                new[] { "E1", "E2" },
                Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(),
                Array.Empty<string>()),
            draft,
            draft,
            new SourceVerificationResult(
                true,
                Array.Empty<SourceVerificationError>(),
                new[] { first, second }),
            false,
            Array.Empty<SourceBackedTraceEvent>());

        var sources = SourceBackedUiPayloadMapper
            .FromVerifiedResult(result)
            .Sources;

        Assert.Equal(2, sources.Count);
        Assert.Equal(
            new[] { "E1", "E2" },
            sources.Select(static source => source.EvidenceId).ToArray());
        Assert.Equal(
            new[] { "chunk-features", "chunk-properties" },
            sources.Select(static source => source.ChunkId).ToArray());
    }

    [Fact]
    public void Source_payload_preserves_evidence_and_revision_identity_after_normalization()
    {
        using var payload = JsonDocument.Parse(
            ToolAgentOrchestrator.BuildDistinctEvidenceSourcesPayloadForTests());
        var sources = payload.RootElement.GetProperty("sources").EnumerateArray()
            .ToArray();

        Assert.Equal(2, sources.Length);
        Assert.Equal(
            new[] { "E1", "E2" },
            sources.Select(static source =>
                    source.GetProperty("evidenceId").GetString())
                .ToArray());
        Assert.All(sources, static source => Assert.Equal(
            "revision-active",
            source.GetProperty("revisionId").GetString()));
        Assert.Equal(
            new[] { "card-1", "card-2" },
            sources.Select(static source =>
                    source.GetProperty("contentCardId").GetString())
                .ToArray());
        Assert.All(sources, static source => Assert.Equal(
            JsonValueKind.Null,
            source.GetProperty("chunkId").ValueKind));
    }

    [Fact]
    public void Source_card_parser_preserves_explicit_anchor_and_content_card_identity()
    {
        var card = Assert.Single(SourceCardParser.Parse(
            """
            {
              "sources": [
                {
                  "evidenceId": "E1",
                  "docId": "doc-1",
                  "docPath": "Knowledge/sample.pdf",
                  "docName": "sample.pdf",
                  "pageStart": 7,
                  "pageEnd": 7,
                  "chunkId": "chunk-7",
                  "anchorId": "anchor-7",
                  "contentCardId": "card-7",
                  "revisionId": "revision-active",
                  "sourceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ]
            }
            """));

        Assert.Equal(
            "anchor-7",
            typeof(SAAIA.Client.WinUI.Models.SourceCard)
                .GetProperty("AnchorId")?
                .GetValue(card));
        Assert.Equal(
            "card-7",
            typeof(SAAIA.Client.WinUI.Models.SourceCard)
                .GetProperty("ContentCardId")?
                .GetValue(card));
        Assert.Equal("chunk-7", card.ChunkId);
    }

    [Fact]
    public void Source_card_click_requires_exact_revision_and_forwards_all_identity_fields()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "Controls",
            "SourcesCardsControl.xaml.cs"));
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("s.SourceHash", source, StringComparison.Ordinal);
        Assert.Contains("s.RevisionId", source, StringComparison.Ordinal);
        Assert.Contains("s.ChunkId", source, StringComparison.Ordinal);
        Assert.Contains("s.AnchorId", source, StringComparison.Ordinal);
        Assert.Contains("s.ContentCardId", source, StringComparison.Ordinal);
        Assert.Contains("requireExactSourceHash: true", normalized, StringComparison.Ordinal);
    }

    private static EvidenceItem CreateCanonicalEvidenceForIdentityTests()
        => new(
            "E1",
            "canonical_content_card",
            "documents.content_cards",
            "Question",
            "doc-1",
            "sample.pdf",
            "Knowledge/sample.pdf",
            new string('a', 64),
            "revision-active",
            7,
            7,
            null,
            "Source-backed fact",
            "source-backed fact",
            1.0,
            1,
            "Knowledge",
            "fr",
            "fr",
            "good",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
                return current;

            current = Directory.GetParent(current)?.FullName
                      ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
