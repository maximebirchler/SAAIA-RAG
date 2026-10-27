using Npgsql;

namespace SAAIA.Backend.AdvancedAnalysis;

internal static partial class AdvancedAnalysisNativeObservationReader
{
    internal sealed partial class Cache
    {
        internal readonly Dictionary<(Guid Tenant, Guid Document, Guid Revision, string Source), NativeRawPageExpectedBinding> Bindings = [];
        internal readonly Dictionary<(Guid Tenant, Guid Document, Guid Revision, string Source,
            string Root, string Generation, int Page), StoredPage> Pages = [];

        internal void EnsureDocumentCapacity((Guid Tenant, Guid Document, Guid Revision, string Source) key)
        {
            if (!Documents.ContainsKey(key) && !Bindings.ContainsKey(key)
                && Documents.Keys.Concat(Bindings.Keys).Distinct().Count() >= 64)
                throw new AdvancedAnalysisToolException("native_observation_cache_budget_exceeded");
        }

        internal NativeRawPageExpectedBinding Pin(
            (Guid Tenant, Guid Document, Guid Revision, string Source) key, NativeRawPageLoadResult load)
        {
            EnsureDocumentCapacity(key);
            if (load.Root is not { Identity: { } identity } root
                || identity.TenantId != key.Tenant || identity.DocumentId != key.Document || identity.RevisionId != key.Revision
                || !string.Equals(identity.SourceSha256, key.Source, StringComparison.OrdinalIgnoreCase)
                || root.CanonicalBundleSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)
                || load.Kind is not (NativeRawPageReadKind.LegacyAbsent or NativeRawPageReadKind.ExplicitInline or NativeRawPageReadKind.RawPage)
                || load.Kind == NativeRawPageReadKind.LegacyAbsent && load.Generation is not null
                || load.Kind != NativeRawPageReadKind.LegacyAbsent && (load.Generation is not { } generation
                    || generation.GenerationId is not { Length: 64 } id || !id.All(char.IsAsciiHexDigit)
                    || generation.ProjectionVersion != NativeRawPageSchema.Projection
                    || generation.ProcessingRunId == Guid.Empty
                    || generation.ManifestSha256 is not { Length: 64 } manifest || !manifest.All(char.IsAsciiHexDigit)))
                throw Integrity();
            var binding = new NativeRawPageExpectedBinding(root.CanonicalBundleSha256.ToLowerInvariant(), load.Kind,
                load.Generation?.GenerationId.ToLowerInvariant(), load.Generation?.ProjectionVersion);
            if (Bindings.TryGetValue(key, out var expected) && binding != expected)
                throw new AdvancedAnalysisToolException("native_observation_generation_changed");
            Bindings.TryAdd(key, binding);
            return binding;
        }

        internal StoredPage AdmitPage((Guid Tenant, Guid Document, Guid Revision, string Source) key,
            NativeRawPageLoadResult load, StoredPage page)
        {
            var binding = Pin(key, load);
            var pageKey = (key.Tenant, key.Document, key.Revision, key.Source,
                binding.CanonicalBundleSha256, binding.GenerationId ?? "", page.Context.PhysicalPage);
            if (Pages.TryGetValue(pageKey, out var existing))
                return existing;
            if (load.Kind == NativeRawPageReadKind.RawPage)
            {
                if (load.PageUtf8BytesAdmitted is not { } utf8Bytes || utf8Bytes <= 0 || utf8Bytes > NativeRawPageSchema.MaximumPageBytes
                    || load.PageCompressedBytesMaterialized is not { } compressedBytes || compressedBytes <= 0
                    || compressedBytes > NativeRawPageSchema.MaximumPageBytes)
                    throw Integrity();
                if (utf8Bytes > 134_217_728 - UncompressedBytes)
                    throw new AdvancedAnalysisToolException("native_observation_cache_budget_exceeded");
                UncompressedBytes += utf8Bytes;
            }
            // Only a requested page reaches this admission. Reader requests one;
            // Resolver requests at most its distinct native reference pages.
            Pages.Add(pageKey, page);
            return page;
        }
    }

    internal sealed class StoredPage(NativeObservationPageReadContext context, string fileName, string docPath)
    {
        internal NativeObservationPageReadContext Context { get; } = context;
        internal string FileName { get; } = fileName;
        internal string DocPath { get; } = docPath;
        private readonly Dictionary<(string Block, string RawSha), IReadOnlyList<AdvancedAnalysisNativeObservationLine>> lines = [];

        internal AdvancedAnalysisNativeObservationLine Resolve(
            SAAIA.Contracts.AdvancedAnalysisNativeObservationReference reference, CancellationToken ct)
        {
            AdvancedAnalysisNativeObservationWindows.ValidateReference(reference);
            if (reference.PhysicalPage != Context.PhysicalPage)
                throw new AdvancedAnalysisToolException("native_observation_identity_or_window_invalid");
            var key = (reference.BlockId, reference.RawTextSha256.ToLowerInvariant());
            if (!lines.TryGetValue(key, out var captured))
            {
                captured = AdvancedAnalysisNativeObservationWindows.ObservedLines(Context, key.BlockId, key.Item2, ct);
                lines.Add(key, captured);
            }
            return AdvancedAnalysisNativeObservationWindows.ValidateLine(reference, captured);
        }

        internal AdvancedAnalysisNativeObservationLineSource Source(AdvancedAnalysisNativeObservationLine line)
        {
            var reference = line.Source;
            var block = Context.Blocks!.Single(item => item.BlockId == reference.BlockId);
            var count = lines[(reference.BlockId, reference.RawTextSha256.ToLowerInvariant())].Count;
            return new(reference, count, reference.LineOrdinal, 1,
                reference.LineOrdinal + 1 < count ? reference.LineOrdinal + 1 : null,
                Context.PageQualityFlags.Concat(block.QualityFlags).Append("canonical_native_audit_literal_not_primary")
                    .Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    internal static async Task<StoredPage?> LoadPageAsync(NpgsqlConnection connection, Guid tenant, Guid document,
        Guid revision, string source, int physicalPage, Cache cache, CancellationToken ct,
        NpgsqlTransaction? transaction = null)
    {
        using var detail = NativeReadDiagnostics.Start("native.page_load");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty || physicalPage <= 0
                || source is not { Length: 64 } || !source.All(char.IsAsciiHexDigit))
                throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
            var key = (tenant, document, revision, source.ToLowerInvariant());
            cache.EnsureDocumentCapacity(key);
            cache.Bindings.TryGetValue(key, out var expected);
            if (expected is not null && cache.Pages.TryGetValue((tenant, document, revision, key.Item4,
                    expected.CanonicalBundleSha256, expected.GenerationId ?? "", physicalPage), out var cached))
            {
                detail?.CacheLookup(true);
                detail?.Complete();
                return cached;
            }
            detail?.CacheLookup(false);
            NativeRawPageLoadResult load;
            try
            {
                load = await CanonicalNativeRawPageRepo.ReadAsync(connection, transaction, tenant, document, revision,
                    source, physicalPage, expected, ct);
            }
            catch (InvalidDataException exception)
            {
                // Only the fixed generation code is distinguished. Never copy a
                // private decoder/SQL message into a tool response or inner error.
                throw new AdvancedAnalysisToolException(exception.Message == "native_observation_generation_changed"
                    ? "native_observation_generation_changed" : "native_observation_page_integrity_invalid");
            }
            if (load.Kind == NativeRawPageReadKind.ScopeMissing)
            {
                if (expected is not null)
                    throw new AdvancedAnalysisToolException("native_observation_generation_changed");
                detail?.Complete();
                return null;
            }
            var binding = cache.Pin(key, load);
            NativeObservationPageReadContext context;
            if (load.Kind == NativeRawPageReadKind.RawPage)
            {
                context = ContextFromLoad(load, physicalPage);
                detail?.Set("payload_bytes_materialized", load.PageCompressedBytesMaterialized);
                detail?.Set("inflated_bytes_produced", load.PageUtf8BytesAdmitted);
            }
            else
            {
                var inline = await LoadAsync(connection, tenant, document, revision, source, cache, ct,
                    transaction, binding.CanonicalBundleSha256) ?? throw Integrity();
                if (load.Generation is { } generation && (inline.ProcessingRunId != generation.ProcessingRunId
                    || !string.Equals(inline.Document.ManifestSha256, generation.ManifestSha256, StringComparison.OrdinalIgnoreCase)))
                    throw Integrity();
                context = NativeObservationPageReadContext.FromInline(tenant, inline.Document, physicalPage);
            }
            if (load.FileName is null || load.DocPath is null)
                throw Integrity();
            var stored = cache.AdmitPage(key, load, new(context, load.FileName, load.DocPath));
            detail?.Complete();
            return stored;
        }
        catch (Exception error)
        {
            detail?.Fail(error);
            throw;
        }
    }

    private static AdvancedAnalysisToolException Integrity() => new("native_observation_page_integrity_invalid");

    internal static NativeObservationPageReadContext ContextFromLoad(NativeRawPageLoadResult load, int physicalPage)
    {
        if (load.Page is not { } page || page.PhysicalPage != physicalPage || load.Root is not { } root
            || load.Generation is not { } generation || page.Root.Identity.TenantId != root.Identity.TenantId
            || page.Root.Identity.DocumentId != root.Identity.DocumentId || page.Root.Identity.RevisionId != root.Identity.RevisionId
            || !string.Equals(page.Root.Identity.SourceSha256, root.Identity.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(page.Root.CanonicalBundleSha256, root.CanonicalBundleSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(page.Generation.GenerationId, generation.GenerationId, StringComparison.OrdinalIgnoreCase)
            || page.Generation.ProjectionVersion != generation.ProjectionVersion || page.Generation.ProcessingRunId != generation.ProcessingRunId
            || !string.Equals(page.Generation.ManifestSha256, generation.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw Integrity();
        return NativeObservationPageReadContext.FromStored(page);
    }
}
