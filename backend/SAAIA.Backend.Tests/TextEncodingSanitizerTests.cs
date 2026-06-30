using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class TextEncodingSanitizerTests
{
    [Fact]
    public void PdfTextSanitizer_repairs_common_mojibake_without_breaking_real_multilingual_text()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pr\u00c3\u00a9paration \u00e2\u20ac\u00a2 l\u00e2\u20ac\u2122utilisateur\0");

        Assert.Equal("Préparation • l’utilisateur ", sanitized);
        Assert.Equal("NÃO", PdfTextSanitizer.ForStorage("N\u00c3O"));
        Assert.Equal(
            "مرحبا 中文",
            PdfTextSanitizer.ForStorage("\u0645\u0631\u062d\u0628\u0627 \u4e2d\u6587"));
    }

    [Fact]
    public void PostgresTextSanitizer_repairs_text_arrays_and_json_strings()
    {
        Assert.Equal("Matériel – vérifié", PostgresTextSanitizer.Clean("Mat\u00c3\u00a9riel \u00e2\u20ac\u201c v\u00c3\u00a9rifi\u00c3\u00a9\0"));
        Assert.Equal(["Déjà", "NÃO"], PostgresTextSanitizer.CleanArray(["D\u00c3\u00a9j\u00c3\u00a0", "N\u00c3O", "D\u00c3\u00a9j\u00c3\u00a0"]));

        using var doc = JsonDocument.Parse(
            """
            {
              "title": "PrÃ©paration",
              "quote": "lâ€™utilisateur",
              "languageSample": "NÃO"
            }
            """);

        var cleaned = PostgresTextSanitizer.CleanJson(doc.RootElement);

        Assert.NotNull(cleaned);
        using var cleanedDoc = JsonDocument.Parse(cleaned!);
        Assert.Equal("Préparation", cleanedDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal("l’utilisateur", cleanedDoc.RootElement.GetProperty("quote").GetString());
        Assert.Equal("NÃO", cleanedDoc.RootElement.GetProperty("languageSample").GetString());
        var cleanedSerializedJson = PostgresTextSanitizer.CleanJson(JsonSerializer.Serialize(new
        {
            evidence = "source\0snippet"
        }));

        Assert.NotNull(cleanedSerializedJson);
        using var cleanedSerializedDoc = JsonDocument.Parse(cleanedSerializedJson!);
        Assert.Equal("sourcesnippet", cleanedSerializedDoc.RootElement.GetProperty("evidence").GetString());
    }

    [Fact]
    public void PdfTextSanitizer_repairs_pdf_replacement_characters_without_storing_unknown_glyphs()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "organiza\uFFFDon con\uFFFDngency iden\uFFFDfy \uFFFD heading");

        Assert.DoesNotContain('\uFFFD', sanitized);
        Assert.Contains("organization", sanitized, StringComparison.Ordinal);
        Assert.Contains("contingency", sanitized, StringComparison.Ordinal);
        Assert.Contains("identify", sanitized, StringComparison.Ordinal);
        Assert.Contains(" heading", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_soft_line_hyphenation_and_preserves_paragraph_breaks()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "The imple-\nmentation keeps para-\ngraphs.\n\nNext\u00a0block");

        Assert.Contains("implementation", sanitized, StringComparison.Ordinal);
        Assert.Contains("paragraphs", sanitized, StringComparison.Ordinal);
        Assert.Contains("\n\nNext block", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("imple-", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\u00a0', sanitized);
    }
}
