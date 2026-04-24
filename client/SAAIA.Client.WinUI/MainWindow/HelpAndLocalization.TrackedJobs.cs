using System.Net;
using System.Net.Http;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private void StartDirectCommandJobTrackingLoop(ActiveDirectCommandTrackerState state)
    {
        if (state.IsTerminal || state.TrackingLoopStarted || state.Cancellation.IsCancellationRequested)
            return;

        state.TrackingLoopStarted = true;
        _ = TrackDirectCommandJobAsync(state);
    }

    private async Task StartAndRefreshDirectCommandJobTrackerAsync(DirectCommandTrackedJob trackedJob, ChatMessageItem assistantMsg, string? sessionId)
    {
        var state = StartDirectCommandJobTracker(trackedJob, assistantMsg, sessionId, startLoop: false);
        if (state is null)
            return;

        await RefreshTrackedJobStateNowAsync(state, CancellationToken.None, persist: true).ConfigureAwait(false);
        StartDirectCommandJobTrackingLoop(state);
    }

    private async Task RehydrateTrackedJobsForCurrentSessionAsync(bool refreshBeforeLoop = true)
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || _messages.Count == 0)
            return;

        foreach (var msg in _messages)
        {
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (msg.TrackingMeta?.IsTerminal == true)
                continue;
            if (!TryCreateTrackedJobFromMessage(msg, out var trackedJob))
                continue;

            var state = StartDirectCommandJobTracker(trackedJob, msg, _sessionId, startLoop: false);
            if (state is null)
                continue;

            if (refreshBeforeLoop)
                await RefreshTrackedJobStateNowAsync(state, CancellationToken.None, persist: true).ConfigureAwait(false);

            StartDirectCommandJobTrackingLoop(state);
            await Task.Yield();
        }
    }

    private async Task PreRefreshTrackedMessagesAsync(IReadOnlyList<ChatMessageItem> messages, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || messages.Count == 0)
            return;

        foreach (var msg in messages)
        {
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (msg.TrackingMeta?.IsTerminal == true)
                continue;
            if (!TryCreateTrackedJobFromMessage(msg, out var trackedJob))
                continue;

            var state = StartDirectCommandJobTracker(trackedJob, msg, sessionId, startLoop: false);
            if (state is null)
                continue;

            await RefreshTrackedJobStateNowAsync(state, ct, persist: false).ConfigureAwait(false);
            await Task.Yield();
        }
    }

    private void RebindDirectCommandTrackersForCurrentSession()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || _messages.Count == 0)
            return;

        List<ActiveDirectCommandTrackerState> states;
        lock (_directCommandTrackerGate)
        {
            states = _activeDirectCommandTrackers.Values
                .Where(x => !x.IsTerminal && string.Equals(x.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var state in states)
        {
            var msg = FindTrackedJobMessageForCurrentSession(state.MessageId, state.Job.DisplayLabel);
            if (msg is null)
                continue;

            // Important: at session reload time, the message has already been loaded from the backend
            // and may have been pre-refreshed against /chat/messages/{id}/tracking.
            // Rebinding must not overwrite that freshly reloaded backend truth with an older in-memory snapshot.
            state.Message = msg;
            state.MessageId = msg.MessageId;
        }
    }

    private ChatMessageItem? FindTrackedJobMessageForCurrentSession(string? messageId, string displayLabel)
    {
        if (_messages.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            var byId = _messages.FirstOrDefault(m => string.Equals(m.MessageId, messageId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var msg = _messages[i];
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(msg.TrackingMeta?.DisplayLabel)
                && string.Equals(msg.TrackingMeta.DisplayLabel, displayLabel, StringComparison.OrdinalIgnoreCase))
                return msg;
        }

        if (string.IsNullOrWhiteSpace(displayLabel))
            return null;

        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var msg = _messages[i];
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;
            if (msg.Content.IndexOf(displayLabel, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (msg.Content.IndexOf("réindex", StringComparison.OrdinalIgnoreCase) < 0
                && msg.Content.IndexOf("reindex", StringComparison.OrdinalIgnoreCase) < 0
                && msg.Content.IndexOf("index", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            return msg;
        }

        return null;
    }

    private void ApplyTrackedJobSnapshotToMessage(ActiveDirectCommandTrackerState state, ChatMessageItem assistantMsg)
    {
        EnsureTrackedJobStateRenderable(state);

        if (!string.IsNullOrWhiteSpace(state.LastContent))
            assistantMsg.Content = state.LastContent;
        assistantMsg.ProgressText = state.LastProgressText;
        assistantMsg.StatusNote = state.LastStatusNote;
        if (!string.IsNullOrWhiteSpace(state.MessageId))
            assistantMsg.MessageId = state.MessageId;
        assistantMsg.TrackingMeta = BuildTrackedJobMeta(state);
    }

    private void EnsureTrackedJobStateRenderable(ActiveDirectCommandTrackerState state)
    {
        var status = (state.LastKnownStatus ?? state.Job.Status ?? string.Empty).Trim().ToLowerInvariant();
        var displayPercent = GetTrackedJobDisplayPercent(status, state.LastKnownProgressPhase, state.LastKnownProgressPercent, state.LastKnownProgressCurrent, state.LastKnownProgressTotal);
        var startedAtUtc = state.StartedAtUtc ?? state.LastSnapshotAtUtc ?? DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));

        if (state.IsTerminal)
        {
            if (status is "failed" or "error" or "canceled" or "cancelled")
                state.LastContent = DeterministicAgentText.AdminReindexFailed(UiLang, state.Job.DisplayLabel, state.Job.Status);
            else
                state.LastContent = DeterministicAgentText.AdminReindexCompleted(UiLang, state.Job.DisplayLabel);

            state.LastProgressText = null;
            state.LastStatusNote = null;
            return;
        }

        if (status == "queued")
        {
            state.LastContent = DeterministicAgentText.AdminReindexQueued(UiLang, state.Job.DisplayLabel, state.Job.JobId);
            state.LastProgressText ??= DeterministicAgentText.AdminJobQueued(UiLang, state.Job.DisplayLabel, elapsedSeconds);
            return;
        }

        if (displayPercent.HasValue)
            state.LastContent = DeterministicAgentText.AdminReindexRunningWithPercent(UiLang, state.Job.DisplayLabel, displayPercent.Value);
        else
            state.LastContent ??= DeterministicAgentText.AdminReindexRunning(UiLang, state.Job.DisplayLabel);

        var hasStructuredProgress = !string.IsNullOrWhiteSpace(state.LastKnownProgressPhase)
            || state.LastKnownProgressCurrent.HasValue
            || state.LastKnownProgressTotal.HasValue
            || state.LastKnownProgressPercent.HasValue;

        if (hasStructuredProgress || string.IsNullOrWhiteSpace(state.LastProgressText))
        {
            state.LastProgressText = DeterministicAgentText.AdminReindexProgressPhase(
                UiLang,
                state.LastKnownProgressPhase,
                displayPercent,
                state.LastKnownProgressCurrent,
                state.LastKnownProgressTotal,
                elapsedSeconds);
        }
    }

    private void SeedTrackedJobStateFromMessage(ActiveDirectCommandTrackerState state, ChatMessageItem assistantMsg)
    {
        state.LastContent ??= assistantMsg.Content;
        state.LastProgressText ??= assistantMsg.ProgressText;
        state.LastStatusNote ??= assistantMsg.StatusNote;
        state.LastPersistedContent ??= assistantMsg.Content;
        state.LastPersistedProgressText ??= assistantMsg.ProgressText;
        state.LastPersistedStatusNote ??= assistantMsg.StatusNote;
        state.LastPersistedTrackingMetaJson ??= assistantMsg.TrackingMeta is null ? null : JsonSerializer.Serialize(assistantMsg.TrackingMeta);
        state.LastPersistedTerminal = state.LastPersistedTerminal || assistantMsg.TrackingMeta?.IsTerminal == true;

        var meta = assistantMsg.TrackingMeta;
        if (meta is null)
            return;

        if (!string.IsNullOrWhiteSpace(meta.DocId) && string.IsNullOrWhiteSpace(state.Job.DocId))
            state.Job.DocId = meta.DocId;
        if (!string.IsNullOrWhiteSpace(meta.DocPath) && string.IsNullOrWhiteSpace(state.Job.DocPath))
            state.Job.DocPath = meta.DocPath;
        if (!string.IsNullOrWhiteSpace(meta.LastKnownStatus))
        {
            var incomingStatus = NormalizeTrackedJobStatus(meta.LastKnownStatus);
            var currentStatus = NormalizeTrackedJobStatus(state.LastKnownStatus);
            if (state.LastKnownStatus is null || GetTrackedJobStatusRank(incomingStatus) >= GetTrackedJobStatusRank(currentStatus))
                state.LastKnownStatus = incomingStatus;
        }

        if (meta.IsTerminal && !state.IsTerminal)
            state.IsTerminal = true;

        var incomingProgressScore = GetTrackedProgressInfoScore(
            meta.LastKnownProgressPhase,
            meta.LastKnownProgressCurrent,
            meta.LastKnownProgressTotal,
            meta.LastKnownProgressPercent);
        var currentProgressScore = GetTrackedProgressInfoScore(
            state.LastKnownProgressPhase,
            state.LastKnownProgressCurrent,
            state.LastKnownProgressTotal,
            state.LastKnownProgressPercent);

        if (incomingProgressScore >= currentProgressScore)
        {
            state.LastKnownProgressPhase = meta.LastKnownProgressPhase ?? state.LastKnownProgressPhase;
            state.LastKnownProgressCurrent = meta.LastKnownProgressCurrent ?? state.LastKnownProgressCurrent;
            state.LastKnownProgressTotal = meta.LastKnownProgressTotal ?? state.LastKnownProgressTotal;
            state.LastKnownProgressPercent = meta.LastKnownProgressPercent ?? state.LastKnownProgressPercent;
        }

        state.StartedAtUtc ??= meta.StartedAtUtc;
        if (meta.LastSnapshotAtUtc.HasValue && (!state.LastSnapshotAtUtc.HasValue || meta.LastSnapshotAtUtc > state.LastSnapshotAtUtc))
            state.LastSnapshotAtUtc = meta.LastSnapshotAtUtc;
    }

    private static ChatTrackingMeta BuildTrackedJobMeta(DirectCommandTrackedJob trackedJob, bool isTerminal)
        => new()
        {
            Kind = "admin.ingestion.reindex",
            JobId = trackedJob.JobId,
            JobType = string.IsNullOrWhiteSpace(trackedJob.JobType) ? "ingestion" : trackedJob.JobType,
            DisplayLabel = trackedJob.DisplayLabel,
            DocId = trackedJob.DocId,
            DocPath = trackedJob.DocPath,
            LastKnownStatus = trackedJob.Status,
            IsTerminal = isTerminal
        };

    private ChatTrackingMeta BuildTrackedJobMeta(ActiveDirectCommandTrackerState state)
        => new()
        {
            Kind = "admin.ingestion.reindex",
            JobId = state.Job.JobId,
            JobType = string.IsNullOrWhiteSpace(state.Job.JobType) ? "ingestion" : state.Job.JobType,
            DisplayLabel = state.Job.DisplayLabel,
            DocId = state.Job.DocId,
            DocPath = state.Job.DocPath,
            LastKnownStatus = state.LastKnownStatus ?? state.Job.Status,
            LastKnownProgressPhase = state.LastKnownProgressPhase,
            LastKnownProgressCurrent = state.LastKnownProgressCurrent,
            LastKnownProgressTotal = state.LastKnownProgressTotal,
            LastKnownProgressPercent = state.LastKnownProgressPercent,
            StartedAtUtc = state.StartedAtUtc,
            LastSnapshotAtUtc = state.LastSnapshotAtUtc,
            IsTerminal = state.IsTerminal
        };

    private static bool TryCreateTrackedJobFromMessage(ChatMessageItem message, out DirectCommandTrackedJob trackedJob)
    {
        trackedJob = null!;
        var meta = message.TrackingMeta;
        if (meta is null)
            return false;
        if (!string.Equals(meta.Kind, "admin.ingestion.reindex", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(meta.JobId))
            return false;

        trackedJob = new DirectCommandTrackedJob
        {
            JobId = meta.JobId,
            JobType = string.IsNullOrWhiteSpace(meta.JobType) ? "ingestion" : meta.JobType,
            DisplayLabel = string.IsNullOrWhiteSpace(meta.DisplayLabel) ? (message.Content ?? string.Empty) : meta.DisplayLabel!,
            Status = meta.LastKnownStatus ?? "running",
            DocId = meta.DocId,
            DocPath = meta.DocPath
        };
        return true;
    }
    private async Task<JsonElement> TryLoadTrackedAdminJobSnapshotAsync(string jobId, CancellationToken ct)
    {
        JsonElement direct = default;
        var hasDirect = false;

        try
        {
            direct = await _api.AdminJobGetAsync(jobId, ct).ConfigureAwait(false);
            hasDirect = true;
            if (IsTrackedJobSnapshotInformative(direct))
                return direct;
        }
        catch
        {
        }

        try
        {
        var listed = await _api.AdminJobsListAsync("ingestion", 100, 0, null, null, null, null, ct).ConfigureAwait(false);
            if (listed.ValueKind == JsonValueKind.Object && listed.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = TryGetString(item, "jobId") ?? TryGetString(item, "JobId");
                    if (!string.Equals(id, jobId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!hasDirect)
                        return item.Clone();

                    return ChooseBetterTrackedJobSnapshot(direct, item.Clone());
                }
            }
        }
        catch
        {
            if (!hasDirect)
                throw;
        }

        if (hasDirect)
            return direct;

        throw new InvalidOperationException($"Unable to load admin job snapshot for {jobId}.");
    }

    private static bool IsAdminTrackingAccessUnavailable(HttpRequestException ex)
        => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private async Task<JsonElement> TryLoadTrackedJobSnapshotAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        HttpRequestException? messageTrackingError = null;

        if (!string.IsNullOrWhiteSpace(state.MessageId))
        {
            try
            {
                return await _api.ChatMessageTrackingAsync(state.MessageId!, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                messageTrackingError = ex;
                if (ex.StatusCode is not HttpStatusCode.NotFound)
                    throw;
            }
        }

        if (_api.HasAdminKey)
            return await TryLoadTrackedAdminJobSnapshotAsync(state.Job.JobId, ct).ConfigureAwait(false);

        if (messageTrackingError is not null)
            throw messageTrackingError;

        throw new InvalidOperationException($"Unable to load tracked job snapshot for {state.Job.JobId}.");
    }

    private async Task<bool> TryReconcileTrackedJobWithoutAdminAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.Job.DocId))
            return false;

        try
        {
            var isIndexed = await _api.DocumentsCatalogIsIndexedAsync(state.Job.DocId, ct).ConfigureAwait(false);
            if (!isIndexed)
                return false;

            state.LastKnownStatus = "done";
            state.LastSnapshotAtUtc = DateTimeOffset.UtcNow;
            state.Job.Status = "done";
            state.LastContent = DeterministicAgentText.AdminReindexCompleted(UiLang, state.Job.DisplayLabel);
            state.LastProgressText = null;
            state.LastStatusNote = null;
            state.IsTerminal = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement ChooseBetterTrackedJobSnapshot(JsonElement direct, JsonElement fallback)
    {
        var directScore = GetTrackedJobSnapshotScore(direct);
        var fallbackScore = GetTrackedJobSnapshotScore(fallback);
        return fallbackScore > directScore ? fallback : direct;
    }

    private static bool IsTrackedJobSnapshotInformative(JsonElement snapshot)
        => GetTrackedJobSnapshotScore(snapshot) >= 3;

    private static int GetTrackedJobSnapshotScore(JsonElement snapshot)
    {
        var score = 0;
        var status = (ReadTrackedJobStatus(snapshot) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status)) score += 1;
        if (!string.IsNullOrWhiteSpace(ReadTrackedJobProgressPhase(snapshot))) score += 2;
        if (ReadTrackedJobProgressCurrent(snapshot).HasValue) score += 2;
        if (ReadTrackedJobProgressTotal(snapshot).HasValue) score += 2;
        if (ReadTrackedJobProgressPercent(snapshot).HasValue) score += 3;
        if (TryGetDateTimeOffset(snapshot, "StartedAt").HasValue || TryGetDateTimeOffset(snapshot, "startedAt").HasValue) score += 1;
        return score;
    }

    private void ApplyTrackedJobSnapshotState(ActiveDirectCommandTrackerState state, JsonElement snapshot, bool hadTrackingError)
    {
        var snapshotStartedAt = TryGetDateTimeOffset(snapshot, "StartedAt")
            ?? TryGetDateTimeOffset(snapshot, "startedAt")
            ?? TryGetDateTimeOffset(snapshot, "CreatedAt")
            ?? TryGetDateTimeOffset(snapshot, "createdAt");
        if (snapshotStartedAt.HasValue)
            state.StartedAtUtc = snapshotStartedAt.Value;

        var status = NormalizeTrackedJobStatus(ReadTrackedJobStatus(snapshot) ?? state.Job.Status ?? "running");
        var lastError = ReadTrackedJobLastError(snapshot);
        var progressPhase = ReadTrackedJobProgressPhase(snapshot);
        var progressCurrent = ReadTrackedJobProgressCurrent(snapshot);
        var progressTotal = ReadTrackedJobProgressTotal(snapshot);
        var progressPercent = ReadTrackedJobProgressPercent(snapshot);
        var snapshotDocId = ReadTrackedJobDocId(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshotDocId) && string.IsNullOrWhiteSpace(state.Job.DocId))
            state.Job.DocId = snapshotDocId;
        var snapshotDocPath = TryGetString(snapshot, "docPath") ?? TryGetString(snapshot, "DocPath");
        if (!string.IsNullOrWhiteSpace(snapshotDocPath) && string.IsNullOrWhiteSpace(state.Job.DocPath))
            state.Job.DocPath = snapshotDocPath;

        if (!progressPercent.HasValue && progressCurrent.HasValue && progressTotal.HasValue && progressTotal.Value > 0)
            progressPercent = Math.Clamp((int)Math.Round((progressCurrent.Value * 100d) / progressTotal.Value, MidpointRounding.AwayFromZero), 0, 100);

        var currentStatus = NormalizeTrackedJobStatus(state.LastKnownStatus ?? state.Job.Status);
        var currentProgressScore = GetTrackedProgressInfoScore(
            state.LastKnownProgressPhase,
            state.LastKnownProgressCurrent,
            state.LastKnownProgressTotal,
            state.LastKnownProgressPercent);
        var incomingProgressScore = GetTrackedProgressInfoScore(
            progressPhase,
            progressCurrent,
            progressTotal,
            progressPercent);

        var currentIsTerminal = IsTrackedJobTerminalStatus(currentStatus);
        var incomingIsTerminal = IsTrackedJobTerminalStatus(status);

        if (currentIsTerminal && !incomingIsTerminal)
        {
            status = currentStatus;
            progressPhase = state.LastKnownProgressPhase;
            progressCurrent = state.LastKnownProgressCurrent;
            progressTotal = state.LastKnownProgressTotal;
            progressPercent = state.LastKnownProgressPercent;
        }
        else if (status == "queued" && currentStatus == "running" && currentProgressScore > incomingProgressScore)
        {
            status = currentStatus;
            progressPhase = state.LastKnownProgressPhase;
            progressCurrent = state.LastKnownProgressCurrent;
            progressTotal = state.LastKnownProgressTotal;
            progressPercent = state.LastKnownProgressPercent;
        }

        var displayPercent = GetTrackedJobDisplayPercent(status, progressPhase, progressPercent, progressCurrent, progressTotal);
        var startedAtUtc = state.StartedAtUtc ?? state.LastSnapshotAtUtc ?? DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));

        state.LastKnownStatus = status;
        state.LastKnownProgressPhase = progressPhase;
        state.LastKnownProgressCurrent = progressCurrent;
        state.LastKnownProgressTotal = progressTotal;
        state.LastKnownProgressPercent = displayPercent;
        state.LastSnapshotAtUtc = DateTimeOffset.UtcNow;
        state.Job.Status = status;

        if (status is "done" or "completed" or "succeeded" or "success")
        {
            state.LastContent = DeterministicAgentText.AdminReindexCompleted(UiLang, state.Job.DisplayLabel);
            state.LastProgressText = null;
            state.LastStatusNote = null;
            state.IsTerminal = true;
            return;
        }

        if (status is "failed" or "error" or "canceled" or "cancelled")
        {
            state.LastContent = DeterministicAgentText.AdminReindexFailed(UiLang, state.Job.DisplayLabel, lastError);
            state.LastProgressText = null;
            state.LastStatusNote = null;
            state.IsTerminal = true;
            return;
        }

        state.IsTerminal = false;
        state.LastStatusNote = hadTrackingError ? ClientUiText.Get("help.loading", UiLang) : null;

        if (status == "queued")
        {
            state.LastContent = DeterministicAgentText.AdminReindexQueued(UiLang, state.Job.DisplayLabel, state.Job.JobId);
            state.LastProgressText = DeterministicAgentText.AdminJobQueued(UiLang, state.Job.DisplayLabel, elapsedSeconds);
            return;
        }

        state.LastContent = displayPercent.HasValue
            ? DeterministicAgentText.AdminReindexRunningWithPercent(UiLang, state.Job.DisplayLabel, displayPercent.Value)
            : DeterministicAgentText.AdminReindexRunning(UiLang, state.Job.DisplayLabel);

        state.LastProgressText = DeterministicAgentText.AdminReindexProgressPhase(
            UiLang,
            progressPhase,
            displayPercent,
            progressCurrent,
            progressTotal,
            elapsedSeconds);
    }

    private async Task<bool> RefreshTrackedJobStateNowAsync(ActiveDirectCommandTrackerState state, CancellationToken ct, bool persist)
    {
        try
        {
            var snapshot = await TryLoadTrackedJobSnapshotAsync(state, ct).ConfigureAwait(false);
            ApplyTrackedJobSnapshotState(state, snapshot, hadTrackingError: false);
            if (persist)
                await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
            else
                await RunOnUiThreadAsync(() =>
                {
                    var msg = state.Message;
                    if (msg is null && string.Equals(_sessionId, state.SessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        msg = FindTrackedJobMessageForCurrentSession(state.MessageId, state.Job.DisplayLabel);
                        state.Message = msg;
                    }
                    if (msg is not null)
                        ApplyTrackedJobSnapshotToMessage(state, msg);
                }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException ex) when (IsAdminTrackingAccessUnavailable(ex))
        {
            _api.ClearAdminSessionKey();
            ClientLog.Warn($"Tracked job refresh lost admin session for jobId={state.Job.JobId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"Tracked job refresh failed for jobId={state.Job.JobId}: {ex.Message}");
        }

        if (await TryReconcileTrackedJobWithoutAdminAsync(state, ct).ConfigureAwait(false))
        {
            if (persist)
                await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task TrackDirectCommandJobAsync(ActiveDirectCommandTrackerState state)
    {
        var ct = state.Cancellation.Token;

        try
        {
            while (!ct.IsCancellationRequested && !state.IsTerminal)
            {
                var refreshed = await RefreshTrackedJobStateNowAsync(state, ct, persist: true).ConfigureAwait(false);
                if (state.IsTerminal)
                {
                    await PersistTrackedJobTerminalSnapshotStrongAsync(state).ConfigureAwait(false);
                    break;
                }

                if (!refreshed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                var status = (state.LastKnownStatus ?? state.Job.Status ?? string.Empty).Trim().ToLowerInvariant();
                await Task.Delay(TimeSpan.FromSeconds(status == "queued" ? 1.5 : 2.5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_directCommandTrackerGate)
            {
                state.TrackingLoopStarted = false;
                _directCommandTrackers.Remove(state.Cancellation);
                if (state.IsTerminal)
                    _activeDirectCommandTrackers.Remove(state.Job.JobId);
            }
            state.Cancellation.Dispose();
        }
    }

    private async Task ApplyTrackedJobStateToUiAndPersistAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        await RunOnUiThreadAsync(() =>
        {
            var msg = state.Message;
            if (msg is null && string.Equals(_sessionId, state.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                msg = FindTrackedJobMessageForCurrentSession(state.MessageId, state.Job.DisplayLabel);
                state.Message = msg;
            }
            if (msg is not null)
            {
                ApplyTrackedJobSnapshotToMessage(state, msg);
                if (_autoFollow && !_userScrolledUp)
                    ScrollToBottom(force: false);
            }
        }).ConfigureAwait(false);

        await MaybePersistTrackedJobSnapshotAsync(state, ct).ConfigureAwait(false);
    }

    private async Task<bool> PersistTrackedJobSnapshotCoreAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.MessageId))
            return false;

        var content = state.LastContent;
        var statusNote = state.LastStatusNote;
        var progressText = state.LastProgressText;
        var now = DateTimeOffset.UtcNow;
        var meta = BuildTrackedJobMeta(state);
        var metaJson = JsonSerializer.Serialize(meta);

        try
        {
            await _api.PatchMessageAsync(state.MessageId!, content, statusNote, progressText, meta, ct).ConfigureAwait(false);
            state.LastPersistedAtUtc = now;
            state.LastPersistedContent = content;
            state.LastPersistedStatusNote = statusNote;
            state.LastPersistedProgressText = progressText;
            state.LastPersistedTrackingMetaJson = metaJson;
            state.LastPersistedTerminal = state.IsTerminal;
            return true;
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"Tracked job message patch failed for jobId={state.Job.JobId} messageId={state.MessageId}: {ex.Message}");
            return false;
        }
    }

    private async Task MaybePersistTrackedJobSnapshotAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.MessageId))
            return;

        var content = state.LastContent;
        var statusNote = state.LastStatusNote;
        var progressText = state.LastProgressText;
        var now = DateTimeOffset.UtcNow;

        var meta = BuildTrackedJobMeta(state);
        var metaJson = JsonSerializer.Serialize(meta);

        var changed = !string.Equals(state.LastPersistedContent, content, StringComparison.Ordinal)
            || !string.Equals(state.LastPersistedStatusNote, statusNote, StringComparison.Ordinal)
            || !string.Equals(state.LastPersistedProgressText, progressText, StringComparison.Ordinal)
            || !string.Equals(state.LastPersistedTrackingMetaJson, metaJson, StringComparison.Ordinal)
            || state.LastPersistedTerminal != state.IsTerminal;

        if (!changed)
            return;

        await PersistTrackedJobSnapshotCoreAsync(state, ct).ConfigureAwait(false);
    }

    private async Task PersistTrackedJobTerminalSnapshotStrongAsync(ActiveDirectCommandTrackerState state)
    {
        if (!state.IsTerminal)
            return;
        if (state.LastPersistedTerminal)
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await PersistTrackedJobSnapshotCoreAsync(state, cts.Token).ConfigureAwait(false))
                return;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string NormalizeTrackedJobStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "completed" or "succeeded" or "success" => "done",
            "error" => "failed",
            "cancelled" => "canceled",
            "cancel-requested" or "cancel requested" => "cancel_requested",
            "pause" => "paused",
            _ => normalized.Length == 0 ? "queued" : normalized
        };
    }

    private static bool IsTrackedJobTerminalStatus(string? status)
    {
        var normalized = NormalizeTrackedJobStatus(status);
        return normalized is "done" or "failed" or "canceled";
    }

    private static int GetTrackedJobStatusRank(string? status)
    {
        var normalized = NormalizeTrackedJobStatus(status);
        return normalized switch
        {
            "queued" => 0,
            "running" => 1,
            "cancel_requested" => 1,
            "paused" => 1,
            "done" => 2,
            "failed" => 2,
            "canceled" => 2,
            _ => 0
        };
    }

    private static int GetTrackedProgressInfoScore(string? progressPhase, int? progressCurrent, int? progressTotal, int? progressPercent)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(progressPhase))
            score += 2;
        if (progressCurrent.HasValue)
            score += 2;
        if (progressTotal.HasValue)
            score += 2;
        if (progressPercent.HasValue)
            score += 3;
        return score;
    }

    private static int? GetTrackedJobDisplayPercent(string status, string? progressPhase, int? progressPercent, int? progressCurrent, int? progressTotal)
    {
        if (progressPercent.HasValue)
            return Math.Clamp(progressPercent.Value, 0, 100);

        if (progressCurrent.HasValue && progressTotal.HasValue && progressTotal.Value > 0)
            return Math.Clamp((int)Math.Round((progressCurrent.Value * 100d) / progressTotal.Value, MidpointRounding.AwayFromZero), 0, 100);

        var phase = (progressPhase ?? string.Empty).Trim().ToLowerInvariant();
        if (status == "queued")
            return 0;

        return phase switch
        {
            "preparing" => 1,
            "extracting" => 3,
            "deleting" => 97,
            "finalizing" => 99,
            _ => null
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(property.GetString(), out var value) ? value : null;
    }

    private async Task PersistTrackedJobTerminalSnapshotAsync(ChatMessageItem message, ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            state.MessageId = message.MessageId;
        state.Message = message;
        state.IsTerminal = true;
        await MaybePersistTrackedJobSnapshotAsync(state, ct).ConfigureAwait(false);
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }
}
