using System.Text.Json;
using Npgsql;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class AdvancedAnalysisEvidenceResolver
{
    private static async Task<ResolvedEvidenceRow?> ResolveNativeAsync(NpgsqlConnection connection, Guid tenant,
        AdvancedAnalysisEvidenceReference reference, AdvancedAnalysisNativeObservationReader.Cache cache, CancellationToken ct)
    {
        using var detail = NativeReadDiagnostics.Start("native.line_resolve");
        try
        {
            var native = reference.NativeObservation!;
            if (!Guid.TryParse(reference.DocId, out var document) || !Guid.TryParse(reference.RevisionId, out var revision)
                || reference.SourceHash is not { Length: 64 } source || !source.All(char.IsAsciiHexDigit)
                || reference.PageStart != native.PhysicalPage || reference.PageEnd != native.PhysicalPage
                || reference.ChunkId is not null || reference.AnchorId is not null || reference.ContentCardId is not null)
                throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
            var stored = await AdvancedAnalysisNativeObservationReader.LoadAsync(connection, tenant, document,
                revision, source, cache, ct);
            if (stored is null)
            {
                detail?.Complete();
                return null;
            }
            using var phase = detail?.Phase("line_validate");
            var line = stored.Resolve(native, ct);
            var observation = stored.Source(line);
            var row = new ResolvedEvidenceRow
            {
                DocId = document,
                RevisionId = revision,
                SourceHash = stored.Document.Source.Sha256,
                DocPath = stored.DocPath,
                FileName = stored.FileName,
                PageStart = native.PhysicalPage,
                PageEnd = native.PhysicalPage,
                Content = line.Text,
                NativeObservation = observation,
                QualitySignalsJson = JsonSerializer.Serialize(observation.QualitySignals),
                ChunkType = "canonical_native_audit_line"
            };
            phase?.Complete();
            detail?.Complete();
            return row;
        }
        catch (Exception error)
        {
            detail?.Fail(error);
            throw;
        }
    }
}
