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
    public async Task BuildQuestionsAsync_uses_neutral_fallback_for_non_ui_document_language()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var questions = await service.BuildQuestionsAsync(
            "Handleiding.pdf",
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.Contains("Handleiding.pdf: Onderhoud?", questions);
        Assert.DoesNotContain(questions, question => question.StartsWith("What does", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildQuestionsAsync_includes_detected_document_language_in_prompt()
    {
        string? capturedRequest = null;
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"questions\":[\"Welke controles staan in de handleiding?\",\"Wanneer is onderhoud vereist?\"]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK,
            requestBody => capturedRequest = requestBody);

        var questions = await service.BuildQuestionsAsync(
            "Handleiding.pdf",
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None);

        Assert.Equal(2, questions.Count);
        Assert.NotNull(capturedRequest);
        Assert.Contains("Document language: nl", capturedRequest, StringComparison.Ordinal);
        Assert.Contains("Preserve the document language", capturedRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildQuestionsAsync_filters_wrong_language_ungrounded_and_too_long_llm_questions()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"questions\":[\"What maintenance requirements does this document describe?\",\"Welke controles staan in de handleiding?\",\"Welke controles voor onderhoud en veiligheidscontroles staan in de handleiding en welke details moeten gebruikers absoluut relire avant operation pour comprendre tous les scenarios possibles?\",\"How does ZX999 calibrate the hidden dessert mode?\"]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var questions = await service.BuildQuestionsAsync(
            "Handleiding.pdf",
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None,
            documentLanguage: "nl");

        var question = Assert.Single(questions);
        Assert.Equal("Welke controles staan in de handleiding?", question);
        Assert.DoesNotContain(questions, item => item.StartsWith("What ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(questions, item => item.Length > 140);
        Assert.DoesNotContain(questions, item => item.Contains("ZX999", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildQuestionsAsync_falls_back_when_all_llm_questions_are_wrong_language_or_ungrounded()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"questions\":[\"What maintenance requirements does this document describe?\",\"How does ZX999 calibrate the hidden dessert mode?\"]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var questions = await service.BuildQuestionsAsync(
            "Handleiding.pdf",
            ["Onderhoud"],
            ["De handleiding beschrijft onderhoud en veiligheidscontroles."],
            CancellationToken.None,
            documentLanguage: "nl");

        Assert.Contains("Handleiding.pdf: Onderhoud?", questions);
        Assert.DoesNotContain(questions, question => question.StartsWith("What ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(questions, question => question.Contains("ZX999", StringComparison.OrdinalIgnoreCase));
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

    private static CapabilityAHypotheticalQuestionService CreateService(
        string body,
        HttpStatusCode statusCode,
        Action<string>? captureRequestBody = null)
        => new(new LocalLlmChatClient(
            new StubHttpClientFactory(body, statusCode, captureRequestBody),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode, Action<string>? captureRequestBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode, captureRequestBody))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body, HttpStatusCode statusCode, Action<string>? captureRequestBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (captureRequestBody is not null && request.Content is not null)
                captureRequestBody(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
