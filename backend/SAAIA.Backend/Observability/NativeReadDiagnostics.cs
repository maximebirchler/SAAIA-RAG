using System.Diagnostics;

namespace SAAIA.Backend;

/// <summary>Opt-in, content-free timings. It never retains documents or changes a read.</summary>
internal static class NativeReadDiagnostics
{
    internal const string OptInProperty = "saaia.native_read.detail.enabled";
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        { "native.reader", "native.bundle_load", "native.resolver", "native.line_resolve", "canonical.bundle_decode",
          "native.page_load", "native.page_decode" };
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "connection_open", "transaction_begin_readonly", "transaction_commit", "sql_fetch_materialize",
        "gzip_inflate_copy", "hash_validate", "utf8_decode", "json_deserialize", "bundle_validate",
        "native_window", "window_bound_serialize", "reference_loop", "overview_sql",
        "visual_inventory", "table_inventory", "line_validate", "native_page_validate"
    };
    private static readonly string[] ByteFields =
    [
        "payload_bytes_materialized", "declared_uncompressed_bytes", "inflated_bytes_produced",
        "sha256_bytes_processed", "utf8_input_bytes", "decoded_utf16_characters"
    ];
    private static readonly HashSet<string> NumericFields = new(ByteFields.Concat(
        ["row_count", "reference_count", "native_reference_count", "returned_reference_count"]), StringComparer.Ordinal);

    internal static Scope? Start(string operation)
    {
        if (!Enabled() || !Operations.Contains(operation)) return null;
        return Create(operation, null);
    }

    private static bool Enabled()
    {
        var current = Activity.Current;
        var trace = current?.TraceId;
        for (var depth = 0; current is not null && depth < 64 && current.TraceId == trace; depth++, current = current.Parent)
        {
            var value = current.GetCustomProperty(OptInProperty);
            if (value is not null) return value is true;
        }
        return false;
    }

    private static Scope? Create(string operation, string? phase)
    {
        BackendDiagnostics.Operation? probe = null;
        try
        {
            var parentSpan = Activity.Current?.SpanId.ToHexString();
            probe = BackendDiagnostics.Start(operation);
            probe.Set("schema_version", "saaia.native-read.phase.v1");
            probe.Set("parent_span_id", parentSpan);
            probe.Set("phase", phase);
            probe.Set("duration_scope", "inclusive_monotonic_wall");
            probe.Set("attempt_count", 1);
            probe.Set("completed_count", 0);
            foreach (var field in ByteFields) probe.Set(field, null);
            return new(probe);
        }
        catch (Exception)
        {
            try { probe?.Dispose(); } catch (Exception) { }
            BackendDiagnostics.RecordCollectionFailure();
            return null;
        }
    }

    internal sealed class Scope(BackendDiagnostics.Operation probe) : IDisposable
    {
        private bool disposed;

        internal Scope? Phase(string name) => !disposed && Phases.Contains(name) ? Create("native.read.phase", name) : null;

        internal void Set(string name, long? value)
        {
            if (NumericFields.Contains(name)) Safe(() => probe.Set(name, value));
        }

        internal void CacheLookup(bool hit) => Safe(() => probe.Set("cache_lookup", hit ? "hit" : "miss"));

        internal void Complete()
        {
            Safe(() =>
            {
                probe.Set("completed_count", 1);
                probe.Complete();
            });
        }

        internal void Fail(Exception error) => Safe(() => probe.Fail(error));

        private void Safe(Action action)
        {
            if (disposed) return;
            try { action(); }
            catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { probe.Dispose(); }
            catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
        }
    }
}
