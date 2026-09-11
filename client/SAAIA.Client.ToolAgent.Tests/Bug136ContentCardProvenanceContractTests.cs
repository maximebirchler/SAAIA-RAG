using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class Bug136ContentCardProvenanceContractTests
{
    private const string RevisionSha =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ConflictingRevisionSha =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string InvalidCatalogHash = "0123456789abcdef0123456789abcdef";
    private const string MissingProofRisk =
        "missing_grounded_content_card_evidence";

    [Fact]
    public void Bug136_BackendContentCardProjectionUsesIndexedRevisionSha256()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "backend",
            "SAAIA.Backend",
            "Endpoints",
            "DocumentsEndpoints.ContentCards.cs"));

        Assert.Contains("r.source_hash", source, StringComparison.Ordinal);
        Assert.Contains(
            "encode(d.revision_source_hash, 'hex') AS \"SourceHash\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "saaia_document_summary_source_hash(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug136_ValidCardShaAndExactProofArePreservedWithoutHydration()
    {
        const string proof =
            "The documented operating mode is selected from the local display.";
        var bundle = BuildBundle(ContentCardResult(new
        {
            contentCardId = "card-valid",
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = RevisionSha,
            title = "Local operating mode",
            pageStart = 7,
            pageEnd = 7,
            hasGroundedEvidence = false,
            evidence = new { sourceText = proof }
        }));

        var card = Assert.Single(bundle.Items);
        Assert.Equal(RevisionSha, card.SourceHash);
        Assert.Contains(proof, card.Excerpt, StringComparison.Ordinal);
        Assert.Equal(
            "true",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
        Assert.DoesNotContain(MissingProofRisk, card.RiskFlags);
        Assert.DoesNotContain(
            "source_hash:evidence_bundle_document_revision",
            card.Lineage);
    }

    [Fact]
    public void Bug136_InvalidCardHashHydratesFromUniqueSameDocumentRevisionSha()
    {
        var results = new ToolResults();
        results.Items.Add(SearchResult(
            docId: "doc-alpha",
            revisionId: "revision-alpha",
            sourceHash: RevisionSha,
            page: 3,
            chunkId: "chunk-alpha"));
        results.Items.Add(ContentCardResultItem(new
        {
            contentCardId = "card-alpha",
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = InvalidCatalogHash,
            title = "Documented option",
            pageStart = 7,
            pageEnd = 7,
            evidence = new { sourceText = "The documented option is available." }
        }));

        var bundle = BuildBundle(results);
        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");

        Assert.Equal(RevisionSha, card.SourceHash);
        Assert.Contains(
            "source_hash:evidence_bundle_document_revision",
            card.Lineage);
    }

    [Theory]
    [InlineData("doc-alpha", "revision-other")]
    [InlineData("doc-other", "revision-alpha")]
    [InlineData("", "revision-alpha")]
    [InlineData("doc-alpha", "")]
    public void Bug136_HydrationRequiresExactCompleteDocumentRevisionPair(
        string cardDocId,
        string cardRevisionId)
    {
        var results = new ToolResults();
        results.Items.Add(SearchResult(
            docId: "doc-alpha",
            revisionId: "revision-alpha",
            sourceHash: RevisionSha,
            page: 3,
            chunkId: "chunk-alpha"));
        results.Items.Add(ContentCardResultItem(new
        {
            contentCardId = "card-nearby",
            docId = string.IsNullOrEmpty(cardDocId) ? null : cardDocId,
            revisionId = string.IsNullOrEmpty(cardRevisionId) ? null : cardRevisionId,
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = InvalidCatalogHash,
            title = "Nearby option",
            pageStart = 7,
            pageEnd = 7,
            evidence = new { sourceText = "A nearby option is documented." }
        }));

        var bundle = BuildBundle(results);
        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");

        Assert.Equal(InvalidCatalogHash, card.SourceHash);
        Assert.DoesNotContain(
            "source_hash:evidence_bundle_document_revision",
            card.Lineage);
    }

    [Fact]
    public void Bug136_ConflictingSameRevisionHashesAreNeverUsedForHydration()
    {
        var results = new ToolResults();
        results.Items.Add(SearchResult(
            docId: "doc-alpha",
            revisionId: "revision-alpha",
            sourceHash: RevisionSha,
            page: 3,
            chunkId: "chunk-alpha-1"));
        results.Items.Add(SearchResult(
            docId: "doc-alpha",
            revisionId: "revision-alpha",
            sourceHash: ConflictingRevisionSha,
            page: 4,
            chunkId: "chunk-alpha-2"));
        results.Items.Add(ContentCardResultItem(new
        {
            contentCardId = "card-conflict",
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = InvalidCatalogHash,
            title = "Conflicting identity option",
            pageStart = 7,
            pageEnd = 7,
            evidence = new { sourceText = "The option has a source excerpt." }
        }));

        var bundle = BuildBundle(results);
        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");

        Assert.Equal(InvalidCatalogHash, card.SourceHash);
        Assert.DoesNotContain(
            "source_hash:evidence_bundle_document_revision",
            card.Lineage);
    }

    [Fact]
    public void Bug136_BackendGroundedFlagCannotTurnATitleOnlyCardIntoProof()
    {
        var bundle = BuildBundle(ContentCardResult(new
        {
            contentCardId = "card-title-only",
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = RevisionSha,
            title = "A title is not evidence",
            pageStart = 7,
            pageEnd = 7,
            hasGroundedEvidence = true
        }));

        var card = Assert.Single(bundle.Items);
        Assert.Equal(
            "false",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
        Assert.Contains(MissingProofRisk, card.RiskFlags);
        Assert.False(IsMechanicallyCitable(card));
    }

    [Theory]
    [InlineData("facts")]
    [InlineData("quantityFacts")]
    public void Bug136_FactSourceTextIsEffectiveProof(string arrayName)
    {
        var evidence = new Dictionary<string, object?>
        {
            [arrayName] = new[]
            {
                new { sourceText = "Exact source fragment.", pageStart = 7, pageEnd = 7 },
                new { sourceText = "Exact source fragment.", pageStart = 7, pageEnd = 7 },
                new { sourceText = "Second source fragment.", pageStart = 7, pageEnd = 7 }
            }
        };
        var bundle = BuildBundle(ContentCardResult(new
        {
            contentCardId = "card-facts-" + arrayName,
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = RevisionSha,
            title = "Fact-backed option",
            pageStart = 7,
            pageEnd = 7,
            hasGroundedEvidence = false,
            evidence
        }));

        var card = Assert.Single(bundle.Items);
        Assert.Equal(
            "true",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
        Assert.DoesNotContain(MissingProofRisk, card.RiskFlags);
        Assert.Equal(1, CountOccurrences(card.Excerpt ?? string.Empty, "Exact source fragment."));
        Assert.True(IsMechanicallyCitable(card));
    }

    [Fact]
    public void Bug136_ProoflessEmbeddedCardIsNavigationOnlyWhileParentHitRemainsCitable()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                query = "documented option",
                hits = new[]
                {
                    new
                    {
                        docId = "doc-alpha",
                        revisionId = "revision-alpha",
                        docPath = "Generic/Alpha.pdf",
                        docName = "Alpha.pdf",
                        sourceHash = RevisionSha,
                        pageStart = 7,
                        pageEnd = 7,
                        chunkId = "chunk-alpha",
                        excerpt = "The parent passage contains documented facts.",
                        matchedContentCards = new[]
                        {
                            new
                            {
                                contentCardId = "card-embedded-title-only",
                                title = "Embedded title only",
                                pageStart = 7,
                                pageEnd = 7,
                                hasGroundedEvidence = true
                            }
                        }
                    }
                }
            })
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Find the documented option.",
            materializeMatchedContentCards: true);
        var parent = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "rag_hit");
        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");

        Assert.DoesNotContain(MissingProofRisk, parent.RiskFlags);
        Assert.True(IsMechanicallyCitable(parent));
        Assert.Contains(MissingProofRisk, card.RiskFlags);
        Assert.Equal(
            "false",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
        Assert.False(IsMechanicallyCitable(card));
    }

    [Fact]
    public void Bug136_ObservationExplicitlyMarksProoflessCardAsNonCitable()
    {
        var results = ContentCardResult(new
        {
            contentCardId = "card-observation",
            docId = "doc-alpha",
            revisionId = "revision-alpha",
            docPath = "Generic/Alpha.pdf",
            docName = "Alpha.pdf",
            sourceHash = RevisionSha,
            title = "Observation title only",
            pageStart = 7,
            pageEnd = 7,
            hasGroundedEvidence = true
        });
        var bundle = BuildBundle(results);
        var options = new SourceBackedAgentV2Options(
            MaximumTurns: 4,
            MaximumToolCalls: 4,
            MaximumObservationItems: 10,
            MaximumObservationExcerptCharacters: 180,
            MaximumOutputTokens: 512);

        using var observation = JsonDocument.Parse(
            SourceBackedAgentObservationCompactor.Build(
                "documents_content_cards",
                results.Items,
                bundle,
                firstToolSequence: 1,
                options));
        var item = observation.RootElement
            .GetProperty("evidence")[0];

        Assert.False(item.GetProperty("mechanicallyCitable").GetBoolean());
        Assert.True(item.GetProperty("navigationOnly").GetBoolean());
        Assert.Equal(
            "false",
            item.GetProperty("hasGroundedEvidence").GetString(),
            ignoreCase: true);
    }

    [Fact]
    public void Bug136_WorkingStateClassifiesProoflessCardsWithNavigationAnchors()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "client",
            "SAAIA.Client.WinUI",
            "ToolAgent",
            "SourceBackedRag",
            "SourceBackedAgentWorkingStatePrompt.cs"));

        Assert.Contains(MissingProofRisk, source, StringComparison.Ordinal);
        Assert.Contains(
            "ANCRES DE NAVIGATION NON CITABLES",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bug136_FinalVerifierRejectsProoflessCardEvenWithValidIdentity()
    {
        var card = CanonicalCardEvidenceItem(
            "E1",
            JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    contentCardId = "card-proofless",
                    title = "Title without source proof",
                    pageStart = 7,
                    pageEnd = 7,
                    hasGroundedEvidence = true
                }
            }));
        var bundle = new EvidenceBundle(
            "bundle-proofless-card",
            "Give the documented option.",
            new[] { card },
            Array.Empty<SourceBackedTraceEvent>());

        var verification = SourceContractVerifier.Verify(
            new WriterDraft("The documented option is available [E1].", new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);

        Assert.Contains(
            verification.Errors,
            static error => error.Code == MissingProofRisk);
        Assert.DoesNotContain(
            verification.Errors,
            static error => error.Code == "invalid_source_sha256");
    }

    [Fact]
    public void Bug136_FinalVerifierAcceptsGroundedCardIdentityAndKeepsShaStrict()
    {
        var grounded = CanonicalCardEvidenceItem(
            "E1",
            JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    contentCardId = "card-grounded",
                    title = "Grounded option",
                    pageStart = 7,
                    pageEnd = 7,
                    evidence = new { sourceText = "The grounded option is documented." }
                }
            }));
        var invalidSha = grounded with
        {
            EvidenceId = "E2",
            SourceHash = InvalidCatalogHash,
            ContentCardId = "card-grounded-invalid-sha"
        };
        var bundle = new EvidenceBundle(
            "bundle-grounded-card",
            "Give the documented option.",
            new[] { grounded, invalidSha },
            Array.Empty<SourceBackedTraceEvent>());

        var groundedVerification = SourceContractVerifier.Verify(
            new WriterDraft("The grounded option is documented [E1].", new[] { "E1" }),
            bundle,
            allowedEvidenceIds: new[] { "E1" },
            enforceRequestedShape: false);
        var invalidShaVerification = SourceContractVerifier.Verify(
            new WriterDraft("The grounded option is documented [E2].", new[] { "E2" }),
            bundle,
            allowedEvidenceIds: new[] { "E2" },
            enforceRequestedShape: false);

        Assert.DoesNotContain(
            groundedVerification.Errors,
            static error => error.Code == MissingProofRisk);
        Assert.DoesNotContain(
            groundedVerification.Errors,
            static error => error.Code == "invalid_source_sha256");
        Assert.Contains(
            invalidShaVerification.Errors,
            static error => error.Code == "invalid_source_sha256");
    }

    [Fact]
    public void Bug136_ProductChangeRemainsDomainNeutral()
    {
        var root = FindRepoRoot();
        var paths = new[]
        {
            Path.Combine(root, "backend", "SAAIA.Backend", "Endpoints", "DocumentsEndpoints.ContentCards.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "EvidenceBundleBuilder.ContentCardInventory.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "EvidenceBundleBuilder.AgentContentCards.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "SourceBackedAgentSemanticCandidateEligibility.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "SourceBackedAgentMechanicalEvidenceContract.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "SourceBackedAgentWorkingStatePrompt.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "SourceBackedAgentObservationCompactor.cs"),
            Path.Combine(root, "client", "SAAIA.Client.WinUI", "ToolAgent", "SourceBackedRag", "SourceContractVerifier.EvidenceIdentity.cs")
        };
        var product = string.Join("\n", paths.Select(File.ReadAllText));

        foreach (var forbidden in new[]
                 {
                     "ABB 266", "HART", "Easy Setup",
                     "Integrated LCD display available"
                 })
        {
            Assert.DoesNotContain(forbidden, product, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsMechanicallyCitable(EvidenceItem item)
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "IsMechanicallyCitableCandidate",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(SourceBackedAgentV2Runner).FullName,
                "IsMechanicallyCitableCandidate");
        return Assert.IsType<bool>(method.Invoke(null, new object[] { item }));
    }

    private static EvidenceItem CanonicalCardEvidenceItem(
        string evidenceId,
        JsonElement matchedCards)
        => new(
            EvidenceId: evidenceId,
            SourceKind: "canonical_content_card",
            ToolName: "documents.content_cards",
            QueryUsed: "documented option",
            DocId: "doc-alpha",
            DocName: "Alpha.pdf",
            DocPath: "Generic/Alpha.pdf",
            SourceHash: RevisionSha,
            RevisionId: "revision-alpha",
            PageStart: 7,
            PageEnd: 7,
            ChunkId: null,
            Excerpt: "Documented option.",
            NormalizedExcerpt: "documented option",
            Score: 0.8,
            Rank: 1,
            CategoryPath: "Generic",
            DocLanguage: "en",
            ProfileLanguage: "en",
            ExtractionQuality: "native_text",
            MatchedContentCards: matchedCards,
            SelectionHints: new Dictionary<string, string>
            {
                ["canonicalContentCard"] = "true"
            },
            CodeHints: new Dictionary<string, string>(),
            RiskFlags: Array.Empty<string>(),
            Lineage: Array.Empty<string>())
        {
            ContentCardId = evidenceId == "E1"
                ? matchedCards[0].GetProperty("contentCardId").GetString()
                : "card-grounded-invalid-sha"
        };

    private static ToolResults ContentCardResult(object card)
    {
        var results = new ToolResults();
        results.Items.Add(ContentCardResultItem(card));
        return results;
    }

    private static ToolResults.Item ContentCardResultItem(object card)
        => new()
        {
            ToolName = "documents.content_cards",
            Result = JsonSerializer.SerializeToElement(new
            {
                query = "documented option",
                citable = true,
                items = new[] { card }
            })
        };

    private static ToolResults.Item SearchResult(
        string docId,
        string revisionId,
        string sourceHash,
        int page,
        string chunkId)
        => new()
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                query = "documented option",
                hits = new[]
                {
                    new
                    {
                        docId,
                        revisionId,
                        docPath = "Generic/Alpha.pdf",
                        docName = "Alpha.pdf",
                        sourceHash,
                        pageStart = page,
                        pageEnd = page,
                        chunkId,
                        excerpt = "A canonical source passage documents the option.",
                        score = 0.9
                    }
                }
            })
        };

    private static EvidenceBundle BuildBundle(ToolResults results)
        => EvidenceBundleBuilder.FromToolResults(
            results,
            "Find the documented option.");

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "ADR-2026-07-08-rag-llm-orchestration-source-backed.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
