using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Theory]
    [InlineData("""{"query":"recette","docId":"D1","docPath":"Cuisine/recettes.pdf"}""", null, "Cuisine/recettes.pdf")]
    [InlineData("""{"query":"recette","docId":"Cuisine/recettes.pdf"}""", null, "Cuisine/recettes.pdf")]
    [InlineData("""{"query":"recette","docId":"doc-123"}""", "doc-123", null)]
    public void ToolArgumentNormalizer_SeparatesOpaqueDocumentIdsFromPathsAndReadOnlyAliases(
        string json,
        string? expectedDocId,
        string? expectedDocPath)
    {
        using var input = JsonDocument.Parse(json);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(input.RootElement);

        Assert.Equal("recette", normalized.GetProperty("query").GetString());
        Assert.Equal(
            expectedDocId,
            normalized.TryGetProperty("docId", out var docId)
                ? docId.GetString()
                : null);
        Assert.Equal(
            expectedDocPath,
            normalized.TryGetProperty("docPath", out var docPath)
                ? docPath.GetString()
                : null);
    }

    [Theory]
    [InlineData("""{"docRef":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    [InlineData("""{"docPath":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    [InlineData("""{"docId":"FIT-PTFE_TF_1620-EN.pdf"}""")]
    public void ToolArgumentNormalizer_PreservesBareFilenameAsResolvableReference(
        string json)
    {
        using var input = JsonDocument.Parse(json);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement);

        Assert.Equal(
            "FIT-PTFE_TF_1620-EN.pdf",
            normalized.GetProperty("docRef").GetString());
        Assert.False(normalized.TryGetProperty("docPath", out _));
        Assert.False(normalized.TryGetProperty("docId", out _));
    }

    [Fact]
    public void ToolArgumentNormalizer_ResolvesPromptAliasesThroughCanonicalEvidence()
    {
        var item = new EvidenceItem(
            "E5",
            "rag_hit",
            "rag.search",
            "PTFE",
            "backend-doc-42",
            "FIT-PTFE_TF_1620-EN.pdf",
            "Documents techniques/FIT-PTFE_TF_1620-EN.pdf",
            null,
            null,
            1,
            1,
            "backend-chunk-9",
            "Product Form and Packaging",
            "product form and packaging",
            0.9,
            1,
            "Documents techniques",
            "en",
            "en",
            "high",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-1",
            "question",
            new[] { item },
            Array.Empty<SourceBackedTraceEvent>());
        using var input = JsonDocument.Parse(
            """{"docRef":"D2","docId":"E5","chunkId":"chunk-3","pageStart":9,"pageEnd":9}""");

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement,
                bundle);

        Assert.False(normalized.TryGetProperty("docRef", out _));
        Assert.Equal(
            "backend-doc-42",
            normalized.GetProperty("docId").GetString());
        Assert.Equal(
            "Documents techniques/FIT-PTFE_TF_1620-EN.pdf",
            normalized.GetProperty("docPath").GetString());
        Assert.Equal(
            "backend-chunk-9",
            normalized.GetProperty("chunkId").GetString());
        Assert.Equal(1, normalized.GetProperty("pageStart").GetInt32());
        Assert.Equal(1, normalized.GetProperty("pageEnd").GetInt32());
    }

    [Fact]
    public void ToolArgumentNormalizer_PreservesConcreteDocumentPathOverConflictingEvidenceAlias()
    {
        var wrongEvidence = new EvidenceItem(
            "E5",
            "rag_hit",
            "rag.search",
            "PTFE certificate",
            "wrong-doc",
            "certificate.pdf",
            "Technical/Certificates/certificate.pdf",
            null,
            null,
            1,
            1,
            "wrong-chunk",
            null,
            null,
            0.9,
            1,
            "Technical/Certificates",
            "en",
            "en",
            "high",
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var bundle = new EvidenceBundle(
            "bundle-1",
            "question",
            new[] { wrongEvidence },
            Array.Empty<SourceBackedTraceEvent>());
        using var input = JsonDocument.Parse(
            """
            {
              "docRef":"Technical/Data sheets/FIT.pdf",
              "docId":"E5",
              "chunkId":"wrong-chunk",
              "pageStart":1,
              "pageEnd":1
            }
            """);

        var normalized =
            SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                input.RootElement,
                bundle);

        Assert.Equal(
            "Technical/Data sheets/FIT.pdf",
            normalized.GetProperty("docPath").GetString());
        Assert.Equal(
            "Technical/Data sheets/FIT.pdf",
            normalized.GetProperty("docRef").GetString());
        Assert.False(normalized.TryGetProperty("docId", out _));
        Assert.False(normalized.TryGetProperty("chunkId", out _));
        Assert.Equal(1, normalized.GetProperty("pageStart").GetInt32());
    }
}
