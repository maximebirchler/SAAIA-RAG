using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private IReadOnlyList<object> BuildStructuredTablesForPrompt(IReadOnlyList<PromptEvidenceItem> evidence)
    {
        foreach (var item in evidence)
        {
            item.VisibleStructuredTableRows = [];
        }
        var maximum = Math.Clamp(_options.MaximumEvidencePromptCharacters, 8_000, 1_000_000);
        var remaining = maximum - JsonSerializer.Serialize(evidence, JsonOptions).Length - 2;
        if (remaining <= 0)
        {
            return [];
        }
        var result = new List<object>();
        var sourceEvidence = evidence.Where(item => item.SourceTableFragment is not null).Select(item =>
            new AdvancedAnalysisResolvedEvidence(item.SourceReference, "", TableFragment: item.SourceTableFragment,
                TableReadWindow: item.TableReadWindow)).ToArray();
        foreach (var group in sourceEvidence.GroupBy(item => (item.Reference.DocId, item.Reference.RevisionId,
                     item.Reference.SourceHash, item.Reference.PageStart, item.TableFragment!.Ordinal)))
        {
            var members = group.ToArray();
            var request = members.LastOrDefault(item => item.TableReadWindow is not null)?.TableReadWindow;
            var views = AdvancedAnalysisStructuredTableAssembler.Build(members, request, remaining);
            foreach (var view in views)
            {
                // Public model projection uses its existing opaque source handle.
                // SQL/observation retains full IDs; never reveal paths or pretend
                // the joined HTML is a new single-chunk citation.
                var source = evidence.FirstOrDefault(item =>
                    item.SourceReference.DocId == view.DocId
                    && item.SourceReference.RevisionId == view.RevisionId
                    && item.SourceReference.SourceHash == view.SourceHash
                    && item.SourceReference.PageStart == view.PhysicalPage
                    && item.SourceReference.PageEnd == view.PhysicalPage
                    && item.SourceTableFragment?.Ordinal == view.TableOrdinal
                    && (view.EvidenceIds.Count == 0
                        || view.EvidenceIds.Contains(item.EvidenceId ?? "", StringComparer.Ordinal)));
                if (source is null)
                {
                    continue;
                }
                var sourceKey = source.SourceKey;
                var compact = new
                {
                    sourceKey, view.PhysicalPage, view.TableOrdinal, view.TotalRows, view.TotalColumns,
                    view.RowIndices, view.Html, view.EvidenceIds, view.Complete, view.NextRowOffset, view.Status,
                    view.CompletenessBasis, view.SourceFidelityValidated, view.QualitySignals,
                    view.MissingRowIndices, view.MissingRowsOmitted,
                    instruction = "Rows joined only from this observed canonical grid. Complete means canonical row coverage, not PDF or visual fidelity. Cite every contributing evidenceId needed by a relationship; do not infer absent rows, cell boundaries, captions or graph edges. Use read_source with tableOrdinal/tableRowOffset/tableRowLimit for another row window."
                };
                var size = JsonSerializer.Serialize(compact, JsonOptions).Length + (result.Count == 0 ? 0 : 1);
                if (size > remaining)
                {
                    break;
                }
                result.Add(compact);
                remaining -= size;
                foreach (var item in evidence.Where(item => view.EvidenceIds.Contains(item.EvidenceId ?? "", StringComparer.Ordinal)))
                {
                    item.VisibleStructuredTableRows = (item.SourceTableFragment?.Rows ?? [])
                        .Where(row => view.RowIndices.Contains(row.Index)).Select(row => row.Xml).ToArray();
                }
            }
        }
        return result;
    }
}
