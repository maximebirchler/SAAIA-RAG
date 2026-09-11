using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagEvidenceIdentityContractTests
{
    [Fact]
    public void Rag_item_exposes_the_exact_indexed_revision_identity()
    {
        Assert.NotNull(typeof(RagItemDto).GetProperty("RevisionId"));
    }

    [Fact]
    public void Rag_source_identity_is_loaded_from_the_active_document_revision()
    {
        var source = File.ReadAllText(ResolveBackendSourceFile(
            "Endpoints",
            "RagEndpoints.cs"));

        Assert.Contains("JOIN document_revisions", source, StringComparison.Ordinal);
        Assert.Contains("r.revision_id", source, StringComparison.Ordinal);
        Assert.Contains("encode(r.source_hash, 'hex')", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS \"SourceHash\"",
            source,
            StringComparison.Ordinal);
    }

    private static string ResolveBackendSourceFile(params string[] segments)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(
                new[] { current, "backend", "SAAIA.Backend" }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
