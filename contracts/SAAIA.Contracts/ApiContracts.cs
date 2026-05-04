using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts;

// ---------------------
// Chat-store contracts
// ---------------------
public sealed class CreateSessionResponse
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("clientUser")]
    public string? ClientUser { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// ---------------------
// RAG contracts
// ---------------------
public sealed class RagMetrics
{
    [JsonPropertyName("tookMs")]
    public long TookMs { get; set; }

    [JsonPropertyName("returned")]
    public int Returned { get; set; }
}

public sealed class RagItem
{
    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("docId")]
    public string? DocId { get; set; }

    [JsonPropertyName("docName")]
    public string DocName { get; set; } = "";

    [JsonPropertyName("docPath")]
    public string? DocPath { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("pageStart")]
    public int? PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int? PageEnd { get; set; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; set; }

    [JsonPropertyName("chunkIndex")]
    public int? ChunkIndex { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("contextualSnippet")]
    public string? ContextualSnippet { get; set; }
}

public sealed class RagSearchResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("queryNormalized")]
    public string? QueryNormalized { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("topK")]
    public int TopK { get; set; }

    [JsonPropertyName("minScore")]
    public double MinScore { get; set; }

    [JsonPropertyName("candidates")]
    public int Candidates { get; set; }

    [JsonPropertyName("maxPerDoc")]
    public int MaxPerDoc { get; set; }

    [JsonPropertyName("maxPerPage")]
    public int MaxPerPage { get; set; }

    [JsonPropertyName("metrics")]
    public RagMetrics Metrics { get; set; } = new();

    [JsonPropertyName("items")]
    public List<RagItem> Items { get; set; } = new();
}


// ---------------------
// Catalog / capabilities contracts (transition to snapshot + capabilities)
// ---------------------
public sealed class CatalogSnapshotResponse
{
    [JsonPropertyName("snapshotId")]
    public string SnapshotId { get; set; } = "";

    [JsonPropertyName("catalogVersion")]
    public string CatalogVersion { get; set; } = "";

    [JsonPropertyName("etag")]
    public string ETag { get; set; } = "";

    [JsonPropertyName("categories")]
    public List<CatalogSnapshotCategoryItem> Categories { get; set; } = new();

    [JsonPropertyName("totals")]
    public CatalogSnapshotTotals Totals { get; set; } = new();
}

public sealed class CatalogSnapshotCategoryItem
{
    [JsonPropertyName("categoryRef")]
    public string CategoryRef { get; set; } = "";

    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; set; } = "";

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("canonicalName")]
    public string CanonicalName { get; set; } = "";

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();
}

public sealed class CatalogSnapshotTotals
{
    [JsonPropertyName("documents")]
    public long Documents { get; set; }

    [JsonPropertyName("categories")]
    public int Categories { get; set; }
}

public sealed class CatalogDocumentItem
{
    [JsonPropertyName("documentRef")]
    public string DocumentRef { get; set; } = "";

    [JsonPropertyName("docId")]
    public Guid DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("canonicalName")]
    public string CanonicalName { get; set; } = "";

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }

    [JsonPropertyName("categoryCanonicalName")]
    public string? CategoryCanonicalName { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("lastModifiedUtc")]
    public DateTimeOffset? LastModifiedUtc { get; set; }
}

public sealed class CatalogDocumentListResponse
{
    [JsonPropertyName("value")]
    public List<CatalogDocumentItem> Value { get; set; } = new();

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

public sealed class AuthCapabilitiesResponse
{
    [JsonPropertyName("user")]
    public AuthCapabilitiesUser User { get; set; } = new();

    [JsonPropertyName("ui")]
    public AuthCapabilitiesUi Ui { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public AuthCapabilitiesPayload Capabilities { get; set; } = new();
}

public sealed class AuthCapabilitiesUser
{
    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; set; }

    [JsonPropertyName("isAdmin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
}

public sealed class AuthCapabilitiesUi
{
    [JsonPropertyName("defaultLocale")]
    public string DefaultLocale { get; set; } = "fr-CH";
}

public sealed class AuthCapabilitiesPayload
{
    [JsonPropertyName("directCommands")]
    public List<AuthCapabilityCommand> DirectCommands { get; set; } = new();

    [JsonPropertyName("adminCommands")]
    public List<AuthCapabilityCommand> AdminCommands { get; set; } = new();
}

public sealed class AuthCapabilityCommand
{
    [JsonPropertyName("commandId")]
    public string CommandId { get; set; } = "";

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "always";

    [JsonPropertyName("requiresContext")]
    public List<string> RequiresContext { get; set; } = new();

    [JsonPropertyName("argsSchema")]
    public JsonElement ArgsSchema { get; set; }
}

// ---------------------
// Resolve contracts
// ---------------------

public sealed class SourceResolveRequest
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("pdfRef")]
    public string? PdfRef { get; set; }
}

public sealed class ResolvedSourceDto
{
    [JsonPropertyName("docId")]
    public Guid? DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("docName")]
    public string DocName { get; set; } = "";

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; } = 1;

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; } = 1;

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public sealed class SourceResolveResponse
{
    [JsonPropertyName("source")]
    public ResolvedSourceDto? Source { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("requestedRef")]
    public string? RequestedRef { get; set; }
}

public sealed class ResolveCategoryRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }
}

public sealed class ResolvedCategoryItem
{
    [JsonPropertyName("categoryRef")]
    public string CategoryRef { get; set; } = "";

    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int? Ordinal { get; set; }

    [JsonPropertyName("totalDocuments")]
    public int? TotalDocuments { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();
}

public sealed class ResolveCategoryResponse
{
    [JsonPropertyName("items")]
    public List<ResolvedCategoryItem> Items { get; set; } = new();
}
