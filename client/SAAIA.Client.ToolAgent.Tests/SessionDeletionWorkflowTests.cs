using System.Net;
using System.Text;
using SAAIA.Client.WinUI;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class SessionDeletionWorkflowTests
{
    [Fact]
    public async Task Pending_delete_does_not_remove_the_session_or_attempt_refresh_before_the_real_acknowledgement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(async (request, _) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                entered.SetResult();
                await release.Task;
            }
            return Json(HttpStatusCode.OK, "[]");
        });
        var pending = harness.DeleteAsync(active: false);
        await entered.Task;
        Assert.Equal(new[] { "A", "B" }, harness.Sessions);
        Assert.Equal(0, harness.RefreshAttempts);
        release.SetResult();
        var result = await pending;
        Assert.Null(result.RefreshFailure);
        Assert.Equal(new[] { "A" }, harness.Sessions);
    }

    [Fact]
    public async Task Failed_delete_keeps_the_entire_loaded_conversation_and_never_claims_acknowledgement()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.DeleteAsync(active: true));
        Assert.Equal(new[] { "A", "B" }, harness.Sessions);
        Assert.Equal("B", harness.Current);
        Assert.Equal("messages-B", harness.Messages);
        Assert.Equal("sources-B", harness.Sources);
        Assert.Equal(0, harness.RefreshAttempts);
        Assert.Equal(0, harness.RecoveryAttempts);
        var record = ClientDiagnostics.Journal.Last(1, "session_delete").Single();
        Assert.Equal("failed", record.Outcome);
        Assert.Equal("delete_unconfirmed", record.Reason);
        Assert.DoesNotContain(record.Events, item => item.Operation == "session.delete.acknowledged");
    }

    [Fact]
    public async Task Inactive_delete_acknowledged_then_inventory_500_preserves_loaded_memory_and_removes_only_the_target()
    {
        using var harness = FailingInventory();
        var result = await harness.DeleteAsync(active: false);
        Assert.IsType<HttpRequestException>(result.RefreshFailure);
        Assert.True(result.ConversationAvailable);
        Assert.Null(result.RecoveryFailure);
        Assert.Equal(new[] { "A" }, harness.Sessions);
        Assert.Equal("A", harness.Current);
        Assert.Equal("messages-A", harness.Messages);
        Assert.Equal("sources-A", harness.Sources);
        Assert.Equal("memory-A", harness.Memory);
        Assert.Equal(new[] { "DELETE", "GET" }, harness.Methods);
        Assert.Equal(1, harness.RecoveryAttempts);
    }

    [Fact]
    public async Task Active_delete_acknowledged_then_inventory_500_loads_the_known_survivor_with_its_own_memory()
    {
        using var harness = FailingInventory();
        var result = await harness.DeleteAsync(active: true);
        Assert.IsType<HttpRequestException>(result.RefreshFailure);
        Assert.True(result.ConversationAvailable);
        Assert.Equal(new[] { "A" }, harness.Sessions);
        Assert.Equal("A", harness.Current);
        Assert.Equal("messages-A", harness.Messages);
        Assert.Equal("sources-A", harness.Sources);
        Assert.Equal("memory-A", harness.Memory);
        Assert.True(harness.TargetAlreadyRemovedAtRefresh);
    }

    [Fact]
    public async Task Failure_with_no_known_survivor_keeps_the_deleted_id_absent_and_reports_no_loaded_session()
    {
        using var harness = FailingInventory();
        harness.Sessions.Remove("A");
        var result = await harness.DeleteAsync(active: true);
        Assert.Empty(harness.Sessions);
        Assert.Null(harness.Current);
        Assert.Empty(harness.Messages);
        Assert.Empty(harness.Sources);
        Assert.Empty(harness.Memory);
        Assert.False(result.ConversationAvailable);
        var record = ClientDiagnostics.Journal.Last(1, "session_delete").Single();
        Assert.Contains(record.Events, item => item.Operation == "session.delete.recovery"
            && item.Outcome == "degraded" && item.Reason == "no_loaded_session");
    }

    [Fact]
    public async Task Failure_loading_a_known_survivor_cannot_turn_confirmed_deletion_into_a_failed_delete()
    {
        using var harness = FailingInventory();
        harness.FailRecovery = true;
        var result = await harness.DeleteAsync(active: true);
        Assert.IsType<HttpRequestException>(result.RefreshFailure);
        Assert.IsType<IOException>(result.RecoveryFailure);
        Assert.False(result.ConversationAvailable);
        Assert.Equal(new[] { "A" }, harness.Sessions);
        Assert.Null(harness.Current);
        Assert.Empty(harness.Messages);
        var record = ClientDiagnostics.Journal.Last(1, "session_delete").Single();
        Assert.Equal("degraded", record.Outcome);
        Assert.Equal("deleted_refresh_failed", record.Reason);
        Assert.Contains(record.Events, item => item.Operation == "session.delete.acknowledged");
    }

    [Fact]
    public async Task Diagnostics_order_acknowledgement_before_refresh_failure_and_exclude_message_or_source_content()
    {
        using var harness = FailingInventory();
        await harness.DeleteAsync(active: true);
        var record = ClientDiagnostics.Journal.Last(1, "session_delete").Single();
        var acknowledged = record.Events.Single(item => item.Operation == "session.delete.acknowledged");
        var failed = record.Events.Single(item => item.Operation == "session.delete.refresh_failed");
        Assert.True(acknowledged.Sequence < failed.Sequence);
        Assert.Equal(Harness.TargetId, record.SessionId);
        var serialized = System.Text.Json.JsonSerializer.Serialize(record);
        Assert.DoesNotContain("messages-", serialized);
        Assert.DoesNotContain("sources-", serialized);
        Assert.DoesNotContain("synthetic-fixture-key", serialized);
    }

    [Fact]
    public async Task Successful_refresh_does_not_run_the_fallback_recovery()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "[]")));
        var result = await harness.DeleteAsync(active: true);
        Assert.Null(result.RefreshFailure);
        Assert.Null(result.RecoveryFailure);
        Assert.Equal(0, harness.RecoveryAttempts);
        Assert.Equal("A", harness.Current);
    }

    [Fact]
    public void Compiled_ui_handler_uses_the_acknowledged_deletion_workflow_and_distinct_failure_notice()
    {
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "SessionDelete_Click"),
            call => call.DeclaringType == typeof(SessionDeletionWorkflow) && call.Name == "ExecuteAsync");
        Assert.Contains("delete_refresh", CompiledArchitectureContract.StringsForMethod(typeof(MainWindow), "SessionDelete_Click"));
        Assert.Contains("session.operation.delete_refresh.recovery",
            CompiledArchitectureContract.StringsForMethod(typeof(MainWindow), "RenderConversationOperationFailure"));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Confirmed_deletion_refresh_notice_is_localized_and_distinct_from_unconfirmed_storage(string language)
    {
        var title = ClientUiText.Get("session.operation.delete_refresh.failed", language);
        var recovery = ClientUiText.Get("session.operation.delete_refresh.recovery", language);
        Assert.DoesNotContain("session.operation.", title + recovery);
        Assert.NotEqual(ClientUiText.Get("session.operation.delete.failed", language), title);
        Assert.NotEqual(ClientUiText.Get("session.operation.recovery", language), recovery);
        if (language == "fr")
        {
            Assert.Contains("supprimée", title);
            Assert.Contains("La suppression est confirmée", recovery);
            Assert.Contains("Nouveau", recovery);
            Assert.DoesNotContain("Votre saisie est conservée", recovery);
        }
    }

    private static Harness FailingInventory() => new((request, _) => Task.FromResult(
        Json(request.Method == HttpMethod.Delete ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)));

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "{}")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Harness : IDisposable
    {
        internal const string TargetId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        private readonly HttpClient _http;
        private readonly ApiClient _api;
        internal List<string> Sessions { get; } = ["A", "B"];
        internal List<string> Methods { get; } = [];
        internal string? Current { get; private set; }
        internal string Messages { get; private set; } = "";
        internal string Sources { get; private set; } = "";
        internal string Memory { get; private set; } = "";
        internal int RefreshAttempts { get; private set; }
        internal int RecoveryAttempts { get; private set; }
        internal bool TargetAlreadyRemovedAtRefresh { get; private set; }
        internal bool FailRecovery { get; set; }

        internal Harness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _http = new HttpClient(new Handler(async (request, token) =>
            {
                Methods.Add(request.Method.Method);
                return await respond(request, token);
            }));
            _api = new ApiClient(_http);
            _api.Configure("http://127.0.0.1:1", "synthetic-fixture-key", "fixture-user");
        }

        internal Task<SessionDeletionResult> DeleteAsync(bool active)
        {
            Load(active ? "B" : "A");
            return SessionDeletionWorkflow.ExecuteAsync(_api, TargetId, () =>
            {
                Sessions.Remove("B");
                if (active)
                {
                    Current = null;
                    Messages = Sources = Memory = "";
                }
            }, async () =>
            {
                RefreshAttempts++;
                TargetAlreadyRemovedAtRefresh = !Sessions.Contains("B");
                await _api.ListSessionsAsync(CancellationToken.None);
                if (active && Sessions.Count > 0) Load(Sessions[0]);
            }, () =>
            {
                RecoveryAttempts++;
                if (FailRecovery) throw new IOException("synthetic surviving history failure");
                if (active && Sessions.Count > 0) Load(Sessions[0]);
                return Task.FromResult(Current is not null);
            });
        }

        private void Load(string id)
        {
            Current = id;
            Messages = "messages-" + id;
            Sources = "sources-" + id;
            Memory = "memory-" + id;
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
