using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    private static async Task ExportRetrievalObservationAsync(string filename, object observation)
    {
        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (string.IsNullOrWhiteSpace(export))
            return;

        Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
        Assert.Equal(Path.GetFileName(filename), filename);
        await File.WriteAllTextAsync(
            Path.Combine(export, filename),
            JsonSerializer.Serialize(observation, new JsonSerializerOptions { WriteIndented = true }));
    }
}
