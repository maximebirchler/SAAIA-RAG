using System.Text;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveCuisineAgentValidationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_cuisine_questions_run_through_real_client_agent_when_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SAAIA_LIVE_VALIDATION"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Skipped: set SAAIA_LIVE_VALIDATION=1 to run the live client-agent validation.");
            return;
        }

        var backendUrl = RequireEnv("SAAIA_VALIDATION_BACKEND_URL");
        var apiKey = RequireEnv("SAAIA_API_KEY");
        var llmBaseUrl = NormalizeLlmBaseUrl(RequireEnv("SAAIA_VALIDATION_LLM_BASE_URL"));
        var llmModel = RequireEnv("SAAIA_VALIDATION_LLM_MODEL");

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));

        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, llmModel);

        var agent = new RagChatAgent(api, llm);
        agent.ApplySettings(new AppSettings
        {
            UseLocalLlm = true,
            ManageLocalLlmProcess = false,
            ActiveMode = "strict",
            RagQualityPreset = "deep",
            LlmTemperature = 0.1,
            LlmMaxOutputTokens = 900,
            UiLanguage = "fr"
        });

        var cases = new[]
        {
            "Je vais faire une entrecôte, quelle sauce irait bien avec ?",
            "Je ne sais pas quoi faire pour les repas de cette semaine, tu peux m'aider ?",
            "Je veux un dessert au chocolat facile, tu proposes quoi ?",
            "J'ai du cabillaud, tu as une recette ?",
            "Tu peux me faire une idée de batch cooking avec cuisson parallèle ?"
        };

        foreach (var question in cases)
        {
            var streamed = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

            var (answer, sourcesPayload) = await agent.RunAsync(
                question,
                category: "",
                conversationTail: Array.Empty<ChatMessageItem>(),
                onDelta: delta => streamed.Append(delta),
                ct: cts.Token,
                onPhase: phase => output.WriteLine("PHASE: " + phase),
                onProgress: progress =>
                {
                    if (!string.IsNullOrWhiteSpace(progress))
                        output.WriteLine("PROGRESS: " + progress);
                });

            var rendered = string.IsNullOrWhiteSpace(answer) ? streamed.ToString() : answer;
            output.WriteLine("QUESTION: " + question);
            output.WriteLine("ANSWER:");
            output.WriteLine(rendered);

            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.NotNull(sourcesPayload);
            Assert.DoesNotContain("Aucun document trouve", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aucune donnee", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Laquelle veux-tu que j'utilise", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Le meilleur r", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Cuisine", rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RequireEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required when SAAIA_LIVE_VALIDATION=1.");
        return value.Trim();
    }

    private static string NormalizeLlmBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }
}
