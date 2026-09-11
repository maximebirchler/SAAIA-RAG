using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class LlmStructuredOutputContract
{
    private static readonly Regex ValidName = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public LlmStructuredOutputContract(string name, JsonElement schema)
    {
        if (string.IsNullOrWhiteSpace(name) || !ValidName.IsMatch(name))
            throw new ArgumentException("Structured output contract name must contain only letters, digits, '_' or '-'.", nameof(name));
        if (schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Structured output schema must be a JSON object.", nameof(schema));

        Name = name;
        Schema = schema.Clone();
    }

    public string Name { get; }

    public JsonElement Schema { get; }

    public static LlmStructuredOutputContract Parse(string name, string jsonSchema)
    {
        using var document = JsonDocument.Parse(jsonSchema);
        return new LlmStructuredOutputContract(name, document.RootElement);
    }
}
