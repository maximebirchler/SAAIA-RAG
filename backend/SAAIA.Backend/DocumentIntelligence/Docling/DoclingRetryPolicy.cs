using System.Net;
using System.Text.Json;

/// <summary>Only proven transport unavailability is retried; document/schema failures remain terminal.</summary>
internal static class DoclingRetryPolicy
{
    internal const int MaximumAttempts = 5;

    internal static TimeSpan DelayAfterAttempt(int attempt)
        => attempt is >= 1 and < MaximumAttempts
            ? TimeSpan.FromSeconds(1 << (attempt - 1))
            : throw new ArgumentOutOfRangeException(nameof(attempt));

    internal static bool IsTransient(HttpRequestException exception)
        => exception is TransientResponseException || exception.StatusCode is null;

    internal static HttpRequestException FailureForResponse(HttpStatusCode statusCode, byte[] body)
        => IsTransientResponse(statusCode, body)
            ? new TransientResponseException(statusCode)
            : SafeFailure(statusCode);

    internal static HttpRequestException SafeFailure(HttpStatusCode? statusCode)
        => new(statusCode is null
                ? "Docling conversion network transport failed (docling_conversion_transport_error)."
                : $"Docling conversion failed with HTTP {(int)statusCode} (docling_conversion_http_error).",
            inner: null, statusCode);

    private static bool IsTransientResponse(HttpStatusCode statusCode, byte[] body)
    {
        if (statusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            return true;
        if (statusCode != HttpStatusCode.BadGateway)
            return false;
        if (body.Length > 4096)
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            // Duplicate discriminator keys are ambiguous and cannot prove a transport failure.
            var details = document.RootElement.EnumerateObject().Where(property => property.NameEquals("detail")).ToArray();
            return details.Length == 1 && details[0].Value.ValueKind == JsonValueKind.String
                && string.Equals(details[0].Value.GetString(), "Docling upstream conversion failed.", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class TransientResponseException(HttpStatusCode statusCode)
        : HttpRequestException($"Docling conversion failed with HTTP {(int)statusCode} (docling_conversion_http_error).", null, statusCode);
}
