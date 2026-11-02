using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    // Wire-budget fixtures start after admission. Supply their known call IDs
    // explicitly; no production or fixture renderer joins request positions.
    private static void BindAdmittedNativeFixtureCalls(object turn,
        params (string CallId, AdvancedAnalysisSearchRequest Request)[] bindings)
    {
        var property = turn.GetType().GetProperty("CallRequests");
        // The causal runner also compiles these fixtures against historical
        // production sources, where this admission field did not yet exist.
        if (property is null) return;
        using var calls = JsonDocument.Parse((string)turn.GetType().GetProperty("CallsJson")!.GetValue(turn)!);
        var ids = calls.RootElement.EnumerateArray().Where(call =>
                call.GetProperty("function").GetProperty("name").GetString()
                    is "search_corpus" or "read_source" or "find_source_text")
            .Select(call => call.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Order(StringComparer.Ordinal), bindings.Select(binding => binding.CallId).Order(StringComparer.Ordinal));
        var admitted = (IReadOnlyList<AdvancedAnalysisSearchRequest>)turn.GetType().GetProperty("Requests")!.GetValue(turn)!;
        foreach (var binding in bindings)
        {
            var identity = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", binding.Request)!;
            Assert.Contains(admitted, request => string.Equals(identity,
                (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!, StringComparison.OrdinalIgnoreCase));
        }
        property.SetValue(turn, bindings.ToDictionary(binding => binding.CallId, binding => binding.Request, StringComparer.Ordinal));
    }
}
