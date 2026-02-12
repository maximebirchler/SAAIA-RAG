using System;
using System.Collections.Generic;
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
