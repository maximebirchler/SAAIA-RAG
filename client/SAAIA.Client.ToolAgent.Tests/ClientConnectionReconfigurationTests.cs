using System.Net;
using System.Text;
using SAAIA.Client.WinUI;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClientConnectionReconfigurationTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void Applied_configuration_reconnects_an_existing_connection_even_with_automatic_connection_disabled(
        bool connected, bool automatic, bool requiresSetup, bool reconnect)
        => Assert.Equal(reconnect, ClientConnectionPolicy.ShouldReconnectAfterConfiguration(connected, automatic, requiresSetup));

    [Fact]
    public async Task Background_snapshot_keeps_old_endpoint_and_identity_while_foreground_reconfigures()
    {
        var requests = new List<(string Host, string Key, string? Admin)>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add((request.RequestUri!.Host, request.Headers.GetValues("X-Api-Key").Single(),
                request.Headers.TryGetValues("X-Admin-Key", out var admin) ? admin.Single() : null));
            return new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }));
        var foreground = new ApiClient(http);
        foreground.Configure("http://old-endpoint.invalid", "synthetic-old-key", "same-user", "old-admin");
        var background = foreground.CreateConnectionSnapshot();
        Assert.True(background.HasSameConnection(foreground));
        foreground.ClearAdminSessionKey();
        foreground.Configure("http://new-endpoint.invalid", "synthetic-new-key", "same-user");
        Assert.False(background.HasSameConnection(foreground));

        await background.ListSessionsAsync(CancellationToken.None);
        await foreground.ListSessionsAsync(CancellationToken.None);
        Assert.Equal(new[] { "old-endpoint.invalid", "new-endpoint.invalid" }, requests.Select(item => item.Host));
        Assert.Equal(new[] { "synthetic-old-key", "synthetic-new-key" }, requests.Select(item => item.Key));
        Assert.True(background.HasAdminKey);
        Assert.False(foreground.HasAdminKey);
    }

    [Fact]
    public async Task Late_background_persistence_cannot_write_to_the_new_endpoint_even_after_a_failed_reconnect()
    {
        var writes = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                writes.Add(request.RequestUri!.Host);
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            return new(HttpStatusCode.InternalServerError);
        }));
        var foreground = new ApiClient(http);
        foreground.Configure("http://old-endpoint.invalid", "old-key", "same-user");
        var background = foreground.CreateConnectionSnapshot();
        foreground.Configure("http://failed-new-endpoint.invalid", "new-key", "same-user");
        await Assert.ThrowsAsync<HttpRequestException>(() => foreground.ListSessionsAsync(CancellationToken.None));
        await background.PatchMessageAsync("old-message", "old-job-final", null, null, null, CancellationToken.None);
        Assert.Equal("old-endpoint.invalid", Assert.Single(writes));
        Assert.False(background.HasSameConnection(foreground));
    }

    [Fact]
    public async Task Same_connection_admin_logout_removes_credentials_from_the_next_background_admin_request()
    {
        var headers = new List<string?>();
        using var http = new HttpClient(new Handler(request =>
        {
            headers.Add(request.Headers.TryGetValues("X-Admin-Key", out var values) ? values.Single() : null);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));
        var active = new ApiClient(http);
        active.Configure("http://same-endpoint.invalid", "same-key", "same-user", "synthetic-admin-before-logout");
        var background = active.CreateConnectionSnapshot();
        await background.AdminJobGetAsync("fixture-job", CancellationToken.None);
        active.ClearAdminSessionKey();
        background.SynchronizeAdminSession(active);
        await background.AdminJobGetAsync("fixture-job", CancellationToken.None);

        Assert.False(background.HasAdminKey);
        Assert.Equal(new string?[] { "synthetic-admin-before-logout", null }, headers);
    }

    [Fact]
    public async Task Another_endpoint_admin_session_cannot_be_adopted_by_an_old_background_owner()
    {
        (string Host, string? Admin)? requestSeen = null;
        using var http = new HttpClient(new Handler(request =>
        {
            requestSeen = (request.RequestUri!.Host,
                request.Headers.TryGetValues("X-Admin-Key", out var values) ? values.Single() : null);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));
        var active = new ApiClient(http);
        active.Configure("http://old-endpoint.invalid", "old-key", "same-user", "synthetic-old-admin");
        var background = active.CreateConnectionSnapshot();
        active.Configure("http://new-endpoint.invalid", "new-key", "same-user", "synthetic-new-admin");
        background.SynchronizeAdminSession(active);
        await background.AdminJobGetAsync("fixture-job", CancellationToken.None);

        Assert.Equal(("old-endpoint.invalid", (string?)"synthetic-old-admin"), requestSeen);
    }

    [Theory]
    [InlineData("changed-key", "same-user")]
    [InlineData("same-key", "changed-user")]
    public void Identity_changes_on_the_same_endpoint_do_not_match_previous_background_owners(string key, string user)
    {
        var api = new ApiClient();
        api.Configure("http://same-endpoint.invalid", "same-key", "same-user");
        var owner = api.CreateConnectionSnapshot();
        api.Configure("http://same-endpoint.invalid", key, user);
        Assert.False(owner.HasSameConnection(api));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_unauthorized_response_cannot_revoke_a_new_admin_authentication_on_the_same_endpoint(bool sameCredential)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            seen.Add(request.Headers.GetValues("X-Admin-Key").Single());
            if (seen.Count == 1)
            {
                entered.SetResult();
                await release.Task;
                return new(HttpStatusCode.Unauthorized);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));
        var active = new ApiClient(http);
        active.Configure("http://same-endpoint.invalid", "same-key", "same-user", "synthetic-old-admin");
        var owner = active.CreateConnectionSnapshot();
        var attempted = owner.CreateConnectionSnapshot();
        var pending = attempted.AdminJobGetAsync("fixture-job", CancellationToken.None);
        await entered.Task;
        var replacement = sameCredential ? "synthetic-old-admin" : "synthetic-new-admin";
        active.SetAdminSessionKey(replacement);
        release.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.False(active.TryInvalidateAdminSession(attempted));
        Assert.True(active.HasAdminKey);
        Assert.True(owner.TryInvalidateAdminSession(attempted));
        owner.SynchronizeAdminSession(active);
        await owner.AdminJobGetAsync("fixture-job", CancellationToken.None);
        Assert.Equal(new[] { "synthetic-old-admin", replacement }, seen);
    }

    [Fact]
    public void Rejected_current_credentials_are_revoked_but_another_endpoint_is_untouched()
    {
        var active = new ApiClient();
        active.Configure("http://old-endpoint.invalid", "same-key", "same-user", "synthetic-admin");
        var rejected = active.CreateConnectionSnapshot();
        Assert.True(active.TryInvalidateAdminSession(rejected));
        Assert.False(active.HasAdminKey);
        active.Configure("http://other-endpoint.invalid", "same-key", "same-user", "synthetic-admin");
        Assert.False(active.TryInvalidateAdminSession(rejected));
        Assert.True(active.HasAdminKey);
    }

    [Fact]
    public void Compiled_connection_and_trackers_use_owned_contexts_and_clear_both_success_and_failure_paths()
    {
        var connect = CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "ConnectAsync");
        Assert.True(connect.Count(call => call.DeclaringType == typeof(MainWindow) && call.Name == "ResetConnectionSurface") >= 2);
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "ShowSetupWizardAsync"),
            call => call.DeclaringType == typeof(ClientConnectionPolicy));
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "TrackAdvancedAnalysisMessageAsync"),
            call => call.DeclaringType == typeof(ApiClient) && call.Name == "CreateConnectionSnapshot");
        Assert.Contains(CompiledArchitectureContract.CallsForMethod(typeof(MainWindow), "StartDirectCommandJobTracker"),
            call => call.DeclaringType == typeof(ApiClient) && call.Name == "CreateConnectionSnapshot");
    }

    [Fact]
    public void Extracted_network_classifier_preserves_actionable_connection_errors()
    {
        Assert.Contains("refusée", ConnectionErrorClassifier.Classify(new HttpRequestException("connection refused"), "http://fixture", "fr"));
        Assert.Contains("timeout", ConnectionErrorClassifier.Classify(new TaskCanceledException(), "http://fixture", "en"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "clé API a été refusée")]
    [InlineData(HttpStatusCode.Forbidden, "ne permet pas d'accéder")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public void Known_http_errors_are_localized_without_exposing_the_raw_english_exception(HttpStatusCode status, string expected)
    {
        var error = new HttpRequestException("Response status code does not indicate success: RAW-DIAGNOSTIC", null, status);
        var text = ConnectionErrorClassifier.Classify(error, "http://fixture", "fr");
        Assert.Contains(expected, text);
        Assert.DoesNotContain("Response status", text);
        Assert.DoesNotContain("RAW-DIAGNOSTIC", text);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
