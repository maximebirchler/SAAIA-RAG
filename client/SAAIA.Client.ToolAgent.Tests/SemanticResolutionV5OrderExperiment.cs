using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.ToolAgent.Tests;

internal enum SemanticResolutionV5OrderArm
{
    DecisionFirst,
    ReasonFirst
}

internal static class SemanticResolutionV5OrderExperiment
{
    internal const string ContractName = "source_backed_semantic_resolution_v5";

    private static readonly string[] DecisionFirstOrder =
    {
        "decision",
        "evidenceIds",
        "reason"
    };

    private static readonly string[] ReasonFirstOrder =
    {
        "reason",
        "evidenceIds",
        "decision"
    };

    internal static LlmStructuredOutputContract BuildFrozenContract(
        IReadOnlyList<string> allowedEvidenceIds)
    {
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["decision"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[]
                        {
                            "answer", "clarify", "context", "research"
                        }
                    },
                    ["evidenceIds"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 0,
                        ["maxItems"] = Math.Min(12, allowedEvidenceIds.Count),
                        ["uniqueItems"] = true,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = allowedEvidenceIds
                        }
                    },
                    ["reason"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["minLength"] = 8,
                        ["maxLength"] = 480
                    }
                },
                ["required"] = DecisionFirstOrder,
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(ContractName, schema);
    }

    internal static LlmStructuredOutputContract Apply(
        LlmStructuredOutputContract contract,
        SemanticResolutionV5OrderArm arm)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (arm == SemanticResolutionV5OrderArm.DecisionFirst
            || !string.Equals(contract.Name, ContractName, StringComparison.Ordinal))
        {
            return contract;
        }

        var root = contract.Schema;
        RequireExactOrder(
            root.EnumerateObject().Select(static property => property.Name),
            "type", "properties", "required", "additionalProperties");
        var properties = root.GetProperty("properties");
        RequireExactOrder(
            properties.EnumerateObject().Select(static property => property.Name),
            DecisionFirstOrder);
        RequireExactOrder(
            root.GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString() ?? string.Empty),
            DecisionFirstOrder);

        var reorderedProperties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var propertyName in ReasonFirstOrder)
        {
            reorderedProperties[propertyName] =
                properties.GetProperty(propertyName).Clone();
        }

        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = root.GetProperty("type").Clone(),
                ["properties"] = reorderedProperties,
                ["required"] = ReasonFirstOrder,
                ["additionalProperties"] =
                    root.GetProperty("additionalProperties").Clone()
            });
        return new LlmStructuredOutputContract(contract.Name, schema);
    }

    private static void RequireExactOrder(
        IEnumerable<string> actual,
        params string[] expected)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The paired V5 order experiment requires the exact frozen source schema.");
        }
    }
}
