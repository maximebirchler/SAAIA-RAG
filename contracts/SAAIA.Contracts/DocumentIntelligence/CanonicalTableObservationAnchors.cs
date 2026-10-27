using System.Security.Cryptography;
using System.Text;

namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>Secondary literal line anchors. The caller validates the document once, not once per line.</summary>
public static class CanonicalTableObservationAnchors
{
    public const string ProjectionType = "table_region_observation";
    public const string Precision = "table_region_observation_line";

    public static IReadOnlyList<SourceAnchor> Build(CanonicalDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<SourceAnchor>();
        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        var observations = document.Pages.SelectMany(page => page.TableRegionObservations?.Observations ?? []).ToArray();
        if (observations.Length > 8)
            throw new InvalidDataException("table_observation_anchor_observation_limit");
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.DocumentId != document.DocumentId || observation.RevisionId != document.RevisionId
                || !string.Equals(observation.SourceSha256, document.Source.Sha256, StringComparison.OrdinalIgnoreCase)
                || observation.Lines.Count > 5_000)
                throw new InvalidDataException("table_observation_anchor_scope_invalid");
            foreach (var line in observation.Lines.OrderBy(line => line.RawOrdinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line.LineId) || !lineIds.Add(line.LineId)
                    || !string.Equals(Hash(line.Text), line.TextSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("table_observation_anchor_line_invalid");
                result.Add(new SourceAnchor
                {
                    AnchorId = BuildAnchorId(observation, line, document.ManifestSha256),
                    DocumentId = document.DocumentId, RevisionId = document.RevisionId,
                    SourceSha256 = document.Source.Sha256, ManifestSha256 = document.ManifestSha256,
                    ProjectionType = ProjectionType, ProjectionId = line.LineId, Precision = Precision,
                    Regions = [new SourceAnchorRegion
                    {
                        PageNumber = observation.PhysicalPage, Polygon = line.Polygon,
                        RawTextSha256 = line.TextSha256, TableRegionObservationId = observation.ObservationId,
                        TableRegionObservationLineIds = [line.LineId]
                    }]
                });
            }
        }
        return result;
    }

    public static string BuildAnchorId(CanonicalTableRegionObservation observation,
        CanonicalTableRegionObservationLine line, string manifestSha256)
        => "table-observation-anchor-" + Hash(string.Join("|", ProjectionType,
            observation.DocumentId.ToString("D"), observation.RevisionId.ToString("D"), observation.SourceSha256.ToLowerInvariant(),
            manifestSha256.ToLowerInvariant(), observation.PhysicalPage, observation.ObservationId, line.LineId,
            line.TextSha256.ToLowerInvariant()));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
