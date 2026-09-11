using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private async Task<string> CompleteWithRetryAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chars = messages.Sum(m => m.content?.Length ?? 0);
        ClientLog.Info(
            "ToolAgent llm complete start: " +
            $"forceJson={forceJson}|messages={messages.Count}|chars={chars}");
        try
        {
            var result = await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm complete end: " +
                $"forceJson={forceJson}|chars={chars}|answerChars={result?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            ClientLog.Info(
                "ToolAgent llm complete cancelled: " +
                $"forceJson={forceJson}|chars={chars}|ctCancelled={ct.IsCancellationRequested}|ms={sw.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex) when (!IsLlmContextOverflowException(ex))
        {
            ClientLog.Info(
                "ToolAgent llm complete retry: " +
                $"forceJson={forceJson}|chars={chars}|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 220)}");
            await Task.Delay(150, ct).ConfigureAwait(false);
            var retry = await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm complete retry end: " +
                $"forceJson={forceJson}|chars={chars}|answerChars={retry?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return retry ?? string.Empty;
        }
    }

    private async Task<string> CompleteStructuredWithRetryAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chars = messages.Sum(message => message.content?.Length ?? 0);
        ClientLog.Info(
            "ToolAgent llm structured complete start: "
            + $"contract={contract.Name}|messages={messages.Count}|chars={chars}");
        try
        {
            var result = await _llm.CompleteStructuredAsync(
                    messages,
                    contract,
                    ct)
                .ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm structured complete end: "
                + $"contract={contract.Name}|chars={chars}|answerChars={result?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!IsLlmContextOverflowException(ex))
        {
            ClientLog.Info(
                "ToolAgent llm structured complete retry: "
                + $"contract={contract.Name}|chars={chars}|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 220)}");
            await Task.Delay(150, ct).ConfigureAwait(false);
            var retry = await _llm.CompleteStructuredAsync(
                    messages,
                    contract,
                    ct)
                .ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm structured complete retry end: "
                + $"contract={contract.Name}|chars={chars}|answerChars={retry?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return retry ?? string.Empty;
        }
    }

    private async Task<string> StreamOrCompleteWithRetryAsync(
        IReadOnlyList<(string role, string content)> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chars = messages.Sum(m => m.content?.Length ?? 0);
        ClientLog.Info(
            "ToolAgent llm writer start: " +
            $"stream={onDelta is not null}|messages={messages.Count}|chars={chars}");
        if (onDelta is null)
        {
            var completed = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm writer end: " +
                $"stream=false|chars={chars}|answerChars={completed?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return completed ?? string.Empty;
        }

        var streamed = new StringBuilder();
        try
        {
            await _llm.StreamAsync(messages, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ClientLog.Info(
                "ToolAgent llm writer cancelled: " +
                $"stream=true|chars={chars}|partialChars={streamed.Length}|ms={sw.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Info(
                "ToolAgent llm writer stream fallback: " +
                $"chars={chars}|partialChars={streamed.Length}|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 220)}");
            if (streamed.Length == 0)
            {
                var completed = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
                ClientLog.Info(
                    "ToolAgent llm writer fallback end: " +
                    $"chars={chars}|answerChars={completed?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
                return completed ?? string.Empty;
            }
        }

        var finalAnswer = streamed.ToString();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

        ClientLog.Info(
            "ToolAgent llm writer end: " +
            $"stream=true|chars={chars}|answerChars={finalAnswer.Length}|ms={sw.ElapsedMilliseconds}");
        return finalAnswer;
    }

}
