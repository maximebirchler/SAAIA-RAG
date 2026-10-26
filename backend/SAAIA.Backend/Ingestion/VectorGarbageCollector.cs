using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;

internal sealed class VectorGarbageCollector(NpgsqlDataSource ds, IVectorGcTransport transport,
    IOptions<VectorGcOptions> options, ILogger<VectorGarbageCollector> log, VectorGcRuntimeState? state = null)
{
    public async Task<VectorGcRoundResult> RunOnceAsync(CancellationToken ct)
    {
        using var diagnostic = BackendDiagnostics.Start("vector_gc.round", log);
        try
        {
            var result = await RunCoreAsync(ct);
            diagnostic.Set("retired_plans", result.RetiredPlans);
            diagnostic.Set("claimed_plans", result.ClaimedPlans);
            diagnostic.Set("verified_absent_points", result.VerifiedAbsentPoints);
            diagnostic.Set("deferred_plans", result.DeferredPlans);
            diagnostic.Complete(options.Value.Enabled ? result.DeferredPlans > 0 ? "degraded" : "success" : "skipped",
                options.Value.Enabled ? null : "disabled");
            return result;
        }
        catch (Exception exception) { state?.Fail(); diagnostic.Fail(exception); throw; }
    }

    private async Task<VectorGcRoundResult> RunCoreAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) return new(0, 0, 0, 0);
        state?.Begin();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var roundSeconds = Math.Clamp(settings.RoundTimeoutSeconds, 1, 120);
        deadline.CancelAfter(TimeSpan.FromSeconds(roundSeconds));
        var token = deadline.Token;
        int retired;
        await using (var conn = await ds.OpenConnectionAsync(token))
            retired = await IngestionVectorRegistry.RetireEligibleAsync(conn, null,
                Math.Clamp(settings.MaxPlansPerRound, 1, 16) * 4, token);
        if (retired > 0) log.LogInformation("vector_registry.retired count={Count}", retired);
        var claimed = await VectorGcQueue.ClaimAsync(ds, settings.MaxPlansPerRound, roundSeconds + 60, token);
        int absent = 0, deferred = 0;
        foreach (var plan in claimed)
        {
            token.ThrowIfCancellationRequested();
            log.LogInformation("vector_gc.claimed plan={PlanId} cursor={Cursor} expected={Count}", plan.PlanId, plan.Cursor, plan.PointCount);
            IReadOnlyList<IngestionVectorPoint> points = [];
            VectorGcBatchResult result;
            try
            {
                points = await VectorGcQueue.ReadBatchAsync(ds, plan, settings.MaxPointsPerBatch, token);
                using var batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                batchDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.BatchTimeoutSeconds, 1, 30)));
                result = points.Count == 0 && plan.Cursor != plan.PointCount
                    ? new("manifest_incomplete", false, 0, 0)
                    : await transport.CollectAsync(plan, points, batchDeadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { result = new("operation_timeout", false, 0, 0); }
            catch (System.Text.Json.JsonException) { result = new("response_json_invalid", false, 0, 0); }
            catch (InvalidDataException exception) { result = new(exception.Message == "vector_gc_manifest_incomplete" ? "manifest_incomplete" : "response_or_manifest_invalid", false, 0, 0); }
            catch (Exception) { result = new("operation_failed", false, 0, 0); }
            // A fresh bounded token allows a timeout to release/defer its claim. No checkpoint
            // after shutdown and no former owner can acknowledge a successor's work.
            using var finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
            finish.CancelAfter(TimeSpan.FromSeconds(5));
            var committed = await VectorGcQueue.CheckpointAsync(ds, plan, points.Count, result,
                settings.RecheckSeconds, settings.FailureBackoffSeconds, finish.Token);
            if (!committed)
            {
                deferred++;
                log.LogWarning("vector_gc.lease_lost plan={PlanId}", plan.PlanId);
                continue;
            }
            if (result.AbsenceVerified) absent += result.AbsentCount;
            else deferred++;
            if (result.Code.StartsWith("reappearance_", StringComparison.Ordinal))
                log.LogWarning("vector_gc.reappearance_observed plan={PlanId} code={Code}", plan.PlanId, result.Code);
            log.LogInformation("vector_gc.batch plan={PlanId} code={Code} absence_observed={Absent} delete_acknowledged={Acknowledged}",
                plan.PlanId, result.Code, result.AbsentCount, result.DeleteAcknowledgedCount);
        }
        var round = new VectorGcRoundResult(retired, claimed.Count, absent, deferred);
        var inventory = await VectorGcInventory.ReadAsync(ds, token);
        state?.Complete(round, inventory);
        log.LogInformation("vector_gc.inventory registered={Registered} retired={Retired} protected={Protected} rechecks_due={Due} targets_blocked={Blocked} transport_failures={Transport} protocol_failures={Protocol} quarantined={Quarantined} unmanaged_active={Unmanaged}",
            inventory.RegisteredPlans, inventory.RetiredPlans, inventory.ProtectedPlans, inventory.DueRechecks,
            inventory.BlockedTargets, inventory.TransportFailures, inventory.ProtocolFailures, inventory.QuarantinedPlans, inventory.UnmanagedActivePublications);
        return round;
    }
}

internal sealed class VectorGcWorker(IServiceProvider services, IOptions<VectorGcOptions> options,
    ILogger<VectorGcWorker> log, VectorGcRuntimeState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.WorkerStarted(options.Value.Enabled);
        log.LogInformation("vector_gc.worker_state enabled={Enabled}", options.Value.Enabled);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try { await services.GetRequiredService<VectorGarbageCollector>().RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { state.Fail(); log.LogWarning("vector_gc.round_incomplete"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 3600)), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
