using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PostgresSourceFidelityTests
{
    [Theory]
    [InlineData("organiza\uFFFDon iden\uFFFDfy")]
    [InlineData("The literal encoding example is \u00c3\u00a9, not the decoded letter.")]
    [InlineData("TEC 123 / IEG 456 / DOF-HDBK are literal identifiers.")]
    [InlineData("Signed limits -5 / +5; decimal 1.5 differs from 15.")]
    public void Published_source_text_is_not_repaired_or_reinterpreted_by_storage(string source)
        => Assert.Equal(source, DocumentFoundationRepo.NormalizePostgresTextForStorage(source));

    [Fact]
    public void Json_storage_preserves_literal_values_and_keys_without_alias_collisions()
    {
        const string source = "organiza\uFFFDon";
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [source] = source,
            ["organization"] = "Already a distinct literal value."
        });
        using var stored = JsonDocument.Parse(PostgresTextSanitizer.CleanJson(json)!);
        Assert.Equal(2, stored.RootElement.EnumerateObject().Count());
        Assert.Equal(source, stored.RootElement.GetProperty(source).GetString());
        Assert.Equal("Already a distinct literal value.", stored.RootElement.GetProperty("organization").GetString());
    }
}
