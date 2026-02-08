using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Models;

public sealed record RagMatch(
    double Score,
    string? DocId,
    string? DocPath,
    string? DocName,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    int? ChunkIndex,
    string? Text
);

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
    int QdrantStatus,
    Timings Timings,
    IReadOnlyList<RagMatch> Matches
);

public sealed record Timings(long TotalMs, long TeiMs, long QdrantMs);

public sealed record CreateSessionResponse(
    string SessionId,
    string? Title,
    string? ClientUser,
    string CreatedAtUtc
);

public sealed record AddMessageResponse(
    string MessageId,
    string CreatedAtUtc
);

public sealed class ChatMessageItem
{
    public string Role { get; set; } = "user"; // user|assistant|system|tool
    public string Content { get; set; } = "";
    public string? SourcesJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
