using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts;

public sealed class DocumentRevisionInfo
{
    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("docId")]
    public Guid DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("ingestionVersion")]
    public int IngestionVersion { get; set; }

    [JsonPropertyName("indexedVersion")]
    public int IndexedVersion { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset PublishedAtUtc { get; set; }
}

public sealed class DocumentProcessingRunInfo
{
    [JsonPropertyName("processingRunId")]
    public Guid ProcessingRunId { get; set; }

    [JsonPropertyName("jobId")]
    public Guid? JobId { get; set; }

    [JsonPropertyName("docId")]
    public Guid DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("revisionId")]
    public Guid? RevisionId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("ingestionVersion")]
    public int IngestionVersion { get; set; }

    [JsonPropertyName("indexedVersionBefore")]
    public int IndexedVersionBefore { get; set; }

    [JsonPropertyName("indexedVersionAfter")]
    public int IndexedVersionAfter { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset? StartedAtUtc { get; set; }

    [JsonPropertyName("finishedAtUtc")]
    public DateTimeOffset FinishedAtUtc { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public sealed class DocumentPageIndexEntry
{
    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("charCount")]
    public int CharCount { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}

public sealed class DocumentSectionInfo
{
    [JsonPropertyName("sectionId")]
    public Guid SectionId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}

public sealed class DocumentUnitInfo
{
    [JsonPropertyName("unitId")]
    public Guid UnitId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sectionId")]
    public Guid? SectionId { get; set; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("charCount")]
    public int CharCount { get; set; }

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}

public sealed class RetrievalChunkInfo
{
    [JsonPropertyName("retrievalChunkId")]
    public Guid RetrievalChunkId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sectionId")]
    public Guid? SectionId { get; set; }

    [JsonPropertyName("unitId")]
    public Guid? UnitId { get; set; }

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}

public sealed class RetrievalChunkLinkInfo
{
    [JsonPropertyName("retrievalChunkId")]
    public Guid RetrievalChunkId { get; set; }

    [JsonPropertyName("linkedChunkId")]
    public Guid LinkedChunkId { get; set; }

    [JsonPropertyName("linkType")]
    public string LinkType { get; set; } = "";
}

public sealed class ExactMatchEntryInfo
{
    [JsonPropertyName("exactMatchEntryId")]
    public Guid ExactMatchEntryId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sectionId")]
    public Guid? SectionId { get; set; }

    [JsonPropertyName("unitId")]
    public Guid? UnitId { get; set; }

    [JsonPropertyName("entryIndex")]
    public int EntryIndex { get; set; }

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("normalizedText")]
    public string NormalizedText { get; set; } = "";

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}

public sealed class ContextualTextEntryInfo
{
    [JsonPropertyName("contextualTextEntryId")]
    public Guid ContextualTextEntryId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sectionId")]
    public Guid? SectionId { get; set; }

    [JsonPropertyName("unitId")]
    public Guid? UnitId { get; set; }

    [JsonPropertyName("retrievalChunkId")]
    public Guid? RetrievalChunkId { get; set; }

    [JsonPropertyName("entryIndex")]
    public int EntryIndex { get; set; }

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}
