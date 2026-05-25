using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalContentClassifierTests
{
    [Theory]
    [InlineData("Sommaire Installation 3 Configuration 8 Maintenance 12 Annexes 18")]
    [InlineData("Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18")]
    [InlineData("Índice Seguridad 3 Procedimiento de bloqueo 8 Mantenimiento 12 Anexos 18")]
    [InlineData("Sumário Segurança 3 Procedimento de bloqueio 8 Manutenção 12 Anexos 18")]
    [InlineData("Sommario Sicurezza 3 Procedura di blocco 8 Manutenzione 12 Allegati 18")]
    [InlineData("Inhaltsverzeichnis Sicherheit 3 Verriegelungsverfahren 8 Wartung 12 Anhänge 18")]
    [InlineData("IndexA, BMotor startup 18Pressure calibration 22Valve inspection 24Weekly checklist 31Yearly shutdown 44Alarm acknowledgement 48Backup restore 52Control cabinet 57Drive replacement 64Emergency stop 72Filter exchange 81Hydraulic test 93Inspection checklist 104")]
    public void DetectNavigationReason_marks_toc_and_compact_indexes(string text)
    {
        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.NotNull(reason);
    }

    [Fact]
    public void DetectNavigationReason_does_not_treat_content_indice_as_navigation()
    {
        var text = "Indice de viscosité: la méthode explique comment mesurer la variation du fluide, comparer les seuils et consigner le résultat.";

        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.Null(reason);
    }

    [Fact]
    public void AnalyzeChunk_scores_dot_leader_toc_as_navigation()
    {
        var text = """
Safety overview .................... 3
Lockout procedure .................. 8
Alarm reset ........................ 12
Maintenance plan ................... 18
Appendix ........................... 24
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.True(signal.NavigationScore >= 0.80);
        Assert.NotNull(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_marks_title_catalog_with_real_body_as_mixed()
    {
        var text = """
Controls overview 3
Maintenance plan 18
Alarm reset 22
Lockout checklist 27
Appendix 31
Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, signal.ContentRole);
        Assert.True(signal.NavigationScore >= 0.55);
        Assert.True(signal.ContentDensityScore >= 0.50);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_measured_instructional_content_as_content()
    {
        var text = """
Batch preparation for 4 units • 1.2 kg base compound • 250 g additive • 100 g binder • 2 modules • 3 cm spacer • 25 min curing time • 180° C oven.
Preparation: warm the base, mix the additive, place the spacer, check the control value and document the result before packaging.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_ocr_joined_instructional_content_as_content()
    {
        var text = """
Preparation : 20 minutes Control : 180° C For 4 units Preparation e 1kgde base e 250gadditive e 25clsolution.
Procedure e Warm the base and verify the control point. e Add the solution slowly. e Document the result and package the batch.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_structured_quantity_block_as_content_despite_inline_numbers()
    {
        var text = """
P PREPARATION 1 INGREDIENTS: 400 g base compound 2 modules 100 ml solution 25 min curing time 3 cm spacer 5 s hold 180 C control temperature.
PREPARATION 1. Rinse the modules and dry them. 2. Mix the base compound with the solution. 3. Heat the batch, verify the control value and document the result.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void AnalyzeChunk_keeps_measured_sequential_body_as_content_despite_inline_numbers()
    {
        var text = """
ALPHA BETA MODULE
1 Mix the base with 150 g powder and 20 g binder for 12 min until the control value is stable.
2 Heat the carrier to 85 C for 12 min and record the pressure value before continuing.
3 Cut the inserts into equal pieces, add 50 cl carrier and keep the assembly moving for 20 s.
4 Place each insert in the fixture and hold it for 25 min while the surface cools.
5 Finish the assembly with 18 cl solution, verify the result and document the batch.
4 units 23 min 12 min 25 min.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void DetectNavigationReason_keeps_structured_content_with_pdf_index_artifact()
    {
        var text = "16 Maintenance lockout [Index: ] ASSET-042 Materials padlock warning tag. Procedure 1. Isolate machine. 2. Verify zero energy.";

        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.Null(reason);
    }

    [Fact]
    public void ClassifyChunk_preserves_original_type_for_navigation_chunks()
    {
        var classification = RetrievalContentClassifier.ClassifyChunk(
            "Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18",
            "unit_exact_v1");

        Assert.Equal(RetrievalContentClassifier.NavigationRole, classification.ContentRole);
        Assert.Equal(RetrievalContentClassifier.NavigationChunkType, classification.ChunkType);
        Assert.Equal("unit_exact_v1", classification.OriginalChunkType);
        Assert.True(classification.NavigationScore >= 0.70);
    }
}
