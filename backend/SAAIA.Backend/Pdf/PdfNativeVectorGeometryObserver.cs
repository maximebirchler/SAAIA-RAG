using SAAIA.Contracts.DocumentIntelligence;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Core;

internal sealed record PdfNativeVectorObservation(string SourceSha256, int PhysicalPage,
    double PageWidthPoints, double PageHeightPoints, PdfNativeCoordinateFrame Frame,
    string Availability, string? Reason, IReadOnlyList<CanonicalTableVectorRule> Rules, int InspectedCommandCount)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PdfNativeVectorDiagnostics? Diagnostics { get; init; }
}

/// <summary>Bounded source primitives, not a reconstructed grid or a proof of painted visibility.</summary>
internal static class PdfNativeVectorGeometryObserver
{
    internal const int MaximumPathsPerPage=2_000;
    internal const int MaximumCommandsPerPage=10_000;
    internal const int MaximumCommandsPerDocument=100_000;
    internal static PdfNativeVectorObservation Observe(Page page, Structure? structure, string sourceSha,
        int remainingDocumentCommands, CancellationToken ct, int maximumPaths=MaximumPathsPerPage,
        int maximumCommands=MaximumCommandsPerPage)
    {
        ct.ThrowIfCancellationRequested();
        var frame=PdfNativeCoordinateFrame.Observe(page,structure,ct);
        var inspected=0;
        var diagnostics = new PdfNativeVectorDiagnosticCollector();
        PdfNativeVectorObservation Result(string availability,string? reason,IReadOnlyList<CanonicalTableVectorRule>? rules=null)
            =>new(sourceSha,page.Number,page.Width,page.Height,frame,availability,reason,rules??[],inspected)
            { Diagnostics = diagnostics.Snapshot() };
        if(frame.Availability!="available") return Result("unavailable","native_coordinate_frame_unavailable");
        if(frame.RotationDegrees!=0) return Result("unavailable","unsupported_native_rotation");
        if(frame.CropBoxLeft!=0 || frame.CropBoxBottom!=0 || frame.MediaBoxLeft!=0 || frame.MediaBoxBottom!=0)
            return Result("unavailable","unsupported_native_origin");
        if(!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width<=0 || page.Height<=0)
            return Result("unavailable","invalid_native_page_size");
        if(remainingDocumentCommands<=0 || maximumCommands<=0 || maximumPaths<=0)
            return Result("refused","document_vector_command_limit");
        var limit=Math.Min(remainingDocumentCommands,maximumCommands);
        var rules=new List<CanonicalTableVectorRule>();var partial=false;var pathIndex=0;
        try
        {
            diagnostics.BeginScan();
            foreach(var path in page.Paths)
            {
                ct.ThrowIfCancellationRequested();
                if(pathIndex>=maximumPaths) return Result("refused","page_vector_path_limit");
                var currentPath=pathIndex++;var subIndex=0;
                foreach(var subpath in path)
                {
                    ct.ThrowIfCancellationRequested();
                    var primitive=$"page_{page.Number}_path_{currentPath}_subpath_{subIndex++}";
                    diagnostics.EnterPrimitive();
                    foreach(var command in subpath.Commands)
                    {
                        ct.ThrowIfCancellationRequested();
                        if(inspected==limit) return Result("refused",remainingDocumentCommands<=maximumCommands
                            ?"document_vector_command_limit":"page_vector_command_limit");
                        inspected++;
                    }
                    diagnostics.InspectedPrimitive();
                    if(path.IsClipping) {Reject("clipping_path");continue;}
                    if(!path.IsFilled && !path.IsStroked) continue;
                    if(subpath.Commands.Any(c=>c is not (PdfSubpath.Move or PdfSubpath.Line or PdfSubpath.Close)))
                    {Reject("unsupported_command");continue;}
                    var edges=subpath.Commands.OfType<PdfSubpath.Line>().Select(l=>(l.From,l.To)).ToList();
                    var moves=subpath.Commands.OfType<PdfSubpath.Move>().ToArray();
                    if(moves.Length!=1 || edges.Count==0) {Reject("unsupported_subpath_shape");continue;}
                    if(subpath.Commands.LastOrDefault() is PdfSubpath.Close && edges[^1].To!=moves[0].Location)
                        edges.Add((edges[^1].To,moves[0].Location));
                    if(edges.Any(e=>!Finite(e.From) || !Finite(e.To) || e.From.X!=e.To.X && e.From.Y!=e.To.Y))
                    {Reject("non_finite_or_non_axis_edges");continue;}
                    // Filling implicitly closes a subpath; stroking does not. Recognize
                    // this additional fill-only geometry without extending stroke edges.
                    var implicitFillRectangle = path.IsFilled && !path.IsStroked && IsImplicitFillRectangle(subpath);
                    if(path.IsFilled && (subpath.IsDrawnAsRectangle && edges.Count==4 || implicitFillRectangle))
                    {
                        var points=edges.SelectMany(e=>new[]{e.From,e.To}).Distinct().ToArray();
                        if(points.Length==4)
                        {
                            var left=points.Min(p=>p.X);var right=points.Max(p=>p.X);
                            var bottom=points.Min(p=>p.Y);var top=points.Max(p=>p.Y);
                            var width=right-left;var height=top-bottom;
                            if(height is >0 and <=2 && width>=10*height)
                            {Add("h",page.Height-(bottom+top)/2,left,right,height,primitive,subpath);continue;}
                            if(width is >0 and <=2 && height>=10*width)
                            {Add("v",(left+right)/2,page.Height-top,page.Height-bottom,width,primitive,subpath);continue;}
                        }
                    }
                    if(!path.IsStroked) {if(!subpath.IsDrawnAsRectangle && !implicitFillRectangle) Reject("unsupported_filled_shape");continue;}
                    if(path.LineDashPattern is {Array.Count:>0} || path.LineCapStyle!=LineCapStyle.Butt)
                    {Reject("dash_or_cap");continue;}
                    var thickness=path.LineWidth;
                    if(!double.IsFinite(thickness) || thickness<=0) {Reject("invalid_stroke_width");continue;}
                    if(thickness>2) continue;
                    foreach(var edge in edges)
                    {
                        ct.ThrowIfCancellationRequested();
                        if(edge.From.Y==edge.To.Y) Add("h",page.Height-edge.From.Y,
                            Math.Min(edge.From.X,edge.To.X),Math.Max(edge.From.X,edge.To.X),thickness,primitive,subpath);
                        else if(edge.From.X==edge.To.X) Add("v",edge.From.X,
                            page.Height-Math.Max(edge.From.Y,edge.To.Y),page.Height-Math.Min(edge.From.Y,edge.To.Y),thickness,primitive,subpath);
                    }
                    void Reject(string reason)
                    {
                        partial = true;
                        diagnostics.Reject(primitive, reason, subpath);
                    }
                }
            }
            diagnostics.CompleteScan();
            return Result(partial?"partial":"observed",partial?"unsupported_native_vector_geometry":null,rules);
        }
        catch(OperationCanceledException){throw;}
        catch {return Result("unavailable","native_vector_observation_failed");}

        static bool Finite(PdfPoint p)=>double.IsFinite(p.X)&&double.IsFinite(p.Y);
        void Add(string axis,double coordinate,double start,double end,double thickness,string primitive,PdfSubpath subpath)
        {
            var spanLimit=axis=="h"?page.Width:page.Height;
            var coordinateLimit=axis=="h"?page.Height:page.Width;
            if(start<0 || end>spanLimit || coordinate<0 || coordinate>coordinateLimit)
            {partial=true;diagnostics.Reject(primitive,"outside_page_bounds",subpath);return;}
            if(end-start<10*thickness) return;
            var id=CanonicalStableId.Create("native_vector_rule",sourceSha,primitive,axis,
                coordinate.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
                start.ToString("R",System.Globalization.CultureInfo.InvariantCulture),end.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            rules.Add(new(){RuleId=id,Axis=axis,Coordinate=coordinate,Start=start,End=end,Thickness=thickness,PrimitiveIds=[primitive]});
            diagnostics.RuleEmitted(primitive);
        }
    }

    private static bool IsImplicitFillRectangle(PdfSubpath subpath)
    {
        // The bounded form is exactly one move and three connected lines. Four
        // distinct corners and every cyclic edge must form an axis-aligned box.
        if (subpath.Commands.Count != 4 || subpath.Commands[0] is not PdfSubpath.Move move
            || subpath.Commands[1] is not PdfSubpath.Line first
            || subpath.Commands[2] is not PdfSubpath.Line second
            || subpath.Commands[3] is not PdfSubpath.Line third
            || first.From != move.Location || second.From != first.To || third.From != second.To)
            return false;

        PdfPoint[] corners = [move.Location, first.To, second.To, third.To];
        if (corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            || corners.Distinct().Count() != 4
            || corners.Select(point => point.X).Distinct().Count() != 2
            || corners.Select(point => point.Y).Distinct().Count() != 2)
            return false;

        for (var index = 0; index < corners.Length; index++)
        {
            var from = corners[index];
            var to = corners[(index + 1) % corners.Length];
            if (from == to || from.X != to.X && from.Y != to.Y)
                return false;
        }
        return true;
    }
}
