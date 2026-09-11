using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class CanonicalPdfCitationContractTests
{
    [Fact]
    public void Extracted_pdf_response_keeps_exact_identity_through_verification_and_ui_source_payload()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "CanonicalPdfCitationContract.json")));
        var root = fixture.RootElement;
        Assert.True(root.GetProperty("syntheticCorpus").GetBoolean());
        Assert.True(root.GetProperty("postgresVerified").GetBoolean());
        var fileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixtures, "Calibration-record.pdf"))))
            .ToLowerInvariant();
        Assert.Equal(fileHash, root.GetProperty("sourceHash").GetString());
        var response = root.GetProperty("response");
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(response.GetRawText(), sourceBackedCanonical: true);
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        const string question = "What measurements are recorded for the amber and cobalt instruments?";
        var bundle = EvidenceBundleBuilder.FromToolResults(tools, question);
        Assert.Equal(2, bundle.Items.Count);
        var originals = response.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("chunkId").GetString()!);
        var pages = root.GetProperty("pages").EnumerateArray().ToDictionary(page => page.GetProperty("PageNumber").GetInt32());
        Assert.All(bundle.Items, item =>
        {
            Assert.Equal(fileHash, item.SourceHash);
            Assert.Equal(root.GetProperty("revisionId").GetString(), item.RevisionId);
            Assert.Equal(root.GetProperty("docId").GetString(), item.DocId);
            Assert.Equal(root.GetProperty("docPath").GetString(), item.DocPath);
            Assert.Equal(originals[item.ChunkId!].GetProperty("text").GetString(), item.Excerpt);
            Assert.Equal(item.PageStart, item.PageEnd);
            Assert.Contains(Assert.IsType<string>(item.Excerpt), pages[item.PageStart!.Value].GetProperty("Text").GetString());
            Assert.Equal(response.GetProperty("query").GetString(), item.QueryUsed);
        });

        // The writer and selection are scripted literal quotations. This tests
        // file/citation transport, not Qwen's selection, wording or sufficiency.
        var selectedIds = bundle.Items.Select(item => item.EvidenceId).ToArray();
        var draft = new WriterDraft(string.Join("\n\n", bundle.Items.Select(item => $"{item.Excerpt} [{item.EvidenceId}]")), selectedIds);
        var intake = new SourceBackedIntake(question, "answer", [], [], false, "en");
        var verification = SourceContractVerifier.Verify(draft, bundle, intake, selectedIds);
        Assert.True(verification.IsValid, string.Join("; ", verification.Errors.Select(error => $"{error.Code}: {error.Message}")));
        Assert.Equal(2, verification.CitedEvidence.Count);
        var result = new SourceBackedPipelineResult("pdf-contract", intake, null, bundle,
            new EvidenceJudgeDecision("write", selectedIds, [], [], []), draft, draft, verification, false, []);
        var uiPayload = SourceBackedUiPayloadMapper.FromVerifiedResult(result);
        Assert.Equal(draft.Answer, uiPayload.Answer);

        // These are the production source transformations performed by the
        // verified terminal. No WinUI window or model is launched by this test.
        var normalizeSources = typeof(ToolAgentOrchestrator).GetMethod("NormalizeVisibleSourceRefsForMemory",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(normalizeSources);
        var sourceRefs = Assert.IsType<List<ToolMemory.SourceRef>>(normalizeSources.Invoke(null, [uiPayload.Sources.ToList()]));
        var buildPayload = typeof(ToolAgentOrchestrator).GetMethod("BuildSourcesPayload",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(string), typeof(List<ToolMemory.SourceRef>), typeof(SourceBackedConversationTurnMemory)], null);
        Assert.NotNull(buildPayload);
        var cards = SourceCardParser.Parse(JsonSerializer.Serialize(buildPayload.Invoke(null, ["answer", sourceRefs, null])));
        Assert.Equal(2, cards.Count);
        Assert.All(cards, card =>
        {
            var evidence = Assert.Single(verification.CitedEvidence, item => item.EvidenceId == card.EvidenceId);
            Assert.Equal(evidence.DocId, card.DocId);
            Assert.Equal(evidence.DocPath, card.DocPath);
            Assert.Equal(evidence.DocName, card.DocName);
            Assert.Equal(fileHash, card.SourceHash);
            Assert.Equal(evidence.RevisionId, card.RevisionId);
            Assert.Equal(evidence.PageStart, card.PageStart);
            Assert.Equal(evidence.PageEnd, card.PageEnd);
            Assert.Equal(evidence.ChunkId, card.ChunkId);
            Assert.Equal(evidence.AnchorId, card.AnchorId);
        });

        var ownedRoot = Path.Combine(Path.GetTempPath(), $"saaia-client-pdf-contract-{Guid.NewGuid():N}");
        var categoryDirectory = Path.Combine(ownedRoot, "Knowledge");
        var localPdf = Path.Combine(categoryDirectory, "Calibration-record.pdf");
        var previousRoot = Environment.GetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT");
        try
        {
            Directory.CreateDirectory(categoryDirectory);
            File.Copy(Path.Combine(fixtures, "Calibration-record.pdf"), localPdf);
            Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", ownedRoot);
            Assert.All(cards, card =>
            {
                var resolved = DocumentPathResolver.ResolveExactRevision(card.DocPath, card.SourceHash);
                Assert.Equal(localPdf, resolved);
                var pageUri = new Uri(DocumentLauncher.BuildPdfPageUriForTests(resolved!, card.PageStart!.Value));
                Assert.Equal(localPdf, pageUri.LocalPath);
                Assert.Equal("#page=" + card.PageStart.Value, pageUri.Fragment);
            });
            File.AppendAllText(localPdf, "\n% different local revision\n");
            Assert.All(cards, card => Assert.Null(DocumentPathResolver.ResolveExactRevision(card.DocPath, card.SourceHash)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", previousRoot);
            if (File.Exists(localPdf))
                File.Delete(localPdf);
            if (Directory.Exists(categoryDirectory))
                Directory.Delete(categoryDirectory, recursive: false);
            if (Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: false);
        }
    }
}
