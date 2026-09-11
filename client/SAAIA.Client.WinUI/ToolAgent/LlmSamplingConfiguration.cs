using System.Globalization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal sealed record LlmSamplingConfiguration(
    double? TemperatureOverride,
    double? TopP,
    int? TopK,
    double? MinP,
    double? FrequencyPenalty,
    double? PresencePenalty,
    bool DeterministicStructuredOutputs)
{
    internal const string TemperatureEnvironmentVariable = "SAAIA_LLM_TEMPERATURE";
    internal const string TopPEnvironmentVariable = "SAAIA_LLM_TOP_P";
    internal const string TopKEnvironmentVariable = "SAAIA_LLM_TOP_K";
    internal const string MinPEnvironmentVariable = "SAAIA_LLM_MIN_P";
    internal const string FrequencyPenaltyEnvironmentVariable = "SAAIA_LLM_FREQUENCY_PENALTY";
    internal const string PresencePenaltyEnvironmentVariable = "SAAIA_LLM_PRESENCE_PENALTY";
    internal const string StructuredSamplingEnvironmentVariable = "SAAIA_LLM_STRUCTURED_SAMPLING";

    internal static LlmSamplingConfiguration ResolveFromEnvironment()
        => new(
            ReadOptionalDouble(TemperatureEnvironmentVariable, 0d, 2d),
            ReadOptionalDouble(TopPEnvironmentVariable, 0d, 1d),
            ReadOptionalInt(TopKEnvironmentVariable, 0, 1000),
            ReadOptionalDouble(MinPEnvironmentVariable, 0d, 1d),
            ReadOptionalDouble(FrequencyPenaltyEnvironmentVariable, -2d, 2d),
            ReadOptionalDouble(PresencePenaltyEnvironmentVariable, -2d, 2d),
            !string.Equals(
                Environment.GetEnvironmentVariable(StructuredSamplingEnvironmentVariable)?.Trim(),
                "native",
                StringComparison.OrdinalIgnoreCase));

    internal void Apply(
        IDictionary<string, object?> payload,
        double requestedTemperature,
        bool structuredOutput)
    {
        var deterministic = structuredOutput && DeterministicStructuredOutputs;
        payload["temperature"] = deterministic
            ? 0d
            : TemperatureOverride ?? requestedTemperature;
        payload["top_p"] = deterministic
            ? 1d
            : TopP ?? 0.85d;
        payload["frequency_penalty"] = deterministic
            ? 0d
            : FrequencyPenalty ?? 0.2d;
        payload["presence_penalty"] = deterministic
            ? 0d
            : PresencePenalty ?? 0.05d;

        if (!deterministic && TopK is not null)
            payload["top_k"] = TopK.Value;
        if (!deterministic && MinP is not null)
            payload["min_p"] = MinP.Value;
    }

    private static double? ReadOptionalDouble(string name, double minimum, double maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!double.TryParse(
                raw.Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be a number between {minimum.ToString(CultureInfo.InvariantCulture)} " +
                $"and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return value;
    }

    private static int? ReadOptionalInt(string name, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }
}
