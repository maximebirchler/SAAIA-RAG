using System.Text.Json;

/// <summary>Observed runtime admission. The token count includes special tokens and the server prompt.</summary>
internal sealed record TeiInputBudget(int MaximumInputTokens, int MaximumClientBatchSize);

static partial class TeiClient
{
    public static async Task<TeiInputBudget> GetInputBudgetAsync(HttpClient tei, CancellationToken ct)
    {
        using var response = await SendAdmissionRequestAsync(tei, HttpMethod.Get, "/info", null, "info", ct);
        using var document = await TeiResponseReader.ReadJsonAsync(response, 32 * 1024, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("max_input_length", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var maximum)
            || maximum is < 1 or > 1_048_576
            || !root.TryGetProperty("auto_truncate", out var truncate)
            || truncate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("tei_input_budget_protocol_invalid");
        if (truncate.GetBoolean())
            throw new TeiRequestException("tei_silent_truncation_runtime_refused");
        var batchSize = 1;
        if (root.TryGetProperty("max_client_batch_size", out var batch)
            && (batch.ValueKind != JsonValueKind.Number || !batch.TryGetInt32(out batchSize) || batchSize < 1))
            throw new InvalidDataException("tei_input_budget_protocol_invalid");
        return new(maximum, batchSize);
    }

    public static async Task<int> CountInputTokensAsync(HttpClient tei, string finalInput, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { inputs = finalInput, add_special_tokens = true });
        using var response = await SendAdmissionRequestAsync(tei, HttpMethod.Post, "/tokenize", payload, "tokenize", ct);
        using var document = await TeiResponseReader.ReadJsonAsync(response, 16 * 1024 * 1024, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1
            || root[0].ValueKind != JsonValueKind.Array || root[0].GetArrayLength() == 0)
            throw new InvalidDataException("tei_tokenize_protocol_invalid");
        foreach (var token in root[0].EnumerateArray())
        {
            if (token.ValueKind != JsonValueKind.Object
                || !token.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out _)
                || !token.TryGetProperty("special", out var special)
                || special.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("tei_tokenize_protocol_invalid");
        }
        return root[0].GetArrayLength();
    }
}
