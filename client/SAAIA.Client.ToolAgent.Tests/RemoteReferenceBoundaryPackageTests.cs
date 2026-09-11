using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// EXP-050/Z.0 offline-only export. Produces the exact direct-boundary request
/// material and a physically separate oracle file. It never performs I/O unless
/// the explicit export switch and output directory are both provided, and it
/// never performs network or backend work.
/// </summary>
public sealed class RemoteReferenceBoundaryPackageTests(ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_EXPORT_REMOTE_REFERENCE_PACKAGE";
    private const string OutputDirectoryVariable =
        "SAAIA_REMOTE_REFERENCE_PACKAGE_DIRECTORY";

    [Fact]
    public async Task Outbound_package_is_exact_and_contains_no_evidence_or_oracles()
    {
        var tools = LiveDirectCapabilityBoundaryBenchmarkTests.BuildTools();
        var scenarios = LiveDirectCapabilityBoundaryBenchmarkTests.BuildScenarios();
        Assert.Equal(6, tools.Count);
        Assert.Equal(8, scenarios.Length);

        var outbound = new
        {
            schemaVersion = 1,
            experiment = "EXP-050",
            variant = "Z0-remote-reference-preauthorization",
            authorization = new
            {
                status = "NOT_AUTHORIZED",
                networkExecutionAllowed = false,
                providerSelected = false,
                modelSelected = false,
                maximumCostApproved = false
            },
            requests = scenarios.Select(scenario => new
            {
                scenarioId = scenario.Id,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = LiveDirectCapabilityBoundaryBenchmarkTests.SystemPrompt
                    },
                    new
                    {
                        role = "user",
                        content =
                            "CATEGORY_PATHS (optional exact values; omit when uncertain):\n"
                            + string.Join(
                                " | ",
                                LiveDirectCapabilityBoundaryBenchmarkTests.CategoryPaths)
                            + "\n\nUSER_MESSAGE:\n"
                            + scenario.Question
                    }
                },
                tools = tools.Select(tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.Parameters
                    }
                }),
                tool_choice = "required",
                temperature = 0,
                max_tokens = 256
            })
        };

        var oracles = new
        {
            schemaVersion = 1,
            experiment = "EXP-050",
            variant = "Z0-local-oracles",
            neverSend = true,
            scenarios = scenarios.Select(scenario => BuildOracle(scenario.Id))
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var outboundJson = JsonSerializer.Serialize(outbound, options);
        var oracleJson = JsonSerializer.Serialize(oracles, options);

        Assert.DoesNotContain(@"C:\", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            new Regex(@"[A-Za-z]:\\\\", RegexOptions.CultureInvariant),
            outboundJson);
        Assert.DoesNotContain("http://", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toolResults", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backendResponses\": true", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedTools", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requiredTerms", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allowedScopes", outboundJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"neverSend\": true", oracleJson, StringComparison.Ordinal);

        using (var outboundDocument = JsonDocument.Parse(outboundJson))
        {
            var requests = outboundDocument.RootElement.GetProperty("requests");
            Assert.Equal(8, requests.GetArrayLength());
            foreach (var request in requests.EnumerateArray())
            {
                var messages = request.GetProperty("messages");
                Assert.Equal(2, messages.GetArrayLength());
                Assert.Equal(
                    "system",
                    messages[0].GetProperty("role").GetString());
                Assert.Equal(
                    "user",
                    messages[1].GetProperty("role").GetString());
                Assert.Equal(6, request.GetProperty("tools").GetArrayLength());
                Assert.Equal("required", request.GetProperty("tool_choice").GetString());
                Assert.Equal(0, request.GetProperty("temperature").GetInt32());
                Assert.Equal(256, request.GetProperty("max_tokens").GetInt32());
            }
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine("Offline package validated in memory; export disabled.");
            return;
        }

        var outputDirectory = Environment.GetEnvironmentVariable(
            OutputDirectoryVariable);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException(
                $"{OutputDirectoryVariable} is required when {EnableVariable}=1.");
        }

        var fullDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullDirectory);
        var outboundPath = Path.Combine(
            fullDirectory,
            "exp050-z0-remote-reference-outbound.json");
        var oraclePath = Path.Combine(
            fullDirectory,
            "exp050-z0-remote-reference-oracles-never-send.json");
        await File.WriteAllTextAsync(outboundPath, outboundJson);
        await File.WriteAllTextAsync(oraclePath, oracleJson);
        output.WriteLine("Outbound package: " + outboundPath);
        output.WriteLine("Local-only oracles: " + oraclePath);
    }

    private static object BuildOracle(string scenarioId)
        => scenarioId switch
        {
            "compound-cuisine" => new
            {
                scenarioId,
                expectedTools = new[] { "rag_search" },
                requiredTerms = new[] { "neff", "ingredient", "reglage" },
                allowedScopes = new[] { "Cuisine" },
                queryMustBeEmpty = false
            },
            "compound-noncuisine" => new
            {
                scenarioId,
                expectedTools = new[]
                {
                    "documents_context", "documents_navigation", "rag_search"
                },
                requiredTerms = new[] { "ansi", "iec 60204-1" },
                allowedScopes = new[] { "Normes" },
                queryMustBeEmpty = false
            },
            "ambiguous" => SimpleOracle(
                scenarioId,
                LiveDirectCapabilityBoundaryBenchmarkTests.ClarificationToolName),
            "ambiguous-nongrid" => new
            {
                scenarioId,
                expectedTools = new[]
                {
                    LiveDirectCapabilityBoundaryBenchmarkTests.ClarificationToolName
                },
                requiredTerms = new[] { "obligatoire", "recommandation" },
                allowedScopes = Array.Empty<string>(),
                queryMustBeEmpty = false
            },
            "operational-inventory" => SimpleOracle(
                scenarioId,
                LiveDirectCapabilityBoundaryBenchmarkTests.CountToolName),
            "simple" => new
            {
                scenarioId,
                expectedTools = new[] { "rag_search" },
                requiredTerms = new[] { "ratatouille" },
                allowedScopes = new[] { "Cuisine" },
                queryMustBeEmpty = false
            },
            "named-document" => new
            {
                scenarioId,
                expectedTools = new[]
                {
                    "documents_navigation", "documents_context"
                },
                requiredTerms = new[] { "fit-ptfe_tf_1620-en.pdf" },
                allowedScopes = Array.Empty<string>(),
                queryMustBeEmpty = false
            },
            "p1" => new
            {
                scenarioId,
                expectedTools = new[]
                {
                    "documents_content_cards", "documents_navigation"
                },
                requiredTerms = Array.Empty<string>(),
                allowedScopes = new[] { "Cuisine" },
                queryMustBeEmpty = true
            },
            _ => throw new InvalidOperationException(
                "Unknown frozen scenario: " + scenarioId)
        };

    private static object SimpleOracle(string scenarioId, string expectedTool)
        => new
        {
            scenarioId,
            expectedTools = new[] { expectedTool },
            requiredTerms = Array.Empty<string>(),
            allowedScopes = Array.Empty<string>(),
            queryMustBeEmpty = false
        };
}
