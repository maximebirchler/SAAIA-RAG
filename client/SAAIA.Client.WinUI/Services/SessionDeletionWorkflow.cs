using System;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>Keeps an acknowledged deletion distinct from a failed inventory refresh.</summary>
internal static class SessionDeletionWorkflow
{
    internal static async Task<SessionDeletionResult> ExecuteAsync(ApiClient api, string sessionId,
        Action commitAcknowledgedDeletion, Func<Task> refreshAndLoad, Func<Task<bool>> recoverFromKnownSessions)
    {
        using var diagnostic = ClientDiagnostics.Journal.BeginTurn(sessionId, "session_delete");
        try
        {
            await api.DeleteSessionAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            diagnostic.Emit("session.delete.request", "failed", exception.GetType().Name);
            diagnostic.Complete("failed", "delete_unconfirmed");
            throw;
        }

        diagnostic.Emit("session.delete.acknowledged", "success", "server_acknowledged");
        try
        {
            // The UI commit must precede any fallible inventory/message read.
            // Retain the UI synchronization context for all supplied callbacks.
            commitAcknowledgedDeletion();
            await refreshAndLoad();
            diagnostic.Emit("session.delete.refresh", "success");
            diagnostic.Complete();
            return new(null, null, true);
        }
        catch (Exception refreshFailure)
        {
            diagnostic.Emit("session.delete.refresh_failed", "degraded", refreshFailure.GetType().Name);
            Exception? recoveryFailure = null;
            var conversationAvailable = false;
            try
            {
                conversationAvailable = await recoverFromKnownSessions();
                diagnostic.Emit("session.delete.recovery", conversationAvailable ? "success" : "degraded",
                    conversationAvailable ? "known_session_available" : "no_loaded_session");
            }
            catch (Exception exception)
            {
                recoveryFailure = exception;
                diagnostic.Emit("session.delete.recovery", "failed", exception.GetType().Name);
            }
            diagnostic.Complete("degraded", "deleted_refresh_failed");
            return new(refreshFailure, recoveryFailure, conversationAvailable);
        }
    }
}

internal sealed record SessionDeletionResult(Exception? RefreshFailure, Exception? RecoveryFailure, bool ConversationAvailable);
