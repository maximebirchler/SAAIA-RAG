using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalEvaluationQuestionBankTests
{
    [Fact]
    public void Retrieval_eval_corpus_v5_declares_expected_version_and_scale()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();

        Assert.Equal("v5", corpus.Version);
        Assert.True(corpus.QuestionCases.Count >= 50, "v5 should be a large customer-style question bank.");
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_covers_both_real_documents_and_cross_doc_cases()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();

        var cenCases = corpus.QuestionCases.Count(static q => q.SourceDocHints.Contains("15281", StringComparer.Ordinal));
        var indCases = corpus.QuestionCases.Count(static q => q.SourceDocHints.Contains("IND570", StringComparer.Ordinal));
        var crossCases = corpus.QuestionCases.Count(static q =>
            q.SourceDocHints.Contains("15281", StringComparer.Ordinal) &&
            q.SourceDocHints.Contains("IND570", StringComparer.Ordinal));

        Assert.True(cenCases >= 20, "v5 should cover the inerting guidance deeply.");
        Assert.True(indCases >= 20, "v5 should cover the IND570 manual deeply.");
        Assert.True(crossCases >= 5, "v5 should include cross-document customer situations.");
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_covers_broad_customer_intents_and_behaviors()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();

        var intents = corpus.QuestionCases
            .Select(static q => q.Intent)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var behaviors = corpus.QuestionCases
            .Select(static q => q.ExpectedBehavior)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("compliance_assessment", intents);
        Assert.Contains("followup_required", intents);
        Assert.Contains("comparison", intents);
        Assert.Contains("document_locate", intents);
        Assert.Contains("integration", intents);
        Assert.Contains("safety", intents);
        Assert.Contains("configuration", intents);
        Assert.Contains("troubleshooting", intents);

        Assert.Contains("answer", behaviors);
        Assert.Contains("answer_with_caveat", behaviors);
        Assert.Contains("ask_clarification", behaviors);
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_keeps_customer_style_and_ambiguity_cases()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();

        var customerStyleCases = corpus.QuestionCases.Count(static q =>
            q.Query.Contains("client", StringComparison.OrdinalIgnoreCase) ||
            q.Query.Contains("projet", StringComparison.OrdinalIgnoreCase));
        var clarifyCases = corpus.QuestionCases.Count(static q => string.Equals(q.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal));
        var cautiousCases = corpus.QuestionCases.Count(static q => string.Equals(q.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal));

        Assert.True(customerStyleCases >= 15, "v5 should feel like real customer/project questions.");
        Assert.True(clarifyCases >= 3, "v5 should include genuinely ambiguous questions.");
        Assert.True(cautiousCases >= 10, "v5 should include cases where the assistant must stay qualified.");
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_cases_are_well_formed()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();

        Assert.All(corpus.QuestionCases, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.Name));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Query));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Intent));
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedBehavior));
            Assert.NotEmpty(testCase.SourceDocHints);
            Assert.NotEmpty(testCase.ExpectedDocHints);
        });
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_semi_real_guidance_matches_expected_behavior()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();
        var failures = new List<string>();

        foreach (var testCase in corpus.QuestionCases)
        {
            var syntheticMatches = BuildSyntheticMatches(testCase.SourceDocHints);
            var guidance = RagEndpoints.BuildAnswerGuidance(testCase.Query, syntheticMatches);

            if (!string.Equals(testCase.ExpectedBehavior, guidance.Behavior, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected '{testCase.ExpectedBehavior}' but got '{guidance.Behavior}' for query '{testCase.Query}'.");
                continue;
            }

            Assert.NotEmpty(guidance.MatchedDocHints ?? Array.Empty<string>());

            foreach (var expectedHint in testCase.ExpectedDocHints)
                Assert.Contains(expectedHint, guidance.MatchedDocHints!, StringComparer.Ordinal);

            if (string.Equals(testCase.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal))
                Assert.False(string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion));

            if (string.Equals(testCase.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal))
                Assert.False(string.IsNullOrWhiteSpace(guidance.QualificationNote));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_includes_runtime_ready_subset_with_doc_dominance_expectations()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();
        var runtimeReadyCases = corpus.QuestionCases.Where(static q => q.RuntimeReady).ToArray();

        Assert.True(runtimeReadyCases.Length >= 10, "v5 should expose a stable runtime-ready subset.");
        Assert.All(runtimeReadyCases, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedPrimaryDocHint));
            Assert.Contains(testCase.ExpectedPrimaryDocHint!, testCase.ExpectedDocHints, StringComparer.Ordinal);
        });
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_runtime_ready_writer_cases_cover_prudent_and_document_selection_flows()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();
        var runtimeReadyCases = corpus.QuestionCases.Where(static q => q.RuntimeReady).ToArray();

        Assert.Contains(runtimeReadyCases, static q => string.Equals(q.Name, "cross_draft_cautious_customer_reply", StringComparison.Ordinal));
        Assert.Contains(runtimeReadyCases, static q => string.Equals(q.Name, "cross_which_passage_to_open_first", StringComparison.Ordinal));
        Assert.Contains(runtimeReadyCases, static q => string.Equals(q.Name, "cross_which_doc_for_what", StringComparison.Ordinal));
        Assert.Contains(runtimeReadyCases, static q => string.Equals(q.Name, "client_what_to_ask_before_yes", StringComparison.Ordinal));
    }

    [Fact]
    public void Retrieval_eval_corpus_v5_runtime_ready_cases_define_response_shapes_for_writer()
    {
        var corpus = RetrievalQuestionBankFixture.LoadV5();
        var runtimeReadyCases = corpus.QuestionCases.Where(static q => q.RuntimeReady).ToArray();

        Assert.All(runtimeReadyCases, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedResponseShape));
        });

        var shapes = runtimeReadyCases
            .Select(static q => q.ExpectedResponseShape)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("summary", shapes);
        Assert.Contains("comparison", shapes);
        Assert.Contains("locate_document", shapes);
        Assert.Contains("locate_passage", shapes);
        Assert.Contains("qualified_answer", shapes);
        Assert.Contains("clarify", shapes);
    }

    private static IReadOnlyList<RagMatch> BuildSyntheticMatches(IReadOnlyList<string> docHints)
    {
        var matches = new List<RagMatch>();

        foreach (var hint in docHints.Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(hint, "15281", StringComparison.Ordinal))
            {
                matches.Add(new RagMatch(
                    Score: 0.98,
                    DocId: "doc-cen-15281",
                    DocPath: "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
                    DocName: "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
                    PageStart: 1,
                    PageEnd: 1,
                    ChunkId: "docmeta-15281",
                    ChunkIndex: -1,
                    Text: "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf [en 15281]",
                    IngestionVersion: 1,
                    HashDoc: "hash-15281",
                    EmbedText: "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf [en 15281]",
                    EmbeddingBasis: "exact_match_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: null,
                    HeadingPath: null,
                    ChunkType: "document_metadata_ref",
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null));
            }
            else if (string.Equals(hint, "IND570", StringComparison.Ordinal))
            {
                matches.Add(new RagMatch(
                    Score: 0.98,
                    DocId: "doc-ind570",
                    DocPath: "Programmation/Mettler/MettlerToledo_IND570.pdf",
                    DocName: "MettlerToledo_IND570.pdf",
                    PageStart: 1,
                    PageEnd: 1,
                    ChunkId: "docmeta-ind570",
                    ChunkIndex: -1,
                    Text: "MettlerToledo_IND570.pdf [ind570]",
                    IngestionVersion: 1,
                    HashDoc: "hash-ind570",
                    EmbedText: "MettlerToledo_IND570.pdf [ind570]",
                    EmbeddingBasis: "exact_match_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: null,
                    HeadingPath: null,
                    ChunkType: "document_metadata_ref",
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null));
            }
            else if (string.Equals(hint, "ACCORD", StringComparison.Ordinal))
            {
                matches.Add(new RagMatch(
                    Score: 0.75,
                    DocId: "doc-accord",
                    DocPath: "General/Accord sur le transfert du code source.pdf",
                    DocName: "Accord sur le transfert du code source.pdf",
                    PageStart: 1,
                    PageEnd: 1,
                    ChunkId: "docmeta-accord",
                    ChunkIndex: -1,
                    Text: "Accord sur le transfert du code source.pdf",
                    IngestionVersion: 1,
                    HashDoc: "hash-accord",
                    EmbedText: "Accord sur le transfert du code source.pdf",
                    EmbeddingBasis: "contextual_text_v1",
                    SectionOrdinal: 1,
                    UnitOrdinal: 1,
                    SectionTitle: "General",
                    HeadingPath: "General",
                    ChunkType: "unit_exact_v1",
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null));
            }
        }

        return matches;
    }
}
