using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeLayoutExtractorTests
{
    [Fact]
    public void Extract_PreservesColumnWiseAlternativeForNativePdfText()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"saaia-pdf-layout-{Guid.NewGuid():N}.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var page = builder.AddPage(PageSize.A4);
            var font = builder.AddStandard14Font(
                Standard14Font.Helvetica);
            page.AddText(
                "LAYOUT TEST",
                12,
                new PdfPoint(40, 790),
                font);
            page.AddText(
                "LEFT COLUMN",
                11,
                new PdfPoint(40, 730),
                font);
            page.AddText(
                "LEFT ALPHA 101",
                10,
                new PdfPoint(40, 700),
                font);
            page.AddText(
                "LEFT BRAVO 102",
                10,
                new PdfPoint(40, 680),
                font);
            page.AddText(
                "RIGHT COLUMN",
                11,
                new PdfPoint(330, 730),
                font);
            page.AddText(
                "RIGHT ECHO 201",
                10,
                new PdfPoint(330, 700),
                font);
            page.AddText(
                "RIGHT FOXTROT 202",
                10,
                new PdfPoint(330, 680),
                font);
            File.WriteAllBytes(path, builder.Build());

            var extraction = PdfExtractor.Extract(path);
            var extractedPage = Assert.Single(extraction.Pages);

            Assert.Equal(
                PdfNativeLayoutExtractor.Algorithm,
                extractedPage.NativeLayoutAlgorithm);
            Assert.NotEmpty(extractedPage.NativeLayoutBlocks!);
            Assert.True(
                extractedPage.NativeLayoutText!.IndexOf(
                    "LEFT BRAVO 102",
                    StringComparison.Ordinal)
                < extractedPage.NativeLayoutText.IndexOf(
                    "RIGHT COLUMN",
                    StringComparison.Ordinal),
                extractedPage.NativeLayoutText);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
