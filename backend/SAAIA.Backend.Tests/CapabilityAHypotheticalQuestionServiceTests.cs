using System.Net;
using System.Text;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CapabilityAHypotheticalQuestionServiceTests
{
    [Fact]
    public async Task BuildQuestionsAsync_uses_llm_questions_when_runtime_returns_structured_json()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"questions\":[\"How should operators apply the control loop overview in IND570?\",\"When does the IND570 control loop guidance become relevant for PLC integration?\"]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var questions = await service.BuildQuestionsAsync(
            "MettlerToledo_IND570.pdf",
            ["Control Loop Overview", "PLC Integration"],
            ["The control loop overview explains how the PLC exchange should be supervised."],
            CancellationToken.None);

        Assert.Equal(2, questions.Count);
        Assert.Contains(questions, question => question.Contains("IND570", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(questions, question => question.StartsWith("What does MettlerToledo_IND570.pdf say about", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildQuestionsAsync_falls_back_to_deterministic_questions_when_runtime_is_unavailable()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var questions = await service.BuildQuestionsAsync(
            "MettlerToledo_IND570.pdf",
            ["Control Loop Overview"],
            ["The control loop overview explains how the PLC exchange should be supervised."],
            CancellationToken.None);

        Assert.Contains("What does MettlerToledo_IND570.pdf say about Control Loop Overview?", questions);
        Assert.Contains("Which requirements from MettlerToledo_IND570.pdf apply to Control Loop Overview?", questions);
    }

    [Fact]
    public async Task BuildTagsAsync_uses_llm_tags_when_runtime_returns_structured_json()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"tags\":[\"plc-integration\",\"control-loop\",\"ind570\"]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var tags = await service.BuildTagsAsync(
            "MettlerToledo_IND570.pdf",
            "programmation",
            ["Control Loop Overview", "PLC Integration"],
            ["The control loop overview explains how the PLC exchange should be supervised."],
            CancellationToken.None);

        Assert.Contains("plc-integration", tags);
        Assert.Contains("control-loop", tags);
        Assert.Contains("ind570", tags);
    }

    [Fact]
    public async Task BuildTagsAsync_falls_back_to_deterministic_tags_when_runtime_is_unavailable()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var tags = await service.BuildTagsAsync(
            "MettlerToledo_IND570.pdf",
            "programmation",
            ["Control Loop Overview"],
            ["The control loop overview explains how the PLC exchange should be supervised."],
            CancellationToken.None);

        Assert.Contains("programmation", tags);
        Assert.Contains("control", tags);
        Assert.Contains("loop", tags);
    }

    [Fact]
    public async Task BuildQuestionsAsync_parses_code_fenced_json_payload_from_llm()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "```json\n{\"questions\":[\"How is the PLC control loop supervised?\",\"When should operators review the IND570 exchange guardrails?\"]}\n```"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var questions = await service.BuildQuestionsAsync(
            "MettlerToledo_IND570.pdf",
            ["Control Loop Overview"],
            ["The control loop overview explains how the PLC exchange should be supervised."],
            CancellationToken.None);

        Assert.Equal(2, questions.Count);
        Assert.Contains(questions, question => question.Contains("PLC control loop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(questions, question => question.Contains("IND570", StringComparison.OrdinalIgnoreCase));
    }

    private static CapabilityAHypotheticalQuestionService CreateService(string body, HttpStatusCode statusCode)
        => new(new LocalLlmChatClient(
            new StubHttpClientFactory(body, statusCode),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
