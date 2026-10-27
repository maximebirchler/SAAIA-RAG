namespace SAAIA.Contracts.DocumentIntelligence;

public static partial class CanonicalTableRegionObservationValidator
{
    public static IReadOnlyList<CanonicalValidationIssue> ValidateDocument(CanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new List<CanonicalValidationIssue>();
        var enabled = document.Pages.Count(p => p.TableRegionObservations is not null);
        if (enabled == 0) return issues;
        Check(enabled == document.Pages.Count, "pages", "mixed_enabled_and_legacy", issues);
        var observations = new Dictionary<string, CanonicalTableRegionObservation>(StringComparer.Ordinal);
        var allLineIds = new HashSet<string>(StringComparer.Ordinal);
        var attemptsCount = 0;
        long measuredBytes = 0;
        foreach (var page in document.Pages)
        {
            var set = page.TableRegionObservations;
            if (set is null) continue;
            Check(set.SchemaVersion == "table_region_observation_set_v1"
                && set.Availability is "observed" or "partial" or "unavailable", "set", "set_schema", issues);
            Check(set.Reason is null || Token(set.Reason, 128), "set.reason", "reason", issues);
            if (set.Availability == "unavailable")
            {
                Check(set.Observations is null && set.Attempts is null && Token(set.Reason, 128), "set", "unknown_not_empty", issues);
                continue;
            }
            if (set.Observations is null || set.Attempts is null)
            { Check(false, "set", "observed_arrays_required", issues); continue; }
            Check(set.Availability != "partial" || Token(set.Reason, 128), "set", "partial_reason", issues);
            if (set.Observations.Count > MaximumObservations || set.Attempts.Count > 1024)
            { Check(false, "set", "observation_or_attempt_budget", issues); continue; }
            foreach (var observation in set.Observations)
            {
                if (observation is null) { Check(false, "observations", "required", issues); continue; }
                var found = Id(observation.ObservationId) && observations.TryAdd(observation.ObservationId, observation);
                Check(found && observations.Count <= MaximumObservations, "observations", "observation_identity_or_budget", issues);
                var localIssues = Validate(observation);
                issues.AddRange(localIssues);
                Check(observation.DocumentId == document.DocumentId && observation.RevisionId == document.RevisionId
                    && observation.SourceSha256 == document.Source.Sha256 && observation.PhysicalPage == page.PageNumber,
                    "observations", "document_identity", issues);
                var tables = page.Tables.Where(t => t.TableId == observation.TableId
                    && t.Provenance.Attributes.GetValueOrDefault("doclingSelfRef") == observation.SourceTableRef).ToArray();
                Check(tables.Length == 1, "observations.tableId", "table_reference", issues);
                Check(page.Geometry.Width == observation.CoordinateFrame?.PageWidthPoints
                    && page.Geometry.Height == observation.CoordinateFrame?.PageHeightPoints && page.Geometry.Unit == "pt",
                    "observations.coordinateFrame", "page_frame_mismatch", issues);
                if (tables.Length == 1)
                    Check(EqualPolygon(tables[0].Polygon, observation.Region), "observations.region", "table_region_mismatch", issues);
                if (observation.Lines is not null)
                    foreach (var line in observation.Lines.Take(MaximumLines + 1))
                        Check(line is not null && Id(line.LineId) && allLineIds.Add(line.LineId), "observations.lines", "global_line_identity", issues);
                if (localIssues.Count == 0)
                    measuredBytes += StrictUtf8.GetByteCount(CanonicalContractJson.Serialize(observation))
                        + (observation.PublicationRawBytes?.Length ?? 0);
            }
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var tableAttempts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attempt in set.Attempts)
            {
                attemptsCount++;
                if (attempt is null) { Check(false, "attempts", "required", issues); continue; }
                Check(TableRef(attempt.SourceTableRef) && tableAttempts.Add(attempt.SourceTableRef)
                    && attempt.PhysicalPage == page.PageNumber
                    && page.Tables.Any(t => t.Provenance.Attributes.GetValueOrDefault("doclingSelfRef") == attempt.SourceTableRef),
                    "attempts", "attempt_scope", issues);
                Check(attempt.Reason is null || Token(attempt.Reason, 128), "attempts.reason", "reason", issues);
                Check(attempt.Status is "completed" or "not_attempted" or "timeout" or "error" or "refused",
                    "attempts.status", "status", issues);
                if (attempt.Status == "completed")
                {
                    var candidates = set.Observations.Where(o => o is not null && o.ObservationId == attempt.ObservationId).Take(2).ToArray();
                    var observation = candidates.Length == 1 ? candidates[0] : null;
                    Check(attempt.Attempted && observation is not null && completed.Add(attempt.ObservationId!)
                        && observation.SourceTableRef == attempt.SourceTableRef && observation.PhysicalPage == attempt.PhysicalPage
                        && observation.RawObservationSha256 == attempt.RawObservationSha256,
                        "attempts", "completed_observation_binding", issues);
                }
                else
                    Check(attempt.ObservationId is null
                        && (attempt.RawObservationSha256 is null || attempt.Status == "refused" && Sha(attempt.RawObservationSha256))
                        && Token(attempt.Reason, 128)
                        && (attempt.Status != "not_attempted" || !attempt.Attempted), "attempts", "unobserved_not_completed", issues);
            }
            Check(completed.SetEquals(set.Observations.Where(o => o is not null).Select(o => o.ObservationId)), "attempts", "observation_attempt_bijection", issues);
            Check(set.Availability != "observed" || set.Attempts.Count == page.Tables.Count
                && set.Attempts.All(a => a is not null && a.Status == "completed"), "set", "incomplete_observed_set", issues);
        }
        Check(attemptsCount <= 1024, "attempts", "document_attempt_budget", issues);
        Check(measuredBytes <= MaximumDocumentBytes, "payload", "document_byte_budget", issues);
        return issues;
    }

    internal static bool EqualPolygon(CanonicalPolygon? left, CanonicalPolygon? right)
        => left is not null && right is not null && left.Points.Count == right.Points.Count
           && left.Points.Zip(right.Points).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y);
}
