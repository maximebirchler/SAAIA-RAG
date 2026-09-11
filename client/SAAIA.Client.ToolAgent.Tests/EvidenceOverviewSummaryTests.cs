using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class EvidenceOverviewSummaryTests
{
    [Fact]
    public void Facet_selector_prompt_preserves_explicit_facets_and_canonical_anchor_labels()
    {
        var prompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewFacetSelectionPromptForTests(
                "Réponds aux trois facettes.",
                new[] { "but du document", "contenu", "cas de citation" },
                new[]
                {
                    ("A1", 8, "Le domaine couvre tous les systèmes de management."),
                    ("A2", 10, "L'article 5 décrit le programme d'audit."),
                    ("A3", 21, "Les critères servent de référence de conformité.")
                });

        Assert.Contains("SAAIA_DOCUMENT_OVERVIEW_SELECTOR", prompt, StringComparison.Ordinal);
        Assert.Contains("F1=but du document", prompt, StringComparison.Ordinal);
        Assert.Contains("F2=contenu", prompt, StringComparison.Ordinal);
        Assert.Contains("F3=cas de citation", prompt, StringComparison.Ordinal);
        Assert.Contains("A1 page=8", prompt, StringComparison.Ordinal);
        Assert.Contains("A3 page=21", prompt, StringComparison.Ordinal);
        Assert.Contains("F1=A2,A5", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Facet_selector_parser_accepts_only_complete_ordered_known_mappings()
    {
        var facets = new[] { "but", "contenu", "usage" };
        var allowed = new[] { "A1", "A2", "A3", "A4", "A5" };

        var parsed = ToolAgentOrchestrator
            .TryParseEvidenceOverviewFacetSelectionForTests(
                "F1=A1,A2\nF2=A3\nF3=A4,A5",
                facets,
                allowed);

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "A1", "A2" }, parsed![0]);
        Assert.Equal(new[] { "A3" }, parsed[1]);
        Assert.Equal(new[] { "A4", "A5" }, parsed[2]);
        Assert.Null(ToolAgentOrchestrator
            .TryParseEvidenceOverviewFacetSelectionForTests(
                "F1=A1\nF3=A2\nF2=A3",
                facets,
                allowed));
        Assert.Null(ToolAgentOrchestrator
            .TryParseEvidenceOverviewFacetSelectionForTests(
                "F1=A1\nF2=A3\nF3=A9",
                facets,
                allowed));
        Assert.Null(ToolAgentOrchestrator
            .TryParseEvidenceOverviewFacetSelectionForTests(
                "Voici mon choix: F1=A1\nF2=A3\nF3=A4",
                facets,
                allowed));
        Assert.Null(ToolAgentOrchestrator
            .TryParseEvidenceOverviewFacetSelectionForTests(
                "F1=A1,A2\nF2=A3\nF3=A2,A4",
                facets,
                allowed));
    }

    [Fact]
    public void Facet_evidence_mapping_requires_complete_unique_chunk_identities()
    {
        var evidence = new[]
        {
            Evidence(1, "Premier extrait canonique."),
            Evidence(2, "Deuxième extrait canonique."),
            Evidence(3, "Troisième extrait canonique.")
        };

        var mapped = ToolAgentOrchestrator
            .TryMapEvidenceOverviewFacetsToEvidenceForTests(
                new[]
                {
                    new[] { "chunk-1", "chunk-2" },
                    new[] { "chunk-3" }
                },
                evidence);

        Assert.NotNull(mapped);
        Assert.Equal(new[] { "E1", "E2" }, mapped![0]
            .Select(static item => item.EvidenceId));
        Assert.Equal(new[] { "E3" }, mapped[1]
            .Select(static item => item.EvidenceId));
        Assert.Null(ToolAgentOrchestrator
            .TryMapEvidenceOverviewFacetsToEvidenceForTests(
                new[]
                {
                    new[] { "chunk-1" },
                    new[] { "chunk-missing" }
                },
                evidence));
        Assert.Null(ToolAgentOrchestrator
            .TryMapEvidenceOverviewFacetsToEvidenceForTests(
                new[]
                {
                    new[] { "chunk-1" },
                    new[] { "chunk-1" }
                },
                evidence));
    }

    [Fact]
    public void Router_driven_summary_flow_preserves_planned_overview_facets()
    {
        using var planned = JsonDocument.Parse("""
            {
              "docRef": "Guide audit.pdf",
              "strategy": "evidence_overview",
              "overviewFacets": [
                "à quoi sert ce document",
                "quelles informations il contient",
                "dans quels cas le citer"
              ],
              "requestedPointCount": 3,
              "sampleCount": 4
            }
            """);

        var liveArgs = ToolAgentOrchestrator
            .BuildDocumentSummaryLiveArgsForTests(
                "Guide audit.pdf",
                "fr",
                "Fais-moi une synthèse utile de ce document.",
                planned.RootElement);

        Assert.Equal(
            "Guide audit.pdf",
            liveArgs.GetProperty("docRef").GetString());
        Assert.Equal(
            "evidence_overview",
            liveArgs.GetProperty("strategy").GetString());
        Assert.Equal(
            new[]
            {
                "à quoi sert ce document",
                "quelles informations il contient",
                "dans quels cas le citer"
            },
            liveArgs.GetProperty("overviewFacets")
                .EnumerateArray()
                .Select(static facet => facet.GetString()));
        Assert.Equal(3, liveArgs.GetProperty("requestedPointCount").GetInt32());
        Assert.Equal(4, liveArgs.GetProperty("sampleCount").GetInt32());
    }

    [Fact]
    public void Extractive_fallback_is_explicitly_non_synthetic_and_keeps_one_local_citation_per_excerpt()
    {
        var evidence = new[]
        {
            Evidence(1, "La source décrit la portée exacte du document."),
            Evidence(2, "La source décrit les principes et le programme d'audit."),
            Evidence(3, "La source décrit la compétence des auditeurs.")
        };
        var text = ToolAgentOrchestrator
            .BuildEvidenceOverviewExtractiveFallbackTextForTests(
                "fr",
                new[] { "but du document", "contenu" },
                new IReadOnlyList<EvidenceItem>[]
                {
                    new[] { evidence[0] },
                    new[]
                    {
                        evidence[1],
                        evidence[2]
                    }
                });

        Assert.Contains("uniquement les extraits canoniques", text, StringComparison.Ordinal);
        Assert.Contains("ni une synthèse ni des conclusions", text, StringComparison.Ordinal);
        Assert.Contains("Facette — but du document", text, StringComparison.Ordinal);
        Assert.Contains("Facette — contenu", text, StringComparison.Ordinal);
        Assert.Contains("La source décrit la portée exacte du document. [E1]", text, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(text, @"\[E1\]").Cast<Match>());
        Assert.Single(Regex.Matches(text, @"\[E2\]").Cast<Match>());
        Assert.Single(Regex.Matches(text, @"\[E3\]").Cast<Match>());
        var verification = SourceContractVerifier.Verify(
            new WriterDraft(text, new[] { "E1", "E2", "E3" }),
            new EvidenceBundle(
                "bundle-1",
                "question",
                evidence,
                Array.Empty<SourceBackedTraceEvent>()),
            allowedEvidenceIds: new[] { "E1", "E2", "E3" },
            enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true,
            allowMultipleEvidencePerVisibleSource: true,
            requireSeparateAtomicClaims: true);
        Assert.True(
            verification.IsValid,
            string.Join(" | ", verification.Errors.Select(
                static error => error.Code + ":" + error.Message)));
    }

    [Fact]
    public void Page_sampling_covers_the_whole_document_with_bucket_midpoints()
    {
        Assert.Equal(
            new[] { 9, 26, 43, 60, 76, 93, 110, 127 },
            ToolAgentOrchestrator.SelectEvidenceOverviewTargetPagesForTests(
                pageCount: 135,
                requestedCount: 8));
    }

    [Fact]
    public void Overprovisioned_page_sampling_keeps_twelve_distinct_document_zones()
    {
        Assert.Equal(
            new[] { 6, 17, 29, 40, 51, 62, 74, 85, 96, 107, 119, 130 },
            ToolAgentOrchestrator.SelectEvidenceOverviewTargetPagesForTests(
                pageCount: 135,
                requestedCount: 12));
    }

    [Theory]
    [InlineData("Users shall assess whole-body access.", "requirement")]
    [InlineData("Periodic training should be considered.", "recommendation")]
    [InlineData("The annex gives an injury table.", "context")]
    public void Source_strength_is_derived_only_from_visible_source_modality(
        string sourceText,
        string expected)
    {
        Assert.Equal(
            expected,
            ToolAgentOrchestrator.ResolveEvidenceOverviewSourceStrengthForTests(
                sourceText));
    }

    [Fact]
    public void Candidate_filter_treats_il_faut_as_obligation_wording()
    {
        var result = ToolAgentOrchestrator
            .ValidateAndRenderEvidenceOverviewCandidatesForTests(
                "Il faut appliquer cette règle [E1]",
                new[] { Evidence(1, "This is contextual information.") },
                requestedPointCount: 1,
                responseLanguage: "fr");

        Assert.Equal(0, result.ValidCount);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.StartsWith(
                "unsupported_obligation:E1:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_filter_rejects_an_unsupported_obligation_and_keeps_seven_verified_claims()
    {
        var evidence = Enumerable.Range(1, 8)
            .Select(index => Evidence(
                index,
                index == 4
                    ? "Users shall assess whole-body access."
                    : $"Contextual source statement {index}."))
            .ToArray();
        var raw = """
            1. Les utilisateurs doivent appliquer une règle non présente [E1]
            2. Le document définit plusieurs notions utiles à la sécurité [E2]
            3. L'évaluation des risques suit des étapes structurées [E3]
            4. L'accès du corps entier fait l'objet d'une évaluation [E4]
            5. La formation périodique entretient les compétences [E5]
            6. Le tableau recense plusieurs catégories de blessures [E6]
            7. Le verrouillage isole les énergies dangereuses [E7]
            8. Des seuils de force réduite sont présentés [E8]
            """;

        var result = ToolAgentOrchestrator
            .ValidateAndRenderEvidenceOverviewCandidatesForTests(
                raw,
                evidence,
                requestedPointCount: 7,
                responseLanguage: "fr");

        Assert.Equal(7, result.ValidCount);
        Assert.Equal(
            new[] { "E2", "E3", "E4", "E5", "E6", "E7", "E8" },
            result.CitedEvidenceIds);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.StartsWith(
                "unsupported_obligation:E1:",
                StringComparison.Ordinal));
        Assert.Equal(7, result.RenderedAnswer.Split('\n').Length);
        Assert.Contains("[E8]", result.RenderedAnswer, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_contract_preserves_actor_object_and_qualifier_relations()
    {
        var prompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewWriterPromptForTests(
                "Résume le document.",
                "fr",
                new[] { Evidence(1, "People responsible for operation shall be periodically retrained.") });

        Assert.Contains(
            "Conserve l'acteur, l'action, l'objet, la relation, la portee et les conditions utiles",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "ne transforme pas une observation en obligation",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "n'ajoute aucun qualificatif non documente",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "People responsible for operation shall be periodically retrained.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "une seule phrase par ligne",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "12 mots maximum",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_and_writer_have_separate_semantic_and_fixed_order_contracts()
    {
        var evidence = Enumerable.Range(1, 8)
            .Select(index => Evidence(index, $"Canonical evidence {index}."))
            .ToArray();
        var selectorPrompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewSelectionPromptForTests(
                "Résume le document en 7 points.",
                requestedPointCount: 7,
                evidence);
        var writerPrompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewWriterPromptForTests(
                "Résume le document en 7 points.",
                "fr",
                evidence.Take(7).ToArray());

        Assert.Contains(
            "Select exactly 7 distinct evidence IDs from the 8 items",
            selectorPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Choose coherent self-contained passages",
            selectorPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "OCR-interleaved table columns",
            selectorPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "E1 page=1 source=Canonical evidence 1.",
            selectorPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Reponds a la demande en exactement 7 phrases, une seule phrase par ligne",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "La phrase 1 synthetise uniquement E1 et finit par [E1]",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.Contains("La phrase 7 synthetise uniquement E7 et finit par [E7]", writerPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("La phrase 8", writerPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_parser_accepts_only_exact_unique_known_ids_without_commentary()
    {
        var evidence = Enumerable.Range(1, 8)
            .Select(index => Evidence(index, $"Canonical evidence {index}."))
            .ToArray();

        Assert.Equal(
            new[] { "E1", "E2", "E3", "E4", "E5", "E7", "E8" },
            ToolAgentOrchestrator.TryParseEvidenceOverviewSelectionForTests(
                "E1,E2,E3,E4,E5,E7,E8",
                evidence,
                requestedPointCount: 7));
        Assert.Null(
            ToolAgentOrchestrator.TryParseEvidenceOverviewSelectionForTests(
                "I choose E1,E2,E3,E4,E5,E7,E8",
                evidence,
                requestedPointCount: 7));
        Assert.Null(
            ToolAgentOrchestrator.TryParseEvidenceOverviewSelectionForTests(
                "E1,E2,E3,E4,E5,E7,E7",
                evidence,
                requestedPointCount: 7));
        Assert.Null(
            ToolAgentOrchestrator.TryParseEvidenceOverviewSelectionForTests(
                "E1,E2,E3,E4,E5,E7,E9",
                evidence,
                requestedPointCount: 7));
    }

    [Fact]
    public void Selector_prompt_keeps_all_twelve_sources_visible_within_a_compact_budget()
    {
        var evidence = Enumerable.Range(1, 12)
            .Select(index => Evidence(
                index,
                $"Canonical source {index}: " + new string('x', 1800)))
            .ToArray();

        var prompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewSelectionPromptForTests(
                "Résume le document en 7 points.",
                requestedPointCount: 7,
                evidence);

        Assert.True(prompt.Length < 10_000, $"prompt length: {prompt.Length}");
        Assert.Contains("E12 page=12 source=Canonical source 12:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 651), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidate_filter_rejects_placeholder_text_and_wrong_source_order()
    {
        var evidence = new[]
        {
            Evidence(1, "Canonical evidence one."),
            Evidence(2, "Canonical evidence two.")
        };

        var placeholder = ToolAgentOrchestrator
            .ValidateAndRenderEvidenceOverviewCandidatesForTests(
                "[E#] [E1]\nPoint utile [E2]",
                evidence,
                requestedPointCount: 2,
                responseLanguage: "fr");
        var shifted = ToolAgentOrchestrator
            .ValidateAndRenderEvidenceOverviewCandidatesForTests(
                "Point deux [E2]\nPoint un [E1]",
                evidence,
                requestedPointCount: 2,
                responseLanguage: "fr");

        Assert.Equal(1, placeholder.ValidCount);
        Assert.Contains(
            placeholder.Rejections,
            rejection => rejection.StartsWith(
                "placeholder_candidate:E1:",
                StringComparison.Ordinal));
        Assert.Equal(0, shifted.ValidCount);
        Assert.All(
            shifted.Rejections,
            rejection => Assert.StartsWith(
                "source_order:",
            rejection,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_repair_is_scoped_to_one_rejected_evidence_line()
    {
        var evidence = Evidence(
            2,
            "This informative annex lists reduced force values.");
        var prompt = ToolAgentOrchestrator
            .BuildEvidenceOverviewCandidateRepairPromptForTests(
                evidence,
                "Les valeurs doivent être appliquées.",
                "fr");
        var replaced = ToolAgentOrchestrator
            .ReplaceEvidenceOverviewCandidateLineForTests(
                "Point un [E1]\nLes valeurs doivent être appliquées [E2]\nPoint trois [E3]",
                "E2",
                "L'annexe répertorie des valeurs de force réduite [E2]");

        Assert.Contains(
            "SAAIA_DOCUMENT_OVERVIEW_CANDIDATE_REPAIR",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_strength=context",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "N'utilise doit, obligatoire, exige, requis ou faut que si source_strength=requirement",
            prompt,
            StringComparison.Ordinal);
        Assert.Equal(
            "Point un [E1]" + Environment.NewLine
            + "L'annexe répertorie des valeurs de force réduite [E2]"
            + Environment.NewLine + "Point trois [E3]",
            replaced);
    }

    [Fact]
    public void Verified_summary_anchors_are_retained_in_conversation_memory()
    {
        var json = """
            {
              "summaryText": "Point un [E1]\nPoint deux [E2]",
              "docId": "doc-1",
              "docPath": "Normes/Named document.pdf",
              "docName": "Named document.pdf",
              "anchors": [
                {
                  "evidenceId": "E1",
                  "docId": "doc-1",
                  "docPath": "Normes/Named document.pdf",
                  "docName": "Named document.pdf",
                  "pageStart": 9,
                  "pageEnd": 9,
                  "label": "Named document.pdf",
                  "sourceHash": "hash-1",
                  "revisionId": "revision-1",
                  "chunkId": "chunk-1",
                  "anchorId": "anchor-1"
                },
                {
                  "evidenceId": "E2",
                  "docId": "doc-1",
                  "docPath": "Normes/Named document.pdf",
                  "docName": "Named document.pdf",
                  "pageStart": 26,
                  "pageEnd": 26,
                  "label": "Named document.pdf",
                  "sourceHash": "hash-1",
                  "revisionId": "revision-1",
                  "chunkId": "chunk-2",
                  "anchorId": "anchor-2"
                }
              ]
            }
            """;

        using var parsed = JsonDocument.Parse(
            ToolAgentOrchestrator.BuildSummaryMemorySourcesForTests(
                "rag.summarize_live",
                json));
        var sources = parsed.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, sources.Length);
        Assert.Equal("E1", sources[0].GetProperty("EvidenceId").GetString());
        Assert.Equal("revision-1", sources[0].GetProperty("RevisionId").GetString());
        Assert.Equal("chunk-1", sources[0].GetProperty("ChunkId").GetString());
        Assert.Equal("anchor-1", sources[0].GetProperty("AnchorId").GetString());
        Assert.Equal(26, sources[1].GetProperty("PageStart").GetInt32());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Overview_writer_receives_the_complete_selected_passage_including_late_conditions(string language)
    {
        var excerpt = new string('x', 2400) + "\nOnly the Zeta series uses this setting; all other series are excluded.";
        var question = new string('q', 1400) + "\nWhich series is eligible?";
        var prompt = ToolAgentOrchestrator.BuildEvidenceOverviewWriterPromptForTests(question, language, [Evidence(1, excerpt)]);
        Assert.Contains(question, prompt, StringComparison.Ordinal);
        Assert.Contains(excerpt, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("E999", true)]
    [InlineData("E0", true)]
    [InlineData("E1000", true)]
    [InlineData("E99999", true)]
    [InlineData("E999", false)]
    public void Overview_does_not_replace_an_explicit_unknown_citation_by_the_expected_line_identity(string unknown, bool spaceBeforeCitation)
    {
        var result = ToolAgentOrchestrator.ValidateAndRenderEvidenceOverviewCandidatesForTests(
            $"La pression est de 73 unités{(spaceBeforeCitation ? " " : "")}[{unknown}].\nLe cycle dure 12 minutes [E2].",
            [Evidence(1, "La pression est de 73 unités."), Evidence(2, "Le cycle dure 12 minutes.")], 2, "fr");
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(new[] { "E2" }, result.CitedEvidenceIds);
        Assert.Contains(result.Rejections, reason => reason == "unknown_evidence:" + unknown);
        Assert.DoesNotContain("[E1]", result.RenderedAnswer, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_preserves_evidence_like_suffixes_inside_ordinary_words()
    {
        var result = ToolAgentOrchestrator.ValidateAndRenderEvidenceOverviewCandidatesForTests(
            "Le paramètre documenté est MODE1.\nLe cycle dure 12 minutes [E2].",
            [Evidence(1, "Le paramètre documenté est MODE1."), Evidence(2, "Le cycle dure 12 minutes.")], 2, "fr");
        Assert.Equal(2, result.ValidCount);
        Assert.Contains("MODE1", result.RenderedAnswer, StringComparison.Ordinal);
    }

    private static EvidenceItem Evidence(int index, string excerpt)
        => new(
            $"E{index}",
            "canonical_document_chunk",
            "documents.context",
            string.Empty,
            "doc-1",
            "Named document.pdf",
            "Normes/Named document.pdf",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "revision-1",
            index,
            index,
            $"chunk-{index}",
            excerpt,
            excerpt,
            null,
            index,
            "Normes",
            "en",
            "en",
            "ok",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
}
