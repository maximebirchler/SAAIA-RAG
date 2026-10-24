using System.Text;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record ProviderErrorBody(string? Text, bool Truncated, bool ReadFailed);

    // Read once and bound provider rejection bodies. A broken body must not replace its HTTP status.
    private static async Task<ProviderErrorBody> ReadProviderErrorBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken, CancellationToken requestCancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64_001];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            return length > 64_000
                ? new ProviderErrorBody(null, true, false)
                : new ProviderErrorBody(Encoding.UTF8.GetString(buffer, 0, length), false, false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProviderErrorBody(null, false, true);
        }
        catch (OperationCanceledException) when (!requestCancellationToken.IsCancellationRequested)
        {
            // The HTTP status is already known. An error-body read timeout is not a new generation timeout.
            return new ProviderErrorBody(null, false, true);
        }
    }
}
