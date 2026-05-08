using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalContentClassifierTests
{
    [Theory]
    [InlineData("Sommaire Installation 3 Configuration 8 Maintenance 12 Annexes 18")]
    [InlineData("Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18")]
    [InlineData("IndexA, BMotor startup 18Pressure calibration 22Valve inspection 24Weekly checklist 31Yearly shutdown 44Alarm acknowledgement 48Backup restore 52Control cabinet 57Drive replacement 64Emergency stop 72Filter exchange 81Hydraulic test 93Inspection checklist 104")]
    public void DetectNavigationReason_marks_toc_and_compact_indexes(string text)
    {
        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.NotNull(reason);
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
    }
}
