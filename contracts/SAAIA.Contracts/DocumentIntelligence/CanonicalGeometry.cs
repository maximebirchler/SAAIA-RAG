using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>
/// A point in the normalized, top-left-origin coordinate space of a page.
/// Values are expressed in the inclusive [0, 1] range.
/// </summary>
public sealed class CanonicalPoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public sealed class CanonicalPolygon
{
    [JsonPropertyName("points")]
    public List<CanonicalPoint> Points { get; set; } = [];
}

public sealed class CanonicalPageGeometry
{
    [JsonPropertyName("width")]
    public double? Width { get; set; }

    [JsonPropertyName("height")]
    public double? Height { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("rotationDegrees")]
    public int RotationDegrees { get; set; }

    [JsonPropertyName("coordinateSpace")]
    public string CoordinateSpace { get; set; } = CanonicalSchema.NormalizedTopLeftCoordinateSpace;
}
