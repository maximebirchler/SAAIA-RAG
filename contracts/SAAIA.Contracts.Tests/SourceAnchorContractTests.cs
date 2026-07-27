using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class SourceAnchorContractTests
{
    [Fact]
    public void Anchor_reconstructs_exact_evidence_from_page_and_span()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var anchor = CanonicalContractFixture.CreateAnchor(document);

        var evidence = Assert.Single(CanonicalEvidenceResolver.Resolve(anchor, document));

        Assert.Equal(1, evidence.PageNumber);
        Assert.Equal("Petit-déjeuner", evidence.RawText);
        Assert.Equal("Petit-déjeuner", evidence.DisplayText);
    }

    [Fact]
    public void Anchor_rejects_tampered_raw_text_hash()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var anchor = CanonicalContractFixture.CreateAnchor(document);
        anchor.Regions[0].RawTextSha256 = new string('0', 64);

        var issues = CanonicalContractValidator.Validate(anchor, document);

        Assert.Contains(issues, issue => issue.Code == "hash_mismatch");
    }
}
