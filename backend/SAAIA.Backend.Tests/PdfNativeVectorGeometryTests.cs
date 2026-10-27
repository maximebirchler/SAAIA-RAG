using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using Xunit;
namespace SAAIA.Backend.Tests;
public sealed class PdfNativeVectorGeometryTests
{
    [Fact]
    public void Secondary_vector_filled_thin_rectangle_is_one_center_bar_without_vertical_caps()
    {
        var bytes=BuildPdf("50 100 200 .5 re f\n");
        using var stream=new MemoryStream(bytes);
        var extraction=PdfExtractor.Extract(stream,default,true);
        var observed=Assert.Single(extraction.Pages).NativeVectorObservation!;
        Assert.Equal("observed",observed.Availability);
        var bar=Assert.Single(observed.Rules);
        Assert.Equal("h",bar.Axis); Assert.Equal(699.75,bar.Coordinate);
        Assert.Equal(50,bar.Start);Assert.Equal(250,bar.End);Assert.Equal(.5,bar.Thickness);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),observed.SourceSha256);
        Assert.True(stream.CanRead);
    }
    [Fact]
    public void Secondary_vector_default_off_does_not_change_primary_extraction()
    {
        var bytes=BuildPdf("50 100 200 .5 re f\n");
        using var stream=new MemoryStream(bytes);using var enabledStream=new MemoryStream(bytes);
        var off=Assert.Single(PdfExtractor.Extract(stream,default).Pages);
        var enabled=Assert.Single(PdfExtractor.Extract(enabledStream,default,true).Pages);
        Assert.Null(off.NativeVectorObservation); Assert.Equal(off.Text,enabled.Text);
        Assert.Equal(off.Checksum,enabled.Checksum);
    }
    [Fact]
    public void Secondary_vector_command_budget_refuses_without_false_empty_observation()
    {
        using var pdf=PdfDocument.Open(BuildPdf("50 100 200 .5 re f\n"));
        var result=PdfNativeVectorGeometryObserver.Observe(pdf.GetPage(1),pdf.Structure,new string('a',64),100,default,maximumCommands:1);
        Assert.Equal("refused",result.Availability);Assert.Equal("page_vector_command_limit",result.Reason);
        Assert.Empty(result.Rules);Assert.InRange(result.InspectedCommandCount,0,1);
    }
    [Theory]
    [InlineData(90,false,"unsupported_native_rotation")]
    [InlineData(0,true,"unsupported_native_origin")]
    public void Secondary_vector_unproven_frame_cannot_be_an_observed_empty_success(int rotation,bool shifted,string reason)
    {
        using var stream=new MemoryStream(BuildPdf("50 100 200 .5 re f\n",rotation,shifted));
        var result=Assert.Single(PdfExtractor.Extract(stream,default,true).Pages).NativeVectorObservation!;
        Assert.Equal("unavailable",result.Availability);Assert.Equal(reason,result.Reason);Assert.Empty(result.Rules);
    }
    [Fact]
    public void Secondary_vector_curves_remain_partial_without_bounding_box_rules()
    {
        using var stream=new MemoryStream(BuildPdf("50 100 m 100 110 150 120 200 100 c S\n"));
        var result=Assert.Single(PdfExtractor.Extract(stream,default,true).Pages).NativeVectorObservation!;
        Assert.Equal("partial",result.Availability);Assert.Empty(result.Rules);
    }
    [Fact]
    public void Secondary_vector_observer_honors_cancellation_before_scanning()
    {
        using var pdf=PdfDocument.Open(BuildPdf("50 100 200 .5 re f\n"));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>PdfNativeVectorGeometryObserver.Observe(pdf.GetPage(1),pdf.Structure,new string('a',64),100,cancellation.Token));
    }
    [Theory]
    [InlineData("1 w 50 100 m 250 100 l S\n", "observed", 1)]
    [InlineData("1 w [3 2] 0 d 50 100 m 250 100 l S\n", "partial", 0)]
    [InlineData("", "observed", 0)]
    public void Secondary_vector_explicit_solid_dashed_and_observed_empty_are_distinct(string commands,string availability,int count)
    {
        using var stream=new MemoryStream(BuildPdf(commands));
        var result=Assert.Single(PdfExtractor.Extract(stream,default,true).Pages).NativeVectorObservation!;
        Assert.Equal(availability,result.Availability);Assert.Equal(count,result.Rules.Count);
    }
    [Fact]
    public void Secondary_vector_path_and_document_limits_return_refusals_with_bounded_counters()
    {
        using var pdf=PdfDocument.Open(BuildPdf("50 100 200 .5 re f\n50 200 200 .5 re f\n"));
        var page=pdf.GetPage(1);
        var paths=PdfNativeVectorGeometryObserver.Observe(page,pdf.Structure,new string('a',64),100,default,maximumPaths:1);
        Assert.Equal("refused",paths.Availability);Assert.Equal("page_vector_path_limit",paths.Reason);Assert.Empty(paths.Rules);
        var commands=PdfNativeVectorGeometryObserver.Observe(page,pdf.Structure,new string('a',64),1,default);
        Assert.Equal("refused",commands.Availability);Assert.Equal("document_vector_command_limit",commands.Reason);
        Assert.InRange(commands.InspectedCommandCount,0,1);Assert.Empty(commands.Rules);
    }
    internal static byte[] BuildPdf(string commands,int rotation=0,bool shifted=false)
    {
        var content="BT /F1 10 Tf 60 600 Td (SOURCE LITERAL) Tj ET\n"+commands;
        string[] objects=["<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800]{(shifted?" /CropBox [20 30 580 770]":"")} /Rotate {rotation} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",$"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"];
        using var stream=new MemoryStream();void Write(string s)=>stream.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");var offsets=new List<long>();
        for(var i=0;i<objects.Length;i++){offsets.Add(stream.Position);Write($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
        var xref=stream.Position;Write($"xref\n0 {objects.Length+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets)Write($"{offset.ToString("D10",CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");return stream.ToArray();
    }
}
