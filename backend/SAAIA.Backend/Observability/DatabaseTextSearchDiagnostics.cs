using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace SAAIA.Backend;

/// <summary>
/// Content-free checks of the actual database parser. A locale name alone is
/// not a capability test; no setting, source row or index is changed here.
/// </summary>
internal static class DatabaseTextSearchDiagnostics
{
    internal const string IncompatibleReason = "database_text_search_unicode_incompatible";
    internal const string UnavailableReason = "database_text_search_probe_unavailable";

    // ASCII SQL escapes make the sentinels independent of a shell/code page.
    // Keep the independent local paid gate's behavioral checks aligned.
    internal const string SentinelSql = """
        SELECT d.datctype, d.datcollate, d.datlocprovider::text, d.daticulocale,
          (to_tsvector('simple', U&'alpha\2026beta') @@ websearch_to_tsquery('simple', 'alpha beta')
           AND to_tsvector('simple', U&'alpha\2014beta') @@ websearch_to_tsquery('simple', 'alpha beta')) AS punctuation_separated,
          (to_tsvector('simple', U&'\00C9COLE') @@ websearch_to_tsquery('simple', U&'\00E9cole')) AS unicode_case_matched,
          (to_tsvector('simple', 'alpha beta') @@ websearch_to_tsquery('simple', 'alpha beta')
           AND NOT (to_tsvector('simple', 'alpha') @@ websearch_to_tsquery('simple', 'alpha beta'))
           AND NOT (to_tsvector('simple', 'beta') @@ websearch_to_tsquery('simple', 'alpha beta'))) AS conjunction_preserved,
          (to_tsvector('simple', U&'caf\00E9') @@ websearch_to_tsquery('simple', U&'caf\00E9')
           AND NOT (to_tsvector('simple', U&'caf\00E9') @@ websearch_to_tsquery('simple', 'cafe'))) AS accent_preserved
        FROM pg_catalog.pg_database d
        WHERE d.datname = current_database();
        """;

    internal static async Task<DatabaseTextSearchSnapshot?> ObserveAsync(NpgsqlConnection connection,
        ILogger? logger, CancellationToken cancellationToken)
    {
        logger ??= NullLogger.Instance;
        using var probe = BackendDiagnostics.Start("startup.database_text_search", logger);
        try
        {
            var snapshot = await ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            probe.Set("lc_ctype", snapshot.LcCtype);
            probe.Set("lc_collate", snapshot.LcCollate);
            probe.Set("locale_provider", snapshot.LocaleProvider);
            probe.Set("icu_locale", snapshot.IcuLocale);
            probe.Set("unicode_punctuation_separated", snapshot.PunctuationSeparated);
            probe.Set("unicode_case_matched", snapshot.UnicodeCaseMatched);
            probe.Set("conjunction_preserved", snapshot.ConjunctionPreserved);
            probe.Set("accent_preserved", snapshot.AccentPreserved);
            probe.Set("unicode_text_search_compatible", snapshot.Compatible);
            probe.Complete(snapshot.Compatible ? "success" : "degraded",
                snapshot.Compatible ? null : IncompatibleReason);
            if (!snapshot.Compatible)
                Warn(logger, IncompatibleReason, null);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            probe.Complete("cancelled", "database_text_search_probe_cancelled");
            throw;
        }
        catch (Exception error)
        {
            // Diagnostics must not turn a working startup into a deployment
            // failure. Never log a connection string, SQL exception message or
            // source content; an unavailable observation is not compatibility.
            probe.Set("exception_type", error.GetType().Name);
            probe.Complete("degraded", UnavailableReason);
            Warn(logger, UnavailableReason, error.GetType().Name);
            return null;
        }
    }

    internal static async Task<DatabaseTextSearchSnapshot> ReadAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction) { CommandTimeout = 5 })
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        DatabaseTextSearchSnapshot snapshot;
        await using (var command = new NpgsqlCommand(SentinelSql, connection, transaction) { CommandTimeout = 5 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Database text search metadata was not returned.");
            snapshot = new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static void Warn(ILogger logger, string reason, string? errorType)
    {
        try
        {
            logger.LogWarning("Database text search diagnostic reason={ReasonCode} error_type={ErrorType}; no database setting or content was changed", reason, errorType);
        }
        catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
    }
}

internal sealed record DatabaseTextSearchSnapshot(string LcCtype, string LcCollate,
    string LocaleProvider, string? IcuLocale, bool PunctuationSeparated,
    bool UnicodeCaseMatched, bool ConjunctionPreserved, bool AccentPreserved)
{
    internal bool Compatible => PunctuationSeparated && UnicodeCaseMatched && ConjunctionPreserved && AccentPreserved;
}
