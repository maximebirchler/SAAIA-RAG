using System.Text.Json;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async Task PersistAssistantMessageWithSourcesAsync(
        string sessionId,
        ChatMessageItem message,
        object? sources,
        CancellationToken cancellationToken)
    {
        await _advancedAnalysisPersistenceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ChatMessageItem? persisted;
            if (string.IsNullOrWhiteSpace(message.MessageId))
            {
                persisted = await _api.AddMessageAsync(
                        sessionId,
                        "assistant",
                        message.Content ?? string.Empty,
                        sources,
                        cancellationToken,
                        message.StatusNote,
                        message.ProgressText)
                    .ConfigureAwait(false);
                if (persisted is null || string.IsNullOrWhiteSpace(persisted.MessageId))
                {
                    throw new InvalidDataException(
                        "The durable assistant message was not returned by the chat API.");
                }

                await RunOnUiThreadAsync(() => message.MessageId = persisted.MessageId)
                    .ConfigureAwait(false);
            }
            else
            {
                persisted = await _api.PatchMessageWithSourcesAsync(
                        message.MessageId,
                        message.Content ?? string.Empty,
                        sources,
                        message.StatusNote,
                        message.ProgressText,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (persisted is null)
                {
                    throw new InvalidDataException(
                        "The patched assistant message was not returned by the chat API.");
                }
            }
        }
        finally
        {
            _advancedAnalysisPersistenceGate.Release();
        }
    }

    private async Task PersistAdvancedAnalysisSnapshotAsync(
        string sessionId,
        ChatMessageItem message,
        AdvancedAnalysisJobDto job,
        string outcome,
        string? answer,
        object? sources,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId)
            || parsedSessionId != job.SessionId)
            throw new InvalidDataException("Advanced-analysis snapshot session mismatch.");
        if (sources is null)
            throw new InvalidDataException("Advanced-analysis snapshot sources are required.");

        var pretty = JsonSerializer.Serialize(
            sources,
            new JsonSerializerOptions { WriteIndented = true });
        await RunOnUiThreadAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(answer))
                message.Content = answer;
            message.SourcesJson = pretty;
            if (job.Status is "succeeded" or "failed" or "canceled")
            {
                message.IsStreaming = false;
                message.ProgressText = null;
            }

            if (string.Equals(_sessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                SourcesCards.Items = SourceCardParser.Parse(pretty);
                SourcesBox.Text = pretty;
            }
        }).ConfigureAwait(false);

        // The snapshot must reach the chat store before the polling loop can move on.
        // Cancellation of a local turn must not create a gap between job creation and
        // the first durable resume token.
        await PersistAssistantMessageWithSourcesAsync(
                sessionId,
                message,
                sources,
                CancellationToken.None)
            .ConfigureAwait(false);

        ClientLog.Info(
            "Advanced analysis snapshot persisted: " +
            $"jobId={job.JobId:D}|status={job.Status}|revision={job.Revision}|outcome={outcome}");
    }

    private void StartAdvancedAnalysisTrackersForCurrentSession()
    {
        if (!Guid.TryParse(_sessionId, out var sessionId) || sessionId == Guid.Empty)
            return;

        foreach (var message in _messages.Where(static item =>
                     string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AdvancedAnalysisPersistedStateParser.TryParse(
                    message.SourcesJson,
                    sessionId,
                    out var state)
                || state is null
                || state.IsTerminal)
            {
                continue;
            }

            StartAdvancedAnalysisTracker(message, state);
        }
    }

    private void StartAdvancedAnalysisTracker(
        ChatMessageItem message,
        AdvancedAnalysisPersistedState initialState)
    {
        CancellationTokenSource cancellation;
        lock (_advancedAnalysisTrackerGate)
        {
            if (_advancedAnalysisTrackers.ContainsKey(initialState.JobId))
                return;
            cancellation = new CancellationTokenSource();
            _advancedAnalysisTrackers.Add(initialState.JobId, cancellation);
        }

        _ = TrackAdvancedAnalysisMessageAsync(message, initialState, cancellation);
    }

    private async Task TrackAdvancedAnalysisMessageAsync(
        ChatMessageItem message,
        AdvancedAnalysisPersistedState initialState,
        CancellationTokenSource cancellation)
    {
        try
        {
            var minimumRevision = initialState.Revision;
            var orchestrator = new ToolAgentOrchestrator(
                _api,
                null!,
                new ToolMemory(),
                _appSettings.Clone());

            while (!cancellation.IsCancellationRequested)
            {
                var result = await orchestrator.ResumeAdvancedAnalysisJobAsync(
                        initialState.SessionId,
                        initialState.HandoffId,
                        initialState.JobId,
                        minimumRevision,
                        UiLang,
                        cancellation.Token,
                        onSnapshot: async (snapshot, token) =>
                        {
                            if (snapshot.Job is null)
                                return;
                            minimumRevision = Math.Max(
                                minimumRevision,
                                snapshot.Job.Revision);
                            await PersistAdvancedAnalysisSnapshotAsync(
                                    initialState.SessionId.ToString("D"),
                                    message,
                                    snapshot.Job,
                                    snapshot.Outcome,
                                    snapshot.FinalAnswer,
                                    snapshot.SourcesPayload,
                                    token)
                                .ConfigureAwait(false);
                        })
                    .ConfigureAwait(false);

                if (result.Job is { Status: "succeeded" or "failed" or "canceled" })
                    break;
                if (string.Equals(result.Outcome, "server_rejected", StringComparison.Ordinal))
                    break;

                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Window shutdown only stops local polling. The server job remains durable.
        }
        catch (Exception exception)
        {
            ClientLog.Exception("AdvancedAnalysis.RestartTracker", exception);
        }
        finally
        {
            lock (_advancedAnalysisTrackerGate)
            {
                if (_advancedAnalysisTrackers.TryGetValue(
                        initialState.JobId,
                        out var active)
                    && ReferenceEquals(active, cancellation))
                {
                    _advancedAnalysisTrackers.Remove(initialState.JobId);
                }
            }

            cancellation.Dispose();
        }
    }

    private void StopAdvancedAnalysisTrackers()
    {
        CancellationTokenSource[] trackers;
        lock (_advancedAnalysisTrackerGate)
        {
            trackers = _advancedAnalysisTrackers.Values.ToArray();
            _advancedAnalysisTrackers.Clear();
        }

        foreach (var tracker in trackers)
        {
            try
            {
                tracker.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
