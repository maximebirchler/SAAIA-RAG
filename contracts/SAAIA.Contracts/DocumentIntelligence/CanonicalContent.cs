using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public sealed class CanonicalTextVariants
{
    [JsonPropertyName("raw")]
    public string Raw { get; set; } = "";

    [JsonPropertyName("canonical")]
    public string Canonical { get; set; } = "";

    [JsonPropertyName("normalized")]
    public string Normalized { get; set; } = "";

    [JsonPropertyName("retrieval")]
    public string Retrieval { get; set; } = "";

    [JsonPropertyName("display")]
    public string Display { get; set; } = "";

    [JsonPropertyName("rawSha256")]
    public string RawSha256 { get; set; } = "";
}

public sealed class CanonicalExtractionProvenance
{
    [JsonPropertyName("stageId")]
    public string StageId { get; set; } = "";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = "";

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("modelSha256")]
    public string? ModelSha256 { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CanonicalWord
{
    [JsonPropertyName("wordId")]
    public string WordId { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class CanonicalSpan
{
    [JsonPropertyName("spanId")]
    public string SpanId { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("text")]
    public CanonicalTextVariants Text { get; set; } = new();

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("words")]
    public List<CanonicalWord> Words { get; set; } = [];

    [JsonPropertyName("style")]
    public CanonicalTextStyle? Style { get; set; }

    [JsonPropertyName("provenance")]
    public CanonicalExtractionProvenance Provenance { get; set; } = new();
}

public sealed class CanonicalTextStyle
{
    [JsonPropertyName("fontFamily")]
    public string? FontFamily { get; set; }

    [JsonPropertyName("fontSize")]
    public double? FontSize { get; set; }

    [JsonPropertyName("bold")]
    public bool? Bold { get; set; }

    [JsonPropertyName("italic")]
    public bool? Italic { get; set; }

    [JsonPropertyName("monospace")]
    public bool? Monospace { get; set; }
}

public sealed class CanonicalBlock
{
    [JsonPropertyName("blockId")]
    public string BlockId { get; set; } = "";

    [JsonPropertyName("blockType")]
    public string BlockType { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("readingOrder")]
    public int ReadingOrder { get; set; }

    [JsonPropertyName("parentBlockId")]
    public string? ParentBlockId { get; set; }

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("text")]
    public CanonicalTextVariants Text { get; set; } = new();

    [JsonPropertyName("spans")]
    public List<CanonicalSpan> Spans { get; set; } = [];

    [JsonPropertyName("childBlockIds")]
    public List<string> ChildBlockIds { get; set; } = [];

    [JsonPropertyName("isRepeatedFurniture")]
    public bool IsRepeatedFurniture { get; set; }

    [JsonPropertyName("qualityFlags")]
    public List<string> QualityFlags { get; set; } = [];

    [JsonPropertyName("provenance")]
    public CanonicalExtractionProvenance Provenance { get; set; } = new();
}
