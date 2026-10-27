using UglyToad.PdfPig.Core;

internal sealed record PdfNativeVectorRejectionSample(string PrimitiveId, string ReasonCode,
    IReadOnlyList<string> CommandKinds);

/// <summary>Counts work actually inspected, not painted visibility or table-region eligibility.</summary>
internal sealed record PdfNativeVectorDiagnostics(string InspectionCoverage, int? PrimitivesEntered,
    int? PrimitivesInspected, int? SupportedPrimitives, int? RulesEmittedBeforeStatus,
    IReadOnlyDictionary<string, int>? RejectionCounts, IReadOnlyList<PdfNativeVectorRejectionSample> Samples,
    int SamplesOmitted)
{
    public string SchemaVersion => "pdfpig_native_vector_diagnostics_v1";
    public string PaintVisibility => "unknown";
    public string EffectiveClip => "unknown";
}

internal sealed class PdfNativeVectorDiagnosticCollector
{
    internal const int MaximumSamples = 8;
    internal static readonly string[] RejectionCodes = ["clipping_path", "unsupported_command",
        "unsupported_subpath_shape", "non_finite_or_non_axis_edges", "unsupported_filled_shape",
        "dash_or_cap", "invalid_stroke_width", "outside_page_bounds"];
    private readonly Dictionary<string, int> _rejections = new(StringComparer.Ordinal);
    private readonly List<PdfNativeVectorRejectionSample> _samples = [];
    private bool _started;
    private bool _complete;
    private int _entered;
    private int _inspected;
    private int _supported;
    private int _emitted;
    private int _omitted;
    private string? _lastSupportedPrimitive;

    internal void BeginScan() => _started = true;
    internal void EnterPrimitive() => _entered++;
    internal void InspectedPrimitive() => _inspected++;
    internal void CompleteScan() => _complete = true;

    internal void RuleEmitted(string primitive)
    {
        _emitted++;
        if (_lastSupportedPrimitive == primitive) return;
        _lastSupportedPrimitive = primitive;
        _supported++;
    }

    internal void Reject(string primitive, string reason, PdfSubpath subpath)
    {
        _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;
        if (_samples.Count == MaximumSamples) { _omitted++; return; }
        // Fixed labels only: no arbitrary command representations or source text.
        var kinds = subpath.Commands.Select(command => command switch
        {
            PdfSubpath.Move => "move", PdfSubpath.Line => "line", PdfSubpath.Close => "close",
            PdfSubpath.CubicBezierCurve => "cubic_bezier", PdfSubpath.QuadraticBezierCurve => "quadratic_bezier",
            _ => "other"
        }).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        _samples.Add(new(primitive, reason, kinds));
    }

    internal PdfNativeVectorDiagnostics Snapshot() => new(
        !_started ? "unavailable" : _complete ? "complete" : "partial",
        _started ? _entered : null, _started ? _inspected : null, _started ? _supported : null,
        _started ? _emitted : null, _started ? new Dictionary<string, int>(_rejections, StringComparer.Ordinal) : null,
        _samples.ToArray(), _omitted);
}
