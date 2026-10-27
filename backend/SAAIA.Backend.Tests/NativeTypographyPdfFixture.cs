using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.Tests;

internal static class NativeTypographyPdfFixture
{
    internal static byte[] Pdf(string content = "BT /F1 12 Tf 60 600 Td (Not italic; 5 - 2.) Tj ET\n",
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", bool alias = false, int rotation = 0, string? secondFont = null,
        string[]? additionalObjects = null)
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Rotate {rotation} /Resources << /Font << /F1 4 0 R {(secondFont is not null ? "/F2 6 0 R" : alias ? "/F2 4 0 R" : "")} >> >> /Contents 5 0 R >>",
            font, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"];
        if (secondFont is not null) objects = [..objects, secondFont];
        if (additionalObjects is not null) objects = [..objects, ..additionalObjects];
        using var stream = new MemoryStream(); void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n"); var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++) { offsets.Add(stream.Position); Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = stream.Position; Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return stream.ToArray();
    }

    internal static (CanonicalDocument Document, PdfExtractionResult Extraction) Extract(byte[]? bytes = null)
    {
        bytes ??= Pdf(); using var stream = new MemoryStream(bytes);
        var extraction = PdfExtractor.Extract(stream, default, false, true);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var document = LegacyPdfCanonicalDocumentAdapter.Project(new(Guid.NewGuid(), Guid.NewGuid(), hash,
            bytes.Length, "fixture.pdf", new string('b', 64), "native", "pdf_text", "PdfPig", "test"), extraction, []);
        return (document, extraction);
    }
}
