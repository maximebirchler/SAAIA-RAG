using System.Net;
using System.Net.Http;
using System.Text.Json;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

internal sealed class AdvancedAnalysisApiException : HttpRequestException
{
    public AdvancedAnalysisApiException(
        HttpStatusCode statusCode,
        string errorCode,
        string? responseBody,
        TimeSpan? retryAfter)
        : base(
            $"Advanced analysis request failed with {(int)statusCode} ({errorCode}).",
            inner: null,
            statusCode)
    {
        ErrorCode = errorCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    public string ErrorCode { get; }

    public string? ResponseBody { get; }

    public TimeSpan? RetryAfter { get; }
}

public sealed partial class ApiClient
{
    public async Task<AdvancedAnalysisJobDto> CreateAdvancedAnalysisJobAsync(
        Guid sessionId,
        AdvancedAnalysisHandoffEnvelope handoff,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty chat session is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(handoff);
        if (handoff.HandoffId == Guid.Empty)
            throw new ArgumentException("A non-empty handoff id is required.", nameof(handoff));

        var snapshot = GetConfigSnapshot();
        var body = JsonSerializer.Serialize(
            new AdvancedAnalysisJobCreateRequest
            {
                UserId = RequireUserId(snapshot),
                SessionId = sessionId,
                Handoff = handoff
            },
            JsonOpts);
        using var request = NewRequest(
            snapshot,
            HttpMethod.Post,
            "/advanced-analysis/jobs",
            body);
        using var response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAdvancedAnalysisJobAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AdvancedAnalysisJobDto> GetAdvancedAnalysisJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("A non-empty advanced analysis job id is required.", nameof(jobId));

        var snapshot = GetConfigSnapshot();
        var userId = Uri.EscapeDataString(RequireUserId(snapshot));
        using var request = NewRequest(
            snapshot,
            HttpMethod.Get,
            $"/advanced-analysis/jobs/{jobId:D}?userId={userId}");
        using var response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAdvancedAnalysisJobAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AdvancedAnalysisJobDto> CancelAdvancedAnalysisJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("A non-empty advanced analysis job id is required.", nameof(jobId));

        var snapshot = GetConfigSnapshot();
        var userId = Uri.EscapeDataString(RequireUserId(snapshot));
        using var request = NewRequest(
            snapshot,
            HttpMethod.Post,
            $"/advanced-analysis/jobs/{jobId:D}/cancel?userId={userId}");
        using var response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAdvancedAnalysisJobAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AdvancedAnalysisJobDto> ReadAdvancedAnalysisJobAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AdvancedAnalysisApiException(
                response.StatusCode,
                ReadAdvancedAnalysisErrorCode(raw),
                raw,
                ReadAdvancedAnalysisRetryAfter(response));
        }

        try
        {
            return JsonSerializer.Deserialize<AdvancedAnalysisJobDto>(raw, JsonOpts)
                   ?? throw new JsonException("advanced_analysis_job_response_empty");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "advanced_analysis_job_response_invalid",
                exception);
        }
    }

    private static string ReadAdvancedAnalysisErrorCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "advanced_analysis_http_error";

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
            {
                return error.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
        }

        return "advanced_analysis_http_error";
    }

    private static TimeSpan? ReadAdvancedAnalysisRetryAfter(
        HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ClampAdvancedAnalysisRetryDelay(delta);
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return ClampAdvancedAnalysisRetryDelay(delay);
        }

        return null;
    }

    private static TimeSpan ClampAdvancedAnalysisRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay > TimeSpan.FromSeconds(60)
            ? TimeSpan.FromSeconds(60)
            : delay;
    }
}
