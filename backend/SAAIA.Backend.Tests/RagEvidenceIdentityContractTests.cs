using SAAIA.Backend.Models;
using System.Text.RegularExpressions;
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
        var literals = CompiledArchitectureContract.StringsForMethod(
            typeof(RetrievalResultProjector), "LoadRagDocumentSourceIdentitiesAsync");
        var sql = Assert.Single(literals, static text =>
            Regex.IsMatch(text, @"(?i)\bJOIN\s+document_revisions\b"));
        Assert.Matches(@"(?i)\br\.revision_id\s*::\s*text\s+AS\s+""RevisionId""", sql);
        Assert.Matches(@"(?i)encode\s*\(\s*r\.source_hash\s*,\s*'hex'\s*\)", sql);
        Assert.Matches(@"(?i)r\.indexed_version\s*=\s*d\.indexed_version", sql);
        Assert.DoesNotContain("saaia_document_summary_source_hash", sql, StringComparison.OrdinalIgnoreCase);
    }
}
