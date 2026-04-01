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

    // These fields are intentionally mutable because the WinUI tracker rehydrates
    // and enriches the tracked job after initial creation (doc resolution, status updates).
    // Keeping them init-only breaks the build in HelpAndLocalization.cs where the tracker
    // patches the tracked job state during resynchronization.
    public string Status { get; set; } = "queued";
    public string? DocId { get; set; }
    public string? DocPath { get; set; }
}
