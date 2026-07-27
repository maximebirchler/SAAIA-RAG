using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public sealed class CanonicalTable
{
    [JsonPropertyName("tableId")]
    public string TableId { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("columnCount")]
    public int ColumnCount { get; set; }

    [JsonPropertyName("cells")]
    public List<CanonicalTableCell> Cells { get; set; } = [];

    [JsonPropertyName("captionBlockIds")]
    public List<string> CaptionBlockIds { get; set; } = [];

    [JsonPropertyName("provenance")]
    public CanonicalExtractionProvenance Provenance { get; set; } = new();
}

public sealed class CanonicalTableCell
{
    [JsonPropertyName("cellId")]
    public string CellId { get; set; } = "";

    [JsonPropertyName("rowIndex")]
    public int RowIndex { get; set; }

    [JsonPropertyName("columnIndex")]
    public int ColumnIndex { get; set; }

    [JsonPropertyName("rowSpan")]
    public int RowSpan { get; set; } = 1;

    [JsonPropertyName("columnSpan")]
    public int ColumnSpan { get; set; } = 1;

    [JsonPropertyName("cellRole")]
    public string CellRole { get; set; } = "";

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("text")]
    public CanonicalTextVariants Text { get; set; } = new();

    [JsonPropertyName("blockIds")]
    public List<string> BlockIds { get; set; } = [];

    [JsonPropertyName("spanIds")]
    public List<string> SpanIds { get; set; } = [];
}

public sealed class CanonicalFigure
{
    [JsonPropertyName("figureId")]
    public string FigureId { get; set; } = "";

    [JsonPropertyName("figureType")]
    public string FigureType { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("captionBlockIds")]
    public List<string> CaptionBlockIds { get; set; } = [];

    [JsonPropertyName("assetIds")]
    public List<string> AssetIds { get; set; } = [];

    [JsonPropertyName("alternativeText")]
    public string? AlternativeText { get; set; }

    [JsonPropertyName("provenance")]
    public CanonicalExtractionProvenance Provenance { get; set; } = new();
}

public sealed class CanonicalRelation
{
    [JsonPropertyName("relationId")]
    public string RelationId { get; set; } = "";

    [JsonPropertyName("relationType")]
    public string RelationType { get; set; } = "";

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("provenance")]
    public CanonicalExtractionProvenance Provenance { get; set; } = new();
}
