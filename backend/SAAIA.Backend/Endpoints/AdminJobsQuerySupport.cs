using System.Globalization;

namespace SAAIA.Backend.Endpoints;

internal static class AdminJobsQuerySupport
{
    public static string GetVisibleIngestionStatus(
        string? status,
        bool cancelRequested,
        string? requestedAction,
        string? action,
        int? documentIndexedVersion,
        bool? documentAutoIngestPaused,
        string? documentAutoIngestPauseReason)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "paused")
            return "paused";

        if (normalized == "running"
            && cancelRequested
            && IngestionAdminStatePolicies.ShouldPresentAsPaused(
                action,
                documentIndexedVersion ?? 0,
                requestedAction,
                documentAutoIngestPaused ?? false,
                documentAutoIngestPauseReason))
        {
            return "paused";
        }

        if (normalized == "running" && cancelRequested)
            return "cancel_requested";

        return normalized;
    }

    public static string NormalizeDateField(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "created" => "created",
            "started" => "started",
            _ => "finished"
        };
    }

    public static string NormalizeSortDirection(string? value)
        => string.Equals((value ?? string.Empty).Trim(), "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

    public static DateTimeOffset? ParseDateParameter(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
