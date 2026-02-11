using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Models;

// =====================
// RAG (CDC v2.7) — /rag/search
// =====================

public sealed record RagItem(
    double Score,
    string? DocId,
    string DocName,
    string? DocPath,
    string? Category,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    int? ChunkIndex,
    string Text
);

public sealed record RagMetrics(long TookMs, int Returned);

public sealed record RagSearchResponse(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    RagMetrics Metrics,
    IReadOnlyList<RagItem> Items
);

// =====================
// Chat-store (CDC v2.7)
// =====================

public sealed record CreateSessionResponse(
    string SessionId,
    string? Title,
    string? ClientUser,
    DateTimeOffset CreatedAtUtc
);

public sealed record AddMessageResponse(
    string MessageId,
    DateTimeOffset CreatedAtUtc
);

// =====================
// UI
// =====================

public sealed class ChatMessageItem
{
    public string Role { get; set; } = "user"; // user|assistant|system|tool
    public string Content { get; set; } = "";
    public string? SourcesJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
