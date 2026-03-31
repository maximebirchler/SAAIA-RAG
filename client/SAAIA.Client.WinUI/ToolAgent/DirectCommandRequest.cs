using System;
using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class DirectCommandRequest
{
    public required string CommandId { get; init; }
    public string ArgsJson { get; init; } = "{}";
    public string DisplayText { get; init; } = string.Empty;
    public string Language { get; init; } = "fr";
}

public sealed class DirectCommandExecutionResult
{
    public required string FinalAnswer { get; init; }
    public object? SourcesPayload { get; init; }
    public string RouterIntent { get; init; } = string.Empty;
    public IReadOnlyList<string> ToolNames { get; init; } = Array.Empty<string>();
    public DirectCommandTrackedJob? TrackedJob { get; init; }
}

public sealed class DirectCommandTrackedJob
{
    public required string JobId { get; init; }
    public string JobType { get; init; } = "ingestion";
    public string DisplayLabel { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public string? DocId { get; init; }
    public string? DocPath { get; init; }
}
