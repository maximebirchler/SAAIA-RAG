using System.Collections;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalAnchorBatchApiTests
{
    [Fact]
    public void Empty_anchor_list_is_allowed_only_for_a_valid_document()
    {
        var document = CanonicalContractFixture.CreateDocument();
        CanonicalContractValidator.ValidateAnchorsOrThrow(document, []);
        document.SchemaVersion = "invalid";
        Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, []));
    }

    [Fact]
    public void Invalid_first_anchor_fails_before_a_null_later_anchor()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var anchor = CanonicalContractFixture.CreateAnchor(document); anchor.SourceSha256 = new string('c', 64);
        var expected = Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateOrThrow(anchor, document));
        var actual = Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, [anchor, null!]));
        Assert.Equal(expected.Issues, actual.Issues);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("manifest")]
    [InlineData("page")]
    [InlineData("reference")]
    [InlineData("hash")]
    public void Batch_returns_the_same_first_anchor_issues_as_single_validation(string mutation)
    {
        var document = CanonicalContractFixture.CreateDocument(); var anchor = CanonicalContractFixture.CreateAnchor(document);
        switch (mutation)
        {
            case "source": anchor.SourceSha256 = new string('c', 64); break;
            case "manifest": anchor.ManifestSha256 = new string('c', 64); break;
            case "page": anchor.Regions[0].PageNumber = 999; break;
            case "reference": anchor.Regions[0].SpanIds = ["missing-span"]; break;
            case "hash": anchor.Regions[0].RawTextSha256 = new string('c', 64); break;
        }
        var expected = Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateOrThrow(anchor, document));
        var actual = Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, [anchor]));
        Assert.Equal(expected.Issues, actual.Issues);
    }

    [Fact]
    public void Caller_list_is_materialized_before_document_validation()
    {
        var document = CanonicalContractFixture.CreateDocument(); var anchor = CanonicalContractFixture.CreateAnchor(document);
        var actual = Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(
            document, new MutatingList(document, anchor)));
        Assert.Contains(actual.Issues, issue => issue.Code == "schema_version");
    }

    [Fact]
    public void Each_call_revalidates_the_document_instead_of_reusing_a_stale_index()
    {
        var document = CanonicalContractFixture.CreateDocument(); var anchor = CanonicalContractFixture.CreateAnchor(document);
        CanonicalContractValidator.ValidateAnchorsOrThrow(document, [anchor]);
        document.Pages[0].Blocks[0].Spans[0].Text.Raw = "mutated without updating hash";
        Assert.Throws<CanonicalContractException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, [anchor]));
    }

    [Fact]
    public void Null_inputs_are_explicitly_rejected()
    {
        var document = CanonicalContractFixture.CreateDocument();
        Assert.Throws<ArgumentNullException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(null!, []));
        Assert.Throws<ArgumentNullException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, null!));
        Assert.Throws<ArgumentNullException>(() => CanonicalContractValidator.ValidateAnchorsOrThrow(document, [null!]));
    }

    private sealed class MutatingList(CanonicalDocument document, SourceAnchor anchor) : IReadOnlyList<SourceAnchor>
    {
        public int Count => 1;
        public SourceAnchor this[int index] => index == 0 ? anchor : throw new IndexOutOfRangeException();
        public IEnumerator<SourceAnchor> GetEnumerator() { document.SchemaVersion = "invalidated_by_list"; yield return anchor; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
