namespace SAAIA.Backend.Models;

/// <summary>
/// Request pour POST /chat/sessions — CDC v2.7
/// userId obligatoire pour scoping multi-user
/// </summary>
public sealed record ChatSessionCreateRequestDto(
    string UserId,
    string? Title = null,
    string? ClientUser = null
);

/// <summary>
/// Response pour POST /chat/sessions — CDC v2.7
/// </summary>
public sealed record ChatSessionDto(
    Guid SessionId,
    string? Title,
    string? ClientUser,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastMessageAt
);

/// <summary>
/// Request pour POST /chat/messages — CDC v2.7
/// userId obligatoire pour validation
/// </summary>
public sealed record ChatMessageCreateRequestDto(
    string UserId,
    string Role,
    string Content,
    string? SourcesJson = null,
    string? StatusNote = null,
    string? ProgressText = null,
    string? TrackingMetaJson = null
);

/// <summary>
/// Response pour POST /chat/messages — CDC v2.7
/// </summary>
public sealed record ChatMessageDto(
    Guid MessageId,
    string Role,
    string Content,
    string? SourcesJson,
    DateTimeOffset CreatedAt,
    string? StatusNote = null,
    string? ProgressText = null,
    string? TrackingMetaJson = null
);


public sealed record ChatMessagePatchRequestDto(
    string UserId,
    string? Content = null,
    string? StatusNote = null,
    string? ProgressText = null,
    string? TrackingMetaJson = null,
    string? SourcesJson = null
);
