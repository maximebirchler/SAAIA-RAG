using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StructuredPageBodySignalTests
{
    [Theory]
    [InlineData("ISO 9001")]
    [InlineData("AX4820")]
    [InlineData("Controller ZX 47")]
    [InlineData("Archive retention schedule 207")]
    public void A_number_does_not_make_a_documentary_title_noise(string title)
        => Assert.False(Invoke("LooksLikeNoisyStructuredPlanningCandidateTitle", title));

    [Theory]
    [InlineData("124")]
    [InlineData("p.12–16")]
    [InlineData("Page 124")]
    [InlineData("120 mm")]
    [InlineData("≤ 2,5 %")]
    public void An_isolated_locator_or_measure_is_not_a_documentary_title(string fragment)
        => Assert.True(Invoke("LooksLikeNoisyStructuredPlanningCandidateTitle", fragment));

    [Theory]
    [InlineData("Calibrage du capteur\nTolérance : 12 mV après stabilisation.\nContrôle : relever le signal du capteur.")]
    [InlineData("Archive transfer. Retention: retain the signed record for 90 days. Destination: copy the sealed archive to approved storage.")]
    [InlineData("Custard. Base: combine 250 ml of milk with the eggs. Finish: stir the mixture over gentle heat.")]
    public void Populated_page_shape_does_not_depend_on_field_vocabulary(string source)
    {
        Assert.True(Invoke("PageEvidenceHasStructuredBodyProofCue", source));
        Assert.True(Invoke("PageEvidenceHasStructuredCardProofCue", source));
    }

    [Theory]
    [InlineData("Calibrage du capteur\nTolérance :\nContrôle :\n")]
    [InlineData("Table of contents\nTolerance: 12 mV after stabilization\nControl: record the sensor output")]
    [InlineData("Calibration guide\nVoltage ranges: page 21\nOutput verification: pages 43–45")]
    [InlineData("Calibration guide. Document title: Calibration worksheet. Summary: Verify the sensor output.")]
    [InlineData("Calibration guide. Overview: see page 21 for further details and limits.")]
    [InlineData("Calibration guide. Overview: see p.21 for details and § 4.1 for the limits.")]
    public void Heading_navigation_metadata_and_references_do_not_supply_body_shape(string source)
    {
        Assert.False(Invoke("PageEvidenceHasStructuredBodyProofCue", source));
        Assert.False(Invoke("PageEvidenceHasStructuredCardProofCue", source));
    }

    [Fact]
    public void Structured_shape_cannot_attach_an_unrelated_body_to_a_title()
    {
        const string wrongBody = "Fan servicing. Speed: verify 120 rpm after replacement. Isolation: open the main disconnect.";
        Assert.True(Invoke("PageEvidenceHasStructuredBodyProofCue", wrongBody));
        var card = Card("Seal replacement", wrongBody);
        Assert.False(Invoke("ContentCardHasSelfContainedStructuredPlanningProof", card, "seal replacement"));
    }

    [Fact]
    public void A_title_supported_by_plain_substantive_prose_keeps_the_other_proof_path()
    {
        const string body = "The seal replacement procedure requires technicians to inspect mounting surfaces before restoring the system.";
        Assert.False(Invoke("PageEvidenceHasStructuredBodyProofCue", body));
        Assert.True(Invoke("ContentCardHasSelfContainedStructuredPlanningProof", Card("Seal replacement", body), "seal replacement"));
    }

    private static object Card(string title, string body)
    {
        var cardType = typeof(ToolAgentOrchestrator).GetNestedType("RagHitContentCardSummary", BindingFlags.NonPublic)!;
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        {
            Title = title,
            PageStart = 12,
            PageEnd = 12,
            Evidence = new
            {
                QuantityFacts = Array.Empty<object>(),
                NonScalableReasons = Array.Empty<string>(),
                Facts = new[] { new { Kind = "body", Label = "Observed text", SourceText = body } }
            }
        }), cardType)!;
    }

    private static bool Invoke(string method, params object[] arguments)
        => Assert.IsType<bool>(typeof(ToolAgentOrchestrator)
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments));
}
