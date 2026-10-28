using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Auxiliary only. A refusal never changes the primary bytes, DTO or projection.</summary>
internal sealed class DoclingInputGeometryCapture(int attempt, DoclingInputGeometryBudget budget, bool publicationAllowed,
    long maximumEntityBytes = DocumentIntelligenceOptions.DefaultMaxResponseBytes)
{
    internal const long MaximumEntityBytes = DocumentIntelligenceOptions.DefaultMaxResponseBytes;
    internal const int MaximumJsonBytes = 8 * 1024 * 1024;
    internal const int MaximumGzipBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    internal DoclingInputGeometrySnapshot Data { get; } = new()
    {
        Attempt = attempt,
        Reason = publicationAllowed ? null : "canonical_artifacts_disabled",
        Limits = new() { ResponseEntityBytes = ValidateEntityLimit(maximumEntityBytes) }
    };
    internal DoclingInputGeometryBudget Budget { get; } = budget;

    private bool frozen;
    private readonly HashSet<(int Table, int Page, int Provenance, int Cell)> linkKeys = [];
    private bool Active => Data.Reason is null && !frozen;

    internal string DebugSnapshotJson
    {
        get
        {
            Data.ObserverMilliseconds = Budget.Milliseconds;
            return JsonSerializer.Serialize(Data, Json);
        }
    }

    internal async Task SerializePdfAsync(FileStream source, Stream output, CancellationToken ct)
    {
        source.Position = 0;
        if (!Active)
        {
            await source.CopyToAsync(output, 128 * 1024, ct);
            return;
        }

        Data.Serializations++;
        Data.SerializedPdfSha256 = null;
        Data.SerializedPdfBytes = null;
        if (Data.Serializations != 1)
            Refuse("ambiguous_upload_serialization");
        if (!Active)
        {
            await source.CopyToAsync(output, 128 * 1024, ct);
            return;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long count = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            count = checked(count + read);
            if (Active)
                Work(() => hash.AppendData(buffer, 0, read), ct);
        }

        if (Active)
            Work(() =>
            {
                Data.SerializedPdfSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                Data.SerializedPdfBytes = count;
            }, ct);
    }

    internal void ObserveResponse(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Data.ResponseBytes = bytes.Length;
        if (!Active)
            return;
        if (Data.SerializedPdfSha256 is null)
        {
            Refuse("upload_not_serialized", "unavailable");
            return;
        }

        // The primary transport has already admitted these exact bytes. Large
        // embedded PNG strings are skipped by the token scanner, not copied
        // into the bounded geometry payload or rewritten before observation.
        if (bytes.Length > Data.Limits.ResponseEntityBytes)
        {
            Refuse("geometry_entity_limit");
            return;
        }

        Work(() =>
        {
            Data.ResponseSha256 = HashWithinBudget(bytes, ct);
            new DoclingInputGeometryScanner(this, ct).Scan(bytes.Span);
            Data.Availability = "observed";
        }, ct);
    }

    internal void Link(int tableIndex, DoclingTableItem item, int provenanceIndex, CanonicalPage page, int cellIndex, string tableId, CanonicalTableCell cell, DoclingTableCell input, CancellationToken ct)
    {
        if (!Active)
            return;
        Work(() =>
        {
            if (tableIndex >= Data.Tables.Count || Data.Links.Count >= 100000)
                throw new DoclingInputGeometryRefusal("geometry_link_limit");
            var source = Data.Tables[tableIndex];
            if (cellIndex >= source.Cells.Count || provenanceIndex >= source.Provenance.Count || !source.Values.Fields.TryGetValue("self_ref", out var self) || self.DecodedString != item.SelfRef || Integer(source.Provenance[provenanceIndex], "page_no") != page.PageNumber)
                throw new DoclingInputGeometryRefusal("geometry_projection_binding_invalid");
            var key = page.PageNumber.ToString(CultureInfo.InvariantCulture);
            if (!Data.Pages.TryGetValue(key, out var pageInput) || page.Geometry.Width is not { } width || page.Geometry.Height is not { } height)
                throw new DoclingInputGeometryRefusal("page_binding_inconsistent");
            var bits = new Dictionary<string, string>(StringComparer.Ordinal);
            MatchDouble(pageInput, "size.width", width, bits, "width");
            MatchDouble(pageInput, "size.height", height, bits, "height");
            var inputFields = source.Cells[cellIndex];
            MatchInteger(inputFields, "start_row_offset_idx", input.RowIndex);
            MatchInteger(inputFields, "start_col_offset_idx", input.ColumnIndex);
            MatchInteger(inputFields, "row_span", input.RowSpan);
            MatchInteger(inputFields, "col_span", input.ColumnSpan);
            if (input.BoundingBox is { } box)
            {
                MatchDouble(inputFields, "bbox.l", box.Left, bits, "l");
                MatchDouble(inputFields, "bbox.t", box.Top, bits, "t");
                MatchDouble(inputFields, "bbox.r", box.Right, bits, "r");
                MatchDouble(inputFields, "bbox.b", box.Bottom, bits, "b");
                if (!inputFields.Fields.TryGetValue("bbox.coord_origin", out var origin) || origin.DecodedString != box.CoordinateOrigin)
                    throw new DoclingInputGeometryRefusal("geometry_projection_binding_invalid");
            }
            else if (cell.Polygon is not null || inputFields.Fields.Keys.Any(k => k.StartsWith("bbox.", StringComparison.Ordinal)))
                throw new DoclingInputGeometryRefusal("geometry_projection_binding_invalid");
            if (!linkKeys.Add((tableIndex, page.PageNumber, provenanceIndex, cellIndex)))
                throw new DoclingInputGeometryRefusal("geometry_projection_binding_duplicate");
            Data.Links.Add(new(tableIndex, tableId, page.PageNumber, provenanceIndex, cellIndex, cell.CellId, Sha(Encoding.UTF8.GetBytes(input.Text)), input.BoundingBox?.CoordinateOrigin, bits, cell.Polygon?.Points.Select(p => new[] { Bits(p.X), Bits(p.Y) }).ToArray()));
        }, ct);
    }

    internal DoclingInputGeometryArtifact? Prepare(Guid tenant, Guid document, Guid revision, Guid run, string expectedSource, long sourceSize, string codeRevision, string deploymentRevision, DoclingConvertResponse response, CancellationToken ct)
    {
        if (!Active)
            return null;
        DoclingInputGeometryArtifact? prepared = null;
        Work(() =>
        {
            if (!Data.SourceInventoryComplete || Data.Availability != "observed")
                throw new DoclingInputGeometryRefusal("geometry_inventory_unavailable");
            if (!string.Equals(Data.SerializedPdfSha256, expectedSource, StringComparison.OrdinalIgnoreCase) || Data.SerializedPdfBytes != sourceSize)
                throw new DoclingInputGeometryRefusal("serialized_source_identity_mismatch");
            var expectedLinks = Data.Tables.Sum(t => (long)t.Cells.Count * t.Provenance.Count);
            if (expectedLinks != Data.Links.Count)
                throw new DoclingInputGeometryRefusal("geometry_projection_incomplete");
            Data.TenantId = tenant;
            Data.DocumentId = document;
            Data.RevisionId = revision;
            Data.ProcessingRunId = run;
            Data.ExpectedSourceSha256 = expectedSource.ToLowerInvariant();
            Data.BackendCodeRevision = codeRevision;
            Data.ConfiguredDeploymentRevision = deploymentRevision;
            Data.ReportedPackageVersions = new(StringComparer.Ordinal);
            foreach (var package in new[]
            {
                "docling-serve",
                "docling-slim",
                "docling-parse",
                "docling-ibm-models"
            }

            )
                if (DoclingExecutionMetadataPolicy.Package(response, package)is { } version)
                    Data.ReportedPackageVersions[package] = version;
            Data.ObserverMilliseconds = Budget.Milliseconds;
            byte[] json;
            using (var output = new LimitedStream(MaximumJsonBytes, Budget, ct))
            {
                JsonSerializer.Serialize(output, Data, Json);
                json = output.ToArray();
            }

            byte[] compressed;
            using (var output = new LimitedStream(MaximumGzipBytes, Budget, ct))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(json);
                compressed = output.ToArray();
            }

            prepared = new(tenant, document, revision, run, expectedSource.ToLowerInvariant(), Sha(json), json, compressed);
        }, ct);
        if (!Active)
            return null;
        frozen = true; // Budget admission closes before manifest creation and SQL.
        return prepared;
    }

    internal IngestionStageManifest CreateStage() => new()
    {
        StageId = "docling_input_geometry",
        StageType = "diagnostic",
        Engine = "saaia-json-token-observer",
        EngineVersion = "1",
        OptionsSha256 = CanonicalContractJson.ComputeSha256(Data.Limits),
        DeviceId = "cpu:0",
        DurationMs = (long)Budget.Milliseconds,
        Attributes = new(StringComparer.Ordinal)
        {
            ["availability"] = Data.Availability,
            ["reason"] = Data.Reason ?? "none",
            ["capturePoint"] = Data.CapturePoint,
            ["sourceInventoryComplete"] = Data.SourceInventoryComplete ? "true" : "false",
            ["pagesCaptured"] = Data.Pages.Count.ToString(CultureInfo.InvariantCulture),
            ["tablesCaptured"] = Data.Tables.Count.ToString(CultureInfo.InvariantCulture),
            ["cellsCaptured"] = Data.CellCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            ["linksCaptured"] = Data.Links.Count.ToString(CultureInfo.InvariantCulture),
            ["omittedCount"] = Data.OmittedCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            ["responseSha256"] = Data.ResponseSha256 ?? "unavailable",
            ["responseBytes"] = Data.ResponseBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            ["entityLimitBytes"] = Data.Limits.ResponseEntityBytes.ToString(CultureInfo.InvariantCulture),
            ["serializedPdfSha256"] = Data.SerializedPdfSha256 ?? "unavailable",
            ["observerBudgetScope"] = "pre_manifest_own_work_cumulative_retries",
            ["storageChecksWithin5s"] = "false"
        }
    };
    private void Work(Action action, CancellationToken ct)
    {
        try
        {
            using (Budget.Work(ct))
                action();
            Budget.Check(ct);
        }
        catch (DoclingInputGeometryRefusal e)
        {
            Refuse(e.Message);
        }
        catch (JsonException)
        {
            Refuse("geometry_json_invalid");
        }
        catch (FormatException)
        {
            Refuse("geometry_number_invalid");
        }
        catch (OverflowException)
        {
            Refuse("geometry_number_invalid");
        }
    }

    private void Refuse(string reason, string availability = "refused")
    {
        Data.Availability = availability;
        Data.Reason = reason;
        Data.SourceInventoryComplete = false;
        Data.OmittedCount = null;
    }

    private static long ValidateEntityLimit(long maximumEntityBytes)
        => maximumEntityBytes is >= 1024 * 1024 and <= 2L * 1024 * 1024 * 1024
            ? maximumEntityBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumEntityBytes));

    private static int Integer(DoclingInputGeometryFields fields, string name) => fields.Fields.TryGetValue(name, out var t) && t.Kind == "Number" && int.TryParse(t.RawToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new DoclingInputGeometryRefusal("geometry_projection_binding_invalid");
    private static void MatchInteger(DoclingInputGeometryFields f, string name, int actual)
    {
        if (f.Fields.ContainsKey(name) && Integer(f, name) != actual)
            throw new DoclingInputGeometryRefusal("geometry_projection_binding_invalid");
    }

    private static void MatchDouble(DoclingInputGeometryFields fields, string name, double actual, Dictionary<string, string> bits, string output)
    {
        if (!fields.Fields.TryGetValue(name, out var token) || token.Kind != "Number" || !double.TryParse(token.RawToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || Bits(value) != Bits(actual))
            throw new DoclingInputGeometryRefusal("geometry_numeric_binding_invalid");
        bits.Add(output, Bits(actual));
    }

    internal static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private string HashWithinBudget(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length;)
        {
            Budget.Check(ct);
            var next = NextHashOffset(offset, bytes.Length);
            hash.AppendData(bytes.Span.Slice(offset, next - offset));
            offset = next;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static int NextHashOffset(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, length);
        return checked(offset + Math.Min(64 * 1024, length - offset));
    }

    private sealed class LimitedStream(int maximum, DoclingInputGeometryBudget budget, CancellationToken ct) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Guard(1);
            base.WriteByte(value);
        }

        private void Guard(int count)
        {
            budget.Check(ct);
            if (Length + count > maximum)
                throw new DoclingInputGeometryRefusal("geometry_payload_limit");
        }
    }
}
