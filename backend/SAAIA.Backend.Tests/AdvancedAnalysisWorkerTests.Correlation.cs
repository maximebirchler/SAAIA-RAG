using System.Text.Json;
using Dapper;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(null, false)]
    [InlineData("mismatched", true)]
    [InlineData("matching_compact_guid", false)]
    public async Task Worker_logs_the_authoritative_job_session_and_separates_client_claims(string? clientHeader, bool mismatch)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync()
            ?? throw new InvalidOperationException("IntegrationPg requires its PostgreSQL fixture.");
        var seed = await database.SeedEvidenceAsync("correlation", "Procedure alpha");
        var jobId = await database.InsertJobAsync(seed, BuildHandoff(seed, "evidence-alpha"));
        var claimed = clientHeader is null ? null : clientHeader == "matching_compact_guid"
            ? seed.SessionId.ToString("N") : Guid.NewGuid().ToString("D");
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE advanced_analysis_jobs SET diagnostic_session_id=@claimed WHERE job_id=@jobId", new { claimed, jobId });
        var logger = new DiagnosticRecordingLogger<AdvancedAnalysisWorker>();
        var provider = new DelegateProvider((request, _) => Task.FromResult(SuccessfulResult(Assert.Single(request.Evidence).Reference.EvidenceId)));
        Assert.True(await CreateWorker(database.DataSource, provider, logger: logger).ProcessOnceAsync(CancellationToken.None));
        var completed = Assert.Single(logger.Events, entry => entry.Values.TryGetValue("Operation", out var operation) && Equals(operation, "advanced.job_attempt"));
        using var fields = JsonDocument.Parse((string)completed.Values["Measurements"]!);
        Assert.Equal(seed.SessionId.ToString("D"), fields.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(claimed, fields.RootElement.GetProperty("client_session_id").GetString());
        Assert.Equal(mismatch, fields.RootElement.GetProperty("client_session_id_mismatch").GetBoolean());
        Assert.Contains(logger.Scopes, scope => scope.TryGetValue("session_id", out var session) && Equals(session, seed.SessionId)
            && scope.TryGetValue("client_session_id", out var header) && Equals(header, claimed));
        await using var read = await database.DataSource.OpenConnectionAsync();
        var sessions = (await read.QueryAsync<string>("SELECT session_id FROM advanced_analysis_job_events WHERE job_id=@jobId", new { jobId })).ToArray();
        Assert.NotEmpty(sessions);
        Assert.All(sessions, session => Assert.Equal(seed.SessionId.ToString("D"), session));
    }
}
