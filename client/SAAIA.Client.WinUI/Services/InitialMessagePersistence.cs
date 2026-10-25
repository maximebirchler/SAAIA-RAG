using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Confirms the user message before the composer is cleared or generation can run.
/// A failed response is unconfirmed storage: the server may already have committed it.
/// </summary>
internal static class InitialMessagePersistence
{
    // Initial chat-store acknowledgement only. Advanced/long-running calls have
    // their own contracts and do not inherit this limit. A shorter deadline may
    // be supplied by a controlled caller; it can never exceed this bound.
    internal static readonly TimeSpan MaximumConfirmationTimeout = TimeSpan.FromSeconds(30);

    internal static async Task PersistBeforeGenerationAsync(ApiClient api, string sessionId,
        string displayText, Action<ChatMessageItem?> commitVisibleState, CancellationToken turnCancellationToken,
        TimeSpan? confirmationTimeout = null)
    {
        var timeout = confirmationTimeout ?? MaximumConfirmationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaximumConfirmationTimeout)
            throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        using var deadline = new CancellationTokenSource(timeout);
        var clock = Stopwatch.StartNew();
        ChatMessageItem? saved;
        try
        {
            // User cancellation does not abort the admitted write. Its independent
            // confirmation deadline prevents a silent server from holding the UI
            // forever. Timeout still leaves server-side persistence uncertain.
            saved = await api.AddMessageAsync(sessionId, "user", displayText, null, deadline.Token,
                requireDurableAcknowledgement: true);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.Journal.Emit("chat.user.persistence", "failed",
                deadline.IsCancellationRequested && exception is OperationCanceledException
                    ? "confirmation_timeout" : "confirmation_unavailable",
                new Dictionary<string, long?> { ["durationMs"] = clock.ElapsedMilliseconds,
                    ["confirmationTimeoutMs"] = (long)timeout.TotalMilliseconds });
            throw new InitialMessagePersistenceUnconfirmedException(exception);
        }
        finally
        {
            ClientDiagnostics.Journal.Phase("userPersistence", clock.ElapsedMilliseconds);
        }

        ClientDiagnostics.Journal.Emit("chat.user.persistence", "success", "server_acknowledged",
            new Dictionary<string, long?> { ["durationMs"] = clock.ElapsedMilliseconds,
                ["confirmationTimeoutMs"] = (long)timeout.TotalMilliseconds });
        // Intentionally retain the caller's UI synchronization context for this commit.
        commitVisibleState(saved);
        turnCancellationToken.ThrowIfCancellationRequested();
    }
}

internal sealed class InitialMessagePersistenceUnconfirmedException(Exception innerException)
    : Exception("User message persistence was not confirmed.", innerException);
