using System.Globalization;
using System.Security.Cryptography;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeImplicitFillRectangleTests(ITestOutputHelper output)
{
    [Fact]
    public void Fill_implicitly_closes_horizontal_rectangle_without_mutating_source_commands()
    {
        var result = Observe("10 50 m 110 50 l 110 51 l 10 51 l f");
        Assert.Equal("observed", result.Availability);
        Assert.Null(result.Reason);
        AssertRule(Assert.Single(result.Rules), "h", 749.5, 10, 110, 1);
    }

    [Fact]
    public void Fill_implicitly_closes_vertical_rectangle_without_mutating_source_commands()
    {
        var result = Observe("50 10 m 51 10 l 51 110 l 50 110 l f");
        Assert.Equal("observed", result.Availability);
        Assert.Null(result.Reason);
        AssertRule(Assert.Single(result.Rules), "v", 50.5, 690, 790, 1);
    }

    [Fact]
    public void Wide_implicit_fill_rectangle_is_known_geometry_without_becoming_a_rule()
    {
        var result = Observe("10 50 m 110 50 l 110 70 l 10 70 l f");
        Assert.Equal("observed", result.Availability);
        Assert.Null(result.Reason);
        Assert.Empty(result.Rules);
        Assert.Empty(result.Diagnostics!.RejectionCounts!);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("B")]
    public void Open_stroke_keeps_three_original_edges_and_never_adds_a_closing_stroke(string paintingOperator)
    {
        var result = Observe($"1 w 10 50 m 110 50 l 110 70 l 10 70 l {paintingOperator}");
        Assert.Equal("observed", result.Availability);
        Assert.Equal(3, result.Rules.Count);
        AssertRule(result.Rules[0], "h", 750, 10, 110, 1);
        AssertRule(result.Rules[1], "v", 110, 730, 750, 1);
        AssertRule(result.Rules[2], "h", 730, 10, 110, 1);
        Assert.DoesNotContain(result.Rules, rule => rule.Axis == "v" && rule.Coordinate == 10);
    }

    [Fact]
    public void Thin_open_fill_and_stroke_keeps_legacy_strokes_instead_of_replacing_them_with_a_fill_axis()
    {
        var result = Observe("1 w 10 50 m 110 50 l 110 51 l 10 51 l B");
        Assert.Equal("observed", result.Availability);
        Assert.Equal(2, result.Rules.Count);
        AssertRule(result.Rules[0], "h", 750, 10, 110, 1);
        AssertRule(result.Rules[1], "h", 749, 10, 110, 1);
    }

    [Fact]
    public void Explicit_rectangle_fill_keeps_its_existing_center_axis()
    {
        var result = Observe("10 50 100 1 re f");
        Assert.Equal("observed", result.Availability);
        AssertRule(Assert.Single(result.Rules), "h", 749.5, 10, 110, 1);
    }

    [Theory]
    [InlineData("triangle", "10 50 m 110 50 l 110 70 l f", "unsupported_filled_shape")]
    [InlineData("self_crossing", "10 50 m 110 70 l 110 50 l 10 70 l f", "non_finite_or_non_axis_edges")]
    [InlineData("repeated_corner", "10 50 m 110 50 l 10 50 l 10 70 l f", "unsupported_filled_shape")]
    [InlineData("clip_only", "10 50 m 110 50 l 110 70 l 10 70 l W n", "clipping_path")]
    [InlineData("clipped_fill", "10 50 100 1 re W f", "clipping_path")]
    public void Nonrectangular_or_clipping_source_stays_refused(string caseId, string commands, string rejection)
    {
        Assert.NotEmpty(caseId);
        var result = Observe(commands);
        Assert.Equal("partial", result.Availability);
        Assert.Equal("unsupported_native_vector_geometry", result.Reason);
        Assert.Empty(result.Rules);
        Assert.Equal(1, result.Diagnostics!.RejectionCounts![rejection]);
    }

    private PdfNativeVectorObservation Observe(string commands)
    {
        var bytes = PdfNativeVectorGeometryTests.BuildPdf(commands + "\n");
        var sourceSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        output.WriteLine("Synthetic PDF SHA256: " + sourceSha);
        using var pdf = PdfDocument.Open(bytes);
        var page = pdf.GetPage(1);
        var sourceCommands = SnapshotCommands(page);
        var sourceText = page.Text;
        var result = PdfNativeVectorGeometryObserver.Observe(page, pdf.Structure, sourceSha, 100, default);

        Assert.Equal(sourceSha, result.SourceSha256);
        Assert.Equal(sourceCommands, SnapshotCommands(page));
        Assert.Equal(sourceText, page.Text);
        Assert.Equal("SOURCE LITERAL", sourceText);
        Assert.Equal("unknown", result.Diagnostics!.PaintVisibility);
        Assert.Equal("unknown", result.Diagnostics.EffectiveClip);
        Assert.Equal("complete", result.Diagnostics.InspectionCoverage);
        return result;
    }

    private static string SnapshotCommands(UglyToad.PdfPig.Content.Page page)
        => string.Join("|", page.Paths.SelectMany(path => path).SelectMany(subpath => subpath.Commands)
            .Select(command => command switch
            {
                PdfSubpath.Move move => "m:" + Point(move.Location),
                PdfSubpath.Line line => "l:" + Point(line.From) + ":" + Point(line.To),
                PdfSubpath.Close => "h",
                _ => command.GetType().FullName
            }));

    private static string Point(PdfPoint point)
        => point.X.ToString("R", CultureInfo.InvariantCulture) + "," + point.Y.ToString("R", CultureInfo.InvariantCulture);

    private static void AssertRule(CanonicalTableVectorRule rule, string axis,
        double coordinate, double start, double end, double thickness)
        => Assert.Equal((axis, coordinate, start, end, thickness),
            (rule.Axis, rule.Coordinate, rule.Start, rule.End, rule.Thickness));
}
