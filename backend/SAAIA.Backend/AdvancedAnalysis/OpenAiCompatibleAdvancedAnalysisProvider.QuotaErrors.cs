using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    internal static bool IsTerminalProviderQuotaError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
                return false;

            // HTTP 429 also covers transient request/token rate limits. Only
            // explicit machine-readable billing/quota reasons are terminal.
            // Do not classify localized free-form messages as billing failures.
            return IsTerminalQuotaValue(error, "type")
                   || IsTerminalQuotaValue(error, "code");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTerminalQuotaValue(JsonElement error, string property)
        => error.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is "insufficient_quota"
               or "credit_balance_exhausted"
               or "billing_hard_limit_reached"
               or "billing_not_active";
}
