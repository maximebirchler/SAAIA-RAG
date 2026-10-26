using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Safe transport diagnostics. Upstream prose and input text never become exception data.</summary>
internal sealed partial class TeiRequestException : HttpRequestException
{
    public string ErrorCode { get; }
    public int? MaximumTokens { get; }
    public int? ObservedTokens { get; }
    public int? MaximumCharacters { get; }
    public int? ObservedCharacters { get; }

    internal TeiRequestException(string errorCode, HttpStatusCode? status = null,
        int? maximumTokens = null, int? observedTokens = null, int? maximumCharacters = null, int? observedCharacters = null)
        : base($"{errorCode}; http={(int?)status}; max_tokens={maximumTokens}; observed_tokens={observedTokens}; max_characters={maximumCharacters}; observed_characters={observedCharacters}", null, status)
    {
        ErrorCode = errorCode;
        MaximumTokens = maximumTokens;
        ObservedTokens = observedTokens;
        MaximumCharacters = maximumCharacters;
        ObservedCharacters = observedCharacters;
        Data["error_code"] = errorCode;
        if (maximumTokens.HasValue) Data["max_tokens"] = maximumTokens.Value;
        if (observedTokens.HasValue) Data["observed_tokens"] = observedTokens.Value;
        if (maximumCharacters.HasValue) Data["max_characters"] = maximumCharacters.Value;
        if (observedCharacters.HasValue) Data["observed_characters"] = observedCharacters.Value;
    }

    internal static async Task<TeiRequestException> FromResponseAsync(HttpResponseMessage response,
        string operation, CancellationToken ct)
    {
        // Only this anchored, known numeric validation grammar is retained. An arbitrary
        // backend error may contain source text, a URL, a path or a credential.
        try
        {
            using var document = await TeiResponseReader.ReadJsonAsync(response, 16 * 1024, ct);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                JsonElement message;
                if ((root.TryGetProperty("message", out message) || root.TryGetProperty("error", out message))
                    && message.ValueKind == JsonValueKind.String)
                {
                    var match = TokenLimitMessage().Match(message.GetString() ?? string.Empty);
                    if (match.Success
                        && int.TryParse(match.Groups[1].Value, out var maximum) && maximum > 0
                        && int.TryParse(match.Groups[2].Value, out var observed) && observed > maximum)
                        return new("tei_input_token_limit_exceeded", response.StatusCode, maximum, observed);
                    var characterMatch = CharacterLimitMessage().Match(message.GetString() ?? string.Empty);
                    if (characterMatch.Success
                        && int.TryParse(characterMatch.Groups[1].Value, out var maximumCharacters) && maximumCharacters > 0
                        && int.TryParse(characterMatch.Groups[2].Value, out var observedCharacters) && observedCharacters > maximumCharacters)
                        return new("tei_input_character_limit_exceeded", response.StatusCode,
                            maximumCharacters: maximumCharacters, observedCharacters: observedCharacters);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { }

        // operation is a code-owned literal, never a value from the server.
        return new(operation switch
        {
            "info" => "tei_info_http_error",
            "tokenize" => "tei_tokenize_http_error",
            _ => "tei_embeddings_http_error"
        }, response.StatusCode);
    }

    [GeneratedRegex("^(?:Input validation error: )?`inputs` must have less than ([0-9]{1,9}) tokens\\. Given: ([0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLimitMessage();

    [GeneratedRegex("^(?:Input validation error: )?`inputs` must have less than ([0-9]{1,9}) characters\\. Given: ([0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterLimitMessage();
}
