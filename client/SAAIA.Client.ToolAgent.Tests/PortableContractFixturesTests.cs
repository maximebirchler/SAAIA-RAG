using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class PortableContractFixturesTests
{
    [Theory]
    [InlineData("A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json", ThreeFormatQwenContractHarness.ContractHash)]
    [InlineData("A630-CASES-NORMALISES-ORACLES.json", ThreeFormatQwenContractHarness.CasesHash)]
    [InlineData("A656-ORDRE-MICROCAMPAGNE-25-CAS.json", ThreeFormatQwenContractHarness.OrderHash)]
    [InlineData("A645-CONTRATS-COMPACTS-CANDIDATS.json", "BD6ECE287F3AF3F4448F187604725D461097122DA0BE1AD762041DEE01104DA0")]
    [InlineData("MultiturnRecoveryMechanicalReplay.v1.json", PortableContractFixtures.MultiturnReplaySha256)]
    public void Published_unit_fixture_is_present_with_its_required_byte_identity(string name, string expectedSha256)
    {
        var path = PortableContractFixtures.PathFor(name);
        Assert.StartsWith(AppContext.BaseDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public void Case_manifest_contains_only_synthetic_document_identities()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            PortableContractFixtures.PathFor("A630-CASES-NORMALISES-ORACLES.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.StartsWith("Synthetic/A630/", item.GetProperty("docPath").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("a630-", item.GetProperty("docId").GetString(), StringComparison.Ordinal);
            Assert.Equal("a630-v1", item.GetProperty("revisionId").GetString());
        }
    }

    [Fact]
    public void Mechanical_replay_declares_its_limited_provenance_and_excludes_private_runtime_metadata()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            PortableContractFixtures.PathFor("MultiturnRecoveryMechanicalReplay.v1.json")));
        var root = document.RootElement;
        Assert.Equal("saaia-portable-mechanical-replay.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("portable_mechanical_replay", root.GetProperty("fixtureKind").GetString());
        Assert.Equal("254D9CD1DC15E522E10DEB42F953DDC2FC5137148757C6F6BF2C9345FC866708",
            root.GetProperty("historicalSourceSha256").GetString());
        Assert.False(root.GetProperty("containsModelPerformanceEvidence").GetBoolean());
        Assert.Equal(new[] { "containsModelPerformanceEvidence", "fixtureKind", "historicalSourceSha256",
                "observations", "schemaVersion", "systemPrompt", "toolNames" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var observation in root.GetProperty("observations").EnumerateArray())
            Assert.Equal(new[] { "ArgumentsRaw", "Question", "ScenarioId", "ToolName" },
                observation.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
