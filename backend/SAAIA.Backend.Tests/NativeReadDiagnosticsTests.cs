using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Contracts.Tests;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeReadDiagnosticsTests
{
    private const string OptIn = "saaia.native_read.detail.enabled";

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Disabled_context_keeps_bundle_bytes_and_emits_no_detail(bool? enabled)
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = BackendDiagnostics.Start("test.native_read", logger))
        {
            if (enabled.HasValue) Activity.Current!.SetCustomProperty(OptIn, enabled.Value);
            var restored = CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            Assert.Equal(artifact.ContentHash, CanonicalArtifactRepo.BuildBundleArtifact(restored).ContentHash);
            parent.Complete();
        }
        Assert.Empty(Details(logger));
    }

    [Fact]
    public void Enabled_real_bundle_separates_inflate_hash_utf8_json_and_validation_with_exact_units()
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var logger = Read(artifact);
        var phases = Phases(logger);
        Assert.Equal(new[] { "gzip_inflate_copy", "hash_validate", "utf8_decode", "json_deserialize", "bundle_validate" },
            phases.Select(FieldPhase));
        Assert.All(phases, entry =>
        {
            using var fields = Fields(entry);
            Assert.Equal(1, fields.RootElement.GetProperty("attempt_count").GetInt32());
            Assert.Equal(1, fields.RootElement.GetProperty("completed_count").GetInt32());
            Assert.Equal("inclusive_monotonic_wall", fields.RootElement.GetProperty("duration_scope").GetString());
            Assert.True((double)entry.Values["DurationMs"]! >= 0);
            Assert.Equal("success", entry.Values["Outcome"]);
        });
        using var inflate = Fields(phases[0]);
        Assert.Equal(artifact.Payload.LongLength, inflate.RootElement.GetProperty("payload_bytes_materialized").GetInt64());
        Assert.Equal(artifact.ByteSize, inflate.RootElement.GetProperty("inflated_bytes_produced").GetInt64());
        using var hash = Fields(phases[1]);
        Assert.Equal(artifact.ByteSize, hash.RootElement.GetProperty("sha256_bytes_processed").GetInt64());
        using var utf8 = Fields(phases[2]);
        Assert.Equal(artifact.ByteSize, utf8.RootElement.GetProperty("utf8_input_bytes").GetInt64());
        Assert.True(utf8.RootElement.GetProperty("decoded_utf16_characters").GetInt64() > 0);
        var decode = Assert.Single(Details(logger), entry => Equals(entry.Values["Operation"], "canonical.bundle_decode"));
        Assert.All(phases, entry =>
        {
            Assert.Equal(decode.Values["TraceId"], entry.Values["TraceId"]);
            using var fields = Fields(entry);
            Assert.Equal(decode.Values["SpanId"], fields.RootElement.GetProperty("parent_span_id").GetString());
        });
    }

    [Theory]
    [InlineData("gzip", "gzip_inflate_copy", 0)]
    [InlineData("hash", "hash_validate", 1)]
    [InlineData("json", "json_deserialize", 3)]
    [InlineData("contract", "bundle_validate", 4)]
    public void Failures_keep_exact_last_phase_and_never_claim_downstream_completed(string failure, string lastPhase, int completed)
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        artifact = failure switch
        {
            "gzip" => artifact with { Payload = [0, 1, 2, 3] },
            "hash" => artifact with { ContentHash = new byte[32] },
            "json" => RawArtifact("{PRIVATE_JSON_SENTINEL"),
            _ => InvalidContractArtifact()
        };
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        {
            Assert.ThrowsAny<Exception>(() => CanonicalArtifactRepo.ReadBundleArtifact(artifact));
            parent.Complete();
        }
        var phases = Phases(logger);
        Assert.NotEmpty(phases);
        Assert.Equal(lastPhase, FieldPhase(phases[^1]));
        Assert.Equal(completed, phases.Count(entry => Equals(entry.Values["Outcome"], "success")));
        using var fields = Fields(phases[^1]);
        Assert.Equal(0, fields.RootElement.GetProperty("completed_count").GetInt32());
        if (failure == "gzip")
            Assert.Equal(JsonValueKind.Null, fields.RootElement.GetProperty("inflated_bytes_produced").ValueKind);
        Assert.DoesNotContain(logger.Events, entry => entry.Text.Contains("PRIVATE_JSON_SENTINEL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opt_in_and_correlated_sinks_do_not_cross_parallel_or_unrelated_contexts()
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var enabled = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        var disabled = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        await Task.WhenAll(Execute(enabled, true), Execute(disabled, false));
        Assert.Equal(5, Phases(enabled).Length);
        Assert.Empty(Details(disabled));
        using (var parent = EnabledParent(enabled))
        using (var unrelated = new Activity("unrelated").SetParentId(
                   ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None).Start())
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
        Assert.Equal(5, Phases(enabled).Length);

        async Task Execute(DiagnosticRecordingLogger<NativeReadDiagnosticsTests> logger, bool active)
        {
            using var parent = BackendDiagnostics.Start("test.native_parallel", logger);
            Activity.Current!.SetCustomProperty(OptIn, active);
            await Task.Yield();
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            parent.Complete();
        }
    }

    [Fact]
    public void A_throwing_diagnostic_sink_cannot_change_success_or_business_failure()
    {
        using var parent = BackendDiagnostics.Start("test.native_sink", new ThrowingLogger());
        Activity.Current!.SetCustomProperty(OptIn, true);
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var collectionFailuresBefore = BackendDiagnostics.CollectionFailures;
        var restored = CanonicalArtifactRepo.ReadBundleArtifact(artifact);
        Assert.True(BackendDiagnostics.CollectionFailures > collectionFailuresBefore);
        Assert.Equal(artifact.ContentHash, CanonicalArtifactRepo.BuildBundleArtifact(restored).ContentHash);
        Assert.Throws<InvalidDataException>(() => CanonicalArtifactRepo.ReadBundleArtifact(artifact with { ContentHash = new byte[32] }));
        parent.Complete();
    }

    [Fact]
    public async Task Existing_cache_hit_records_no_sql_or_decode_and_preserves_the_stored_object()
    {
        var document = Bundle().Document;
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        var tenant = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var stored = new AdvancedAnalysisNativeObservationReader.StoredDocument(document, "PRIVATE_NAME.pdf", "PRIVATE_PATH");
        cache.Documents.Add((tenant, document.DocumentId, document.RevisionId, document.Source.Sha256), stored);
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        {
            for (var index = 0; index < 2; index++)
                Assert.Same(stored, await AdvancedAnalysisNativeObservationReader.LoadAsync(null!, tenant,
                    document.DocumentId, document.RevisionId, document.Source.Sha256, cache, CancellationToken.None));
            parent.Complete();
        }
        var loads = logger.Events.Where(entry => Equals(entry.Values["Operation"], "native.bundle_load")).ToArray();
        Assert.Equal(2, loads.Length);
        Assert.All(loads, entry =>
        {
            using var fields = Fields(entry);
            Assert.Equal("hit", fields.RootElement.GetProperty("cache_lookup").GetString());
            Assert.Equal(1, fields.RootElement.GetProperty("completed_count").GetInt32());
        });
        Assert.Empty(Phases(logger));
        Assert.DoesNotContain(logger.Events, entry => entry.Text.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.Single(cache.Documents);
    }

    [Fact]
    public void Repeated_artifact_calls_record_two_decodes_without_sharing_a_new_cache()
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        {
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            parent.Complete();
        }
        Assert.Equal(2, Details(logger).Count(entry => Equals(entry.Values["Operation"], "canonical.bundle_decode")));
        Assert.Equal(2, Phases(logger).Count(entry => FieldPhase(entry) == "json_deserialize"));
    }

    [Fact]
    public void Malformed_opt_in_and_untrusted_phase_or_field_names_cannot_publish_content()
    {
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(Bundle());
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = BackendDiagnostics.Start("test.native_invalid_flag", logger))
        {
            Activity.Current!.SetCustomProperty(OptIn, "true");
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            Assert.Null(NativeReadDiagnostics.Start("canonical.bundle_decode"));
            Activity.Current.SetCustomProperty(OptIn, true);
            Assert.Null(NativeReadDiagnostics.Start("PRIVATE_OPERATION"));
            using var detail = NativeReadDiagnostics.Start("native.reader");
            Assert.NotNull(detail);
            Assert.Null(detail.Phase("PRIVATE_PHASE"));
            detail.Set("PRIVATE_FIELD", 42);
            detail.Complete();
            parent.Complete();
        }
        Assert.Empty(Phases(logger));
        Assert.DoesNotContain(logger.Events, entry => entry.Text.Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancellation_is_correlated_without_exception_message_or_false_completion()
    {
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        {
            using var detail = NativeReadDiagnostics.Start("native.reader");
            Assert.NotNull(detail);
            detail.Fail(new OperationCanceledException("PRIVATE_CANCEL_SENTINEL"));
            parent.Complete();
        }
        var record = Assert.Single(logger.Events, entry => Equals(entry.Values["Operation"], "native.reader"));
        Assert.Equal("cancelled", record.Values["Outcome"]);
        using var fields = Fields(record);
        Assert.Equal(0, fields.RootElement.GetProperty("completed_count").GetInt32());
        Assert.DoesNotContain(logger.Events, entry => entry.Text.Contains("PRIVATE_CANCEL_SENTINEL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_nearer_explicit_false_suppresses_an_enabled_parent()
    {
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        using (var child = new Activity("test.native_disabled_child").Start())
        {
            child.SetCustomProperty(OptIn, false);
            Assert.Null(NativeReadDiagnostics.Start("native.reader"));
            CanonicalArtifactRepo.ReadBundleArtifact(CanonicalArtifactRepo.BuildBundleArtifact(Bundle()));
            parent.Complete();
        }
        Assert.Empty(Details(logger));
    }

    private static DiagnosticRecordingLogger<NativeReadDiagnosticsTests> Read(CanonicalCompressedArtifact artifact)
    {
        var logger = new DiagnosticRecordingLogger<NativeReadDiagnosticsTests>();
        using (var parent = EnabledParent(logger))
        {
            CanonicalArtifactRepo.ReadBundleArtifact(artifact);
            parent.Complete();
        }
        return logger;
    }

    private static BackendDiagnostics.Operation EnabledParent(ILogger logger)
    {
        var parent = BackendDiagnostics.Start("test.native_read", logger);
        Activity.Current!.SetCustomProperty(OptIn, true);
        return parent;
    }

    private static DiagnosticRecordingLogger<NativeReadDiagnosticsTests>.Entry[] Details(DiagnosticRecordingLogger<NativeReadDiagnosticsTests> logger)
        => logger.Events.Where(entry => Equals(entry.Values["Operation"], "native.read.phase")
            || Equals(entry.Values["Operation"], "canonical.bundle_decode")).ToArray();
    private static DiagnosticRecordingLogger<NativeReadDiagnosticsTests>.Entry[] Phases(DiagnosticRecordingLogger<NativeReadDiagnosticsTests> logger)
        => Details(logger).Where(entry => Equals(entry.Values["Operation"], "native.read.phase")).ToArray();
    private static JsonDocument Fields(DiagnosticRecordingLogger<NativeReadDiagnosticsTests>.Entry entry)
        => JsonDocument.Parse((string)entry.Values["Measurements"]!);
    private static string? FieldPhase(DiagnosticRecordingLogger<NativeReadDiagnosticsTests>.Entry entry)
    {
        using var fields = Fields(entry);
        return fields.RootElement.GetProperty("phase").GetString();
    }

    private static CanonicalCompressedArtifact InvalidContractArtifact()
    {
        var bundle = Bundle();
        bundle.Document.PageCount = -1;
        return RawArtifact(CanonicalContractJson.Serialize(bundle));
    }

    private static CanonicalCompressedArtifact RawArtifact(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(raw);
        return new(CanonicalArtifactRepo.BundleArtifactType, CanonicalSchema.BundleVersion, SHA256.HashData(raw), raw.Length, output.ToArray());
    }

    private static CanonicalIngestionBundle Bundle()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var manifest = new IngestionManifest
        {
            ProcessingRunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            DocumentId = document.DocumentId, RevisionId = document.RevisionId, SourceSha256 = document.Source.Sha256,
            CreatedAtUtc = DateTimeOffset.Parse("2026-10-27T08:00:00Z"), CodeRevision = "abcdef12",
            Hardware = new() { ProfileId = "fixture", LogicalProcessorCount = 2, MemoryBytes = 1_000_000,
                Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
            Stages = [new() { StageId = "native_parser", StageType = "parse", Engine = "fixture", EngineVersion = "1",
                OptionsSha256 = new string('d', 64), DeviceId = "cpu" }],
            Artifacts = [new() { ArtifactId = "source", ArtifactType = CanonicalArtifactTypes.SourceDocument,
                SchemaVersion = "application/pdf", Sha256 = document.Source.Sha256,
                StorageKey = "content://sha256/" + document.Source.Sha256, SizeBytes = document.Source.SizeBytes }]
        };
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        document.ManifestSha256 = envelope.ManifestSha256;
        return new() { Document = document, ManifestEnvelope = envelope, SourceAnchors = [CanonicalContractFixture.CreateAnchor(document)] };
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("PRIVATE_SINK_SENTINEL");
    }
}
