using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisSourceMaterialTests
{
    [Theory]
    [InlineData("Options\nOption alpha I page 65", "Options", true)]
    [InlineData("Options\nAn option is a documented choice.\nOption alpha I page 65", "Options", true)]
    [InlineData("Option alpha", "Option alpha", true)]
    [InlineData("Option alpha\n[Index: ]\nMCRC01072842_BO_identifier-013\nFor 4 units", "Option alpha", true)]
    [InlineData("Options\nOption alpha I page 65\nOption alpha uses a threshold of 17 bar.", "Options", false)]
    [InlineData("Option alpha uses a threshold of 17 bar.", "Option alpha", false)]
    [InlineData("INGRÉDIENTS\nOption alpha\n500 ml (2 tasses) de rhubarbe\n375 ml (1.5 tasses) de fraises\n15-30 ml (1-2 c. à soupe) de sucre\n5-8 feuilles de menthe fraiche, émincées", "Option alpha", false)]
    [InlineData("Option alpha\n125 g de yaourt\nPréchauffez le four à 180°C.\n2 pots de farine\n2 pots de sucre\n3 œufs\n11 g de levure\n1\n2 Mélangez les ingrédients.\n3 Enfournez pour 30 min.\n4 Servez.", "Option alpha", false)]
    [InlineData("Option alpha\n[Index: ]\nMCRC01072842_BO_identifier-013\nFor 4 units\n500 ml liquid\n125 g material\nHold for 3 min.", "Option alpha", false)]
    public void Selected_item_needs_body_beyond_identity_or_a_page_locator(string text, string title, bool expected)
        => Assert.Equal(expected, RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(text, title, "Option alpha"));

    [Fact]
    public void Grounded_card_text_uses_source_quotes_and_excludes_generated_values_and_retrieval_tags()
    {
        const string evidence = """{"facts":[{"kind":"canonical_structure","label":"section_level","value":"99"},{"kind":"claim","value":"The threshold is 999","sourceText":"The threshold is 17."},{"sourceText":"The threshold is 17."}],"quantityFacts":[{"value":999,"sourceText":"Maximum pressure: 17 bar."}]}""";
        var text = AdvancedAnalysisEvidenceResolver.BuildGroundedContentCardText("Pressure valve", evidence);
        Assert.Contains("The threshold is 17.", text, StringComparison.Ordinal);
        Assert.Contains("Maximum pressure: 17 bar.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical_structure", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("The threshold is 17.", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"facts\":[{\"value\":\"unsupported detail\"}]}")]
    [InlineData("{\"facts\":[null,17,{\"sourceText\":false}]}")]
    public void Card_without_source_quotes_remains_an_identity_locator(string evidence)
    {
        var text = AdvancedAnalysisEvidenceResolver.BuildGroundedContentCardText("Valve overview", evidence);
        Assert.Equal("Valve overview", text);
        var signal = RetrievalContentClassifier.AnalyzeEvidenceContent(text, "Valve overview");
        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.Equal("canonical_heading_only", signal.NavigationReason);
    }

    [Theory]
    [InlineData("Options\nOption alpha I page 65\nOption beta I page 66\nOption gamma I page 67", "navigation")]
    [InlineData("Option alpha pagina 65\nOption beta pagina 66", "navigation")]
    [InlineData("Options\r\nOption alpha I page 65\r\nOption beta I page 66", "navigation")]
    [InlineData("Options\nAn option is a documented choice.\nOption alpha I page 64", "mixed_navigation_content")]
    [InlineData("Option alpha\n[Index: ]\nMCRC01072842_BO_identifier-013\nFor 4 units", "navigation")]
    [InlineData("The revision index records the version. The threshold is 17 bar.", "content")]
    public void Evidence_annotation_distinguishes_short_locators_and_substantive_prose(string text, string expectedRole)
    {
        Assert.Equal(expectedRole, RetrievalContentClassifier.AnalyzeEvidenceContent(text).ContentRole);
    }
}
