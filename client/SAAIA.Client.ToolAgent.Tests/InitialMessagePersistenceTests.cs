using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Client.Core.Conversation;
using SAAIA.Client.WinUI;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class InitialMessagePersistenceTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "saaia-initial-persistence-" + Guid.NewGuid().ToString("N"));

    public InitialMessagePersistenceTests()
        => Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClientDataPaths.RootEnvironmentVariable, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Failed_initial_post_preserves_display_and_wire_then_explicit_retry_commits_once()
    {
        var calls = 0;
        var contents = new List<string>();
        using var harness = new SubmissionHarness(async (request, _) =>
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            contents.Add(body.RootElement.GetProperty("content").GetString()!);
            return ++calls == 1 ? new(HttpStatusCode.InternalServerError) : Acknowledged();
        });

        await Assert.ThrowsAsync<InitialMessagePersistenceUnconfirmedException>(() => harness.SendAsync());
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Equal("Instructions wire préservées", harness.PendingWire);
        Assert.Equal("Question affichée", harness.PendingDisplay);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        Assert.Equal("Instructions wire préservées", StagedOutboundMessageState.ResolveForSend(
            harness.PendingWire, harness.PendingDisplay, harness.VisibleText).EffectiveWireText);
        Assert.False(harness.Gate.IsRunningTurn);

        Assert.True(await harness.SendAsync());
        Assert.Equal(2, calls);
        Assert.All(contents, content => Assert.Equal("Question affichée", content));
        Assert.Empty(harness.VisibleText);
        Assert.Null(harness.PendingWire);
        Assert.Null(harness.PendingDisplay);
        Assert.Single(harness.VisibleMessages);
        Assert.Equal(1, harness.ModelCalls);
    }

    [Fact]
    public async Task Held_post_keeps_composer_and_blocks_double_admission_until_acknowledged()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var harness = new SubmissionHarness(async (_, _) =>
        {
            writes++;
            entered.SetResult();
            await release.Task;
            return Acknowledged();
        });
        var sending = harness.SendAsync();
        await entered.Task;
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        Assert.False(await harness.SendAsync());
        Assert.Null(harness.Gate.TryBeginSessionChange("another-session"));
        Assert.Equal(1, writes);
        release.SetResult();
        Assert.True(await sending);
        Assert.Single(harness.VisibleMessages);
        Assert.Equal(1, harness.ModelCalls);
    }

    [Fact]
    public async Task Cancellation_during_storage_waits_for_ack_commits_visible_user_then_never_calls_model()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken writeToken = default;
        using var harness = new SubmissionHarness(async (_, token) =>
        {
            writeToken = token;
            entered.SetResult();
            await release.Task;
            return Acknowledged();
        });
        var sending = harness.SendAsync();
        await entered.Task;
        harness.Gate.CancelActiveTurn();
        Assert.False(writeToken.IsCancellationRequested);
        Assert.True(harness.Gate.IsRunningTurn);
        Assert.False(await harness.SendAsync());
        Assert.Equal("Question affichée", harness.VisibleText);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Empty(harness.VisibleText);
        Assert.Single(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        Assert.False(harness.Gate.IsRunningTurn);
    }

    [Fact]
    public async Task Lost_response_is_unconfirmed_without_automatic_retry_or_false_unsaved_claim()
    {
        var serverCommits = 0;
        var calls = 0;
        using var harness = new SubmissionHarness((_, _) =>
        {
            calls++;
            serverCommits++;
            throw new HttpRequestException("synthetic response lost after server commit");
        });
        string trace;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(Guid.NewGuid().ToString("D")))
        {
            trace = turn.TraceId;
            var exception = await Assert.ThrowsAsync<InitialMessagePersistenceUnconfirmedException>(() => harness.SendAsync());
            Assert.IsType<HttpRequestException>(exception.InnerException);
            turn.Complete("failed", "user_message_persistence_unconfirmed");
        }
        Assert.Equal(1, serverCommits);
        Assert.Equal(1, calls);
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == trace);
        var failure = Assert.Single(record.Events, item => item.Operation == "chat.user.persistence");
        Assert.Equal("failed", failure.Outcome);
        Assert.Equal("confirmation_unavailable", failure.Reason);
        Assert.DoesNotContain("synthetic response", JsonSerializer.Serialize(record));
    }

    [Fact]
    public void Compiled_winui_send_admits_then_confirms_storage_before_autotitle_or_model()
    {
        var calls = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "SendAsync").ToArray();
        var admission = Array.FindIndex(calls, call => call.DeclaringType == typeof(ConversationActivityGate)
            && call.Name == nameof(ConversationActivityGate.TryBeginTurn));
        var persist = Array.FindIndex(calls, call => call.DeclaringType == typeof(InitialMessagePersistence));
        var title = Array.FindIndex(calls, call => call.Name == "MaybeAutoTitleAsync");
        var model = Array.FindIndex(calls, call => call.DeclaringType == typeof(RagChatAgent) && call.Name == "RunAsync");
        Assert.True(admission >= 0 && admission < persist && persist < title && title < model);
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(ApiClient) && call.Name == "AddMessageAsync");
        Assert.Contains("user_message_persistence_unconfirmed", CompiledArchitectureContract.StringsForMethod(typeof(MainWindow), "SendAsync"));
    }

    public static IEnumerable<object[]> InvalidAcknowledgements()
    {
        const string fields = "\"messageId\":\"11111111-1111-4111-8111-111111111111\",\"role\":\"user\",\"content\":\"Question affichée\"";
        yield return new object[] { "empty", "" };
        yield return new object[] { "null", "null" };
        yield return new object[] { "empty-object", "{}" };
        yield return new object[] { "array", "[]" };
        yield return new object[] { "non-guid-identity", "{" + fields.Replace("11111111-1111-4111-8111-111111111111", "not-an-id") + "}" };
        yield return new object[] { "empty-guid-identity", "{" + fields.Replace("11111111-1111-4111-8111-111111111111", Guid.Empty.ToString()) + "}" };
        yield return new object[] { "missing-role", "{" + fields.Replace("\"role\":\"user\",", "") + "}" };
        yield return new object[] { "wrong-role", "{" + fields.Replace("\"role\":\"user\"", "\"role\":\"assistant\"") + "}" };
        yield return new object[] { "missing-content", "{\"messageId\":\"11111111-1111-4111-8111-111111111111\",\"role\":\"user\"}" };
        yield return new object[] { "wrong-content", "{" + fields.Replace("Question affichée", "other draft") + "}" };
        yield return new object[] { "wrong-session", "{" + fields + ",\"sessionId\":\"99999999-9999-4999-8999-999999999999\"}" };
        yield return new object[] { "wrong-user", "{" + fields + ",\"userId\":\"other-user\"}" };
    }

    [Theory]
    [MemberData(nameof(InvalidAcknowledgements))]
    public async Task Invalid_200_response_cannot_clear_draft_or_start_model(string caseName, string responseBody)
    {
        var requests = 0;
        using var harness = new SubmissionHarness((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        });
        var error = await Assert.ThrowsAsync<InitialMessagePersistenceUnconfirmedException>(() => harness.SendAsync());
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(1, requests);
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Equal("Instructions wire préservées", harness.PendingWire);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        Assert.False(string.IsNullOrWhiteSpace(caseName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_message_acknowledgement_matches_content_role_and_optional_owner_echo(bool pascalCase)
    {
        using var harness = new SubmissionHarness(async (request, _) =>
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            var message = new Dictionary<string, string>
            {
                [pascalCase ? "MessageId" : "messageId"] = Guid.NewGuid().ToString("D"),
                [pascalCase ? "Role" : "role"] = "user",
                [pascalCase ? "Content" : "content"] = body.RootElement.GetProperty("content").GetString()!,
                [pascalCase ? "SessionId" : "sessionId"] = SubmissionHarness.SessionKey,
                [pascalCase ? "UserId" : "userId"] = body.RootElement.GetProperty("userId").GetString()!
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json") };
        });
        Assert.True(await harness.SendAsync());
        Assert.Empty(harness.VisibleText);
        Assert.Single(harness.VisibleMessages);
        Assert.Equal(1, harness.ModelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Silent_transport_reaches_its_own_deadline_even_after_user_cancel_without_losing_draft(bool userCancels)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var harness = new SubmissionHarness(async (_, token) =>
        {
            requests++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Acknowledged();
        }, confirmationTimeout: TimeSpan.FromMilliseconds(500));
        string trace;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(Guid.NewGuid().ToString("D")))
        {
            trace = turn.TraceId;
            var pending = harness.SendAsync();
            await entered.Task;
            if (userCancels) harness.Gate.CancelActiveTurn();
            Assert.True(harness.Gate.IsRunningTurn);
            Assert.False(await harness.SendAsync());
            Assert.Null(harness.Gate.TryBeginSessionChange("other"));
            Assert.Equal("Question affichée", harness.VisibleText);
            var error = await Assert.ThrowsAsync<InitialMessagePersistenceUnconfirmedException>(() => pending);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            turn.Complete("failed", "user_message_persistence_unconfirmed");
        }
        Assert.Equal(1, requests);
        Assert.False(harness.Gate.IsRunningTurn);
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Equal("Instructions wire préservées", harness.PendingWire);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal(0, harness.ModelCalls);
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == trace);
        var failure = Assert.Single(record.Events, item => item.Operation == "chat.user.persistence");
        Assert.Equal("confirmation_timeout", failure.Reason);
        Assert.Equal(500, failure.Measurements["confirmationTimeoutMs"]);
    }

    [Fact]
    public async Task Deadline_does_not_replace_a_business_http_failure_with_a_timeout()
    {
        using var harness = new SubmissionHarness((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)),
            confirmationTimeout: TimeSpan.FromMilliseconds(500));
        var error = await Assert.ThrowsAsync<InitialMessagePersistenceUnconfirmedException>(() => harness.SendAsync());
        Assert.Equal(HttpStatusCode.Conflict, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        Assert.Empty(harness.VisibleMessages);
        Assert.Equal("Question affichée", harness.VisibleText);
        Assert.Equal(0, harness.ModelCalls);
    }

    [Fact]
    public async Task Confirmation_deadline_is_positive_and_never_exceeds_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), InitialMessagePersistence.MaximumConfirmationTimeout);
        using var harness = new SubmissionHarness((_, _) => Task.FromResult(Acknowledged()),
            confirmationTimeout: TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.SendAsync());
        using var zero = new SubmissionHarness((_, _) => Task.FromResult(Acknowledged()), confirmationTimeout: TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => zero.SendAsync());
    }

    private static HttpResponseMessage Acknowledged() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"messageId":"11111111-1111-4111-8111-111111111111","role":"user","content":"Question affichée"}""", Encoding.UTF8, "application/json")
    };

    private sealed class SubmissionHarness : IDisposable
    {
        internal const string SessionKey = "66666666-6666-4666-8666-666666666666";
        internal readonly ConversationActivityGate Gate = new();
        internal string VisibleText = "Question affichée";
        internal string? PendingWire = "Instructions wire préservées";
        internal string? PendingDisplay = "Question affichée";
        internal readonly List<string> VisibleMessages = new();
        internal int ModelCalls;
        private readonly HttpClient _http;
        private readonly ApiClient _api;
        private readonly TimeSpan? _confirmationTimeout;

        internal SubmissionHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            TimeSpan? confirmationTimeout = null)
        {
            _confirmationTimeout = confirmationTimeout;
            _http = new HttpClient(new Transport(respond));
            _api = new ApiClient(_http);
            _api.Configure("http://loopback-fixture.invalid", "synthetic-credential", Guid.NewGuid().ToString("D"));
        }

        internal async Task<bool> SendAsync()
        {
            using var lease = Gate.TryBeginTurn(SessionKey);
            if (lease is null) return false;
            await InitialMessagePersistence.PersistBeforeGenerationAsync(_api, lease.SessionId!, VisibleText,
                saved =>
                {
                    VisibleMessages.Add(saved?.MessageId ?? "acknowledged-without-id");
                    VisibleText = string.Empty;
                    PendingWire = PendingDisplay = null;
                }, lease.CancellationToken, _confirmationTimeout);
            ModelCalls++;
            return true;
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
