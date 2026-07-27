using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DoclingConvertResponse
{
    [JsonPropertyName("document")]
    public DoclingExportDocument Document { get; set; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("errors")]
    public List<JsonElement> Errors { get; set; } = [];

    [JsonPropertyName("processing_time")]
    public double ProcessingTimeSeconds { get; set; }

    [JsonPropertyName("timings")]
    public Dictionary<string, DoclingTiming> Timings { get; set; } =
        new(StringComparer.Ordinal);

    [JsonPropertyName("confidence")]
    public DoclingConfidence? Confidence { get; set; }

    [JsonPropertyName("saaia_table_repairs")]
    public List<DoclingTableRepairSummary> TableRepairs { get; set; } = [];
}

internal sealed class DoclingExportDocument
{
    [JsonPropertyName("filename")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("json_content")]
    public DoclingDocument? JsonContent { get; set; }
}

internal sealed class DoclingTiming
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("times")]
    public List<double> Times { get; set; } = [];
}

internal sealed class DoclingConfidence
{
    [JsonPropertyName("parse_score")]
    public double? ParseScore { get; set; }

    [JsonPropertyName("layout_score")]
    public double? LayoutScore { get; set; }

    [JsonPropertyName("table_score")]
    public double? TableScore { get; set; }

    [JsonPropertyName("ocr_score")]
    public double? OcrScore { get; set; }

    [JsonPropertyName("mean_score")]
    public double? MeanScore { get; set; }

    [JsonPropertyName("low_score")]
    public double? LowScore { get; set; }

    [JsonPropertyName("mean_grade")]
    public string? MeanGrade { get; set; }

    [JsonPropertyName("low_grade")]
    public string? LowGrade { get; set; }
}

internal sealed class DoclingDocument
{
    [JsonPropertyName("schema_name")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("body")]
    public DoclingNode Body { get; set; } = new();

    [JsonPropertyName("furniture")]
    public DoclingNode Furniture { get; set; } = new();

    [JsonPropertyName("groups")]
    public List<DoclingGroup> Groups { get; set; } = [];

    [JsonPropertyName("texts")]
    public List<DoclingTextItem> Texts { get; set; } = [];

    [JsonPropertyName("pictures")]
    public List<DoclingPictureItem> Pictures { get; set; } = [];

    [JsonPropertyName("tables")]
    public List<DoclingTableItem> Tables { get; set; } = [];

    [JsonPropertyName("key_value_items")]
    public List<DoclingContentItem> KeyValueItems { get; set; } = [];

    [JsonPropertyName("form_items")]
    public List<DoclingContentItem> FormItems { get; set; } = [];

    [JsonPropertyName("pages")]
    public Dictionary<string, DoclingPage> Pages { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class DoclingNode
{
    [JsonPropertyName("children")]
    public List<DoclingReference> Children { get; set; } = [];
}

internal sealed class DoclingReference
{
    [JsonPropertyName("$ref")]
    public string Ref { get; set; } = "";
}

internal class DoclingContentItem
{
    [JsonPropertyName("self_ref")]
    public string SelfRef { get; set; } = "";

    [JsonPropertyName("parent")]
    public DoclingReference? Parent { get; set; }

    [JsonPropertyName("children")]
    public List<DoclingReference> Children { get; set; } = [];

    [JsonPropertyName("content_layer")]
    public string ContentLayer { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("prov")]
    public List<DoclingProvenance> Provenance { get; set; } = [];

    [JsonPropertyName("captions")]
    public List<DoclingReference> Captions { get; set; } = [];
}

internal sealed class DoclingTextItem : DoclingContentItem
{
    [JsonPropertyName("orig")]
    public string OriginalText { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("level")]
    public int? Level { get; set; }
}

internal sealed class DoclingPictureItem : DoclingContentItem
{
}

internal sealed class DoclingTableItem : DoclingContentItem
{
    [JsonPropertyName("data")]
    public DoclingTableData Data { get; set; } = new();

    [JsonPropertyName("saaia_table_repair")]
    public DoclingTableRepair? TableRepair { get; set; }
}

internal sealed class DoclingGroup : DoclingContentItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class DoclingProvenance
{
    [JsonPropertyName("page_no")]
    public int PageNumber { get; set; }

    [JsonPropertyName("bbox")]
    public DoclingBoundingBox? BoundingBox { get; set; }

    [JsonPropertyName("charspan")]
    public int[] CharacterSpan { get; set; } = [];
}

internal sealed class DoclingBoundingBox
{
    [JsonPropertyName("l")]
    public double Left { get; set; }

    [JsonPropertyName("t")]
    public double Top { get; set; }

    [JsonPropertyName("r")]
    public double Right { get; set; }

    [JsonPropertyName("b")]
    public double Bottom { get; set; }

    [JsonPropertyName("coord_origin")]
    public string CoordinateOrigin { get; set; } = "";
}

internal sealed class DoclingPage
{
    [JsonPropertyName("size")]
    public DoclingPageSize Size { get; set; } = new();

    [JsonPropertyName("page_no")]
    public int PageNumber { get; set; }
}

internal sealed class DoclingPageSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

internal sealed class DoclingTableData
{
    [JsonPropertyName("num_rows")]
    public int RowCount { get; set; }

    [JsonPropertyName("num_cols")]
    public int ColumnCount { get; set; }

    [JsonPropertyName("table_cells")]
    public List<DoclingTableCell> Cells { get; set; } = [];
}

internal sealed class DoclingTableCell
{
    [JsonPropertyName("bbox")]
    public DoclingBoundingBox? BoundingBox { get; set; }

    [JsonPropertyName("row_span")]
    public int RowSpan { get; set; } = 1;

    [JsonPropertyName("col_span")]
    public int ColumnSpan { get; set; } = 1;

    [JsonPropertyName("start_row_offset_idx")]
    public int RowIndex { get; set; }

    [JsonPropertyName("start_col_offset_idx")]
    public int ColumnIndex { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("column_header")]
    public bool IsColumnHeader { get; set; }

    [JsonPropertyName("row_header")]
    public bool IsRowHeader { get; set; }

    [JsonPropertyName("row_section")]
    public bool IsRowSection { get; set; }

    [JsonPropertyName("saaia_original_text")]
    public string? OriginalText { get; set; }

    [JsonPropertyName("saaia_text_repair")]
    public DoclingCellTextRepair? TextRepair { get; set; }
}

internal sealed class DoclingTableRepair
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = "";

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("original_cell_count")]
    public int OriginalCellCount { get; set; }

    [JsonPropertyName("final_cell_count")]
    public int FinalCellCount { get; set; }

    [JsonPropertyName("repaired_cell_count")]
    public int RepairedCellCount { get; set; }

    [JsonPropertyName("added_cell_count")]
    public int AddedCellCount { get; set; }

    [JsonPropertyName("ocr_line_count")]
    public int OcrLineCount { get; set; }

    [JsonPropertyName("mean_confidence")]
    public double? MeanConfidence { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }
}

internal sealed class DoclingCellTextRepair
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = "";

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

internal sealed class DoclingTableRepairSummary
{
    [JsonPropertyName("table_ref")]
    public string TableRef { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("ocr_duration_ms")]
    public int? OcrDurationMs { get; set; }
}
