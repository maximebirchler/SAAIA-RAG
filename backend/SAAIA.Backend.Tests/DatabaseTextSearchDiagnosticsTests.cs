using System.Text.Json;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DatabaseTextSearchDiagnosticsTests
{
    [Fact]
    public async Task Caller_cancellation_is_reported_and_propagated_instead_of_marking_startup_successful()
    {
        var logger = new DiagnosticRecordingLogger<DatabaseTextSearchDiagnosticsTests>();
        await using var closed = new NpgsqlConnection("Host=127.0.0.1;Username=synthetic_cancel_probe");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseTextSearchDiagnostics.ObserveAsync(closed, logger, cancellation.Token));
        var terminal = Assert.Single(logger.Events);
        Assert.Equal("startup.database_text_search", terminal.Values["Operation"]);
        Assert.Equal("cancelled", terminal.Values["Outcome"]);
        Assert.Equal("database_text_search_probe_cancelled", terminal.Values["ReasonCode"]);
    }

    [Fact]
    public async Task Unavailable_database_probe_never_blocks_startup_or_logs_connection_credentials()
    {
        var logger = new DiagnosticRecordingLogger<DatabaseTextSearchDiagnosticsTests>();
        // Kept closed: no connection, service or network attempt is made.
        await using var closed = new NpgsqlConnection("Host=127.0.0.1;Username=private_probe_user;Password=private_probe_password");
        var result = await DatabaseTextSearchDiagnostics.ObserveAsync(closed, logger, CancellationToken.None);
        Assert.Null(result);
        var terminal = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var value)
            && Equals(value, "startup.database_text_search"));
        Assert.Equal("degraded", terminal.Values["Outcome"]);
        Assert.Equal(DatabaseTextSearchDiagnostics.UnavailableReason, terminal.Values["ReasonCode"]);
        Assert.All(logger.Events, entry =>
        {
            Assert.DoesNotContain("private_probe_user", entry.Text);
            Assert.DoesNotContain("private_probe_password", entry.Text);
        });
        using var fields = JsonDocument.Parse((string)terminal.Values["Measurements"]!);
        Assert.False(fields.RootElement.TryGetProperty("unicode_text_search_compatible", out _));
    }
}
