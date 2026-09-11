using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveBackofficeProfileEnrichmentBenchmarkTests(
    ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Live_qwen3_enriches_current_backoffice_profile_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PHASE2_BACKOFFICE_PROFILE_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_PHASE2_BACKOFFICE_PROFILE_BENCHMARK=1 to run EXP-032/N.0.");
            return;
        }

        var inputPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_BACKOFFICE_INPUT_PATH"));
        var llmBaseUrl = RequireEnvironment(
            "SAAIA_VALIDATION_LLM_BASE_URL");
        var model = RequireEnvironment(
            "SAAIA_VALIDATION_LLM_MODEL");
        var artifactPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_BACKOFFICE_ARTIFACT"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var inputBytes = await File.ReadAllBytesAsync(
            inputPath,
            CancellationToken.None);
        var inputJson = Encoding.UTF8.GetString(inputBytes);
        var input = JsonSerializer.Deserialize<LiveBackofficeInput>(
            inputJson,
            JsonOptions)
            ?? throw new InvalidOperationException("EXP-032 input is invalid.");
        var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes))
            .ToLowerInvariant();

        var baselineCards = input.Baseline.ContentCards
            .Select(static card => new DocumentProfileContentCard(
                card.Title,
                card.PageStart,
                card.PageEnd,
                card.Kind,
                card.Signals,
                card.Evidence is { ValueKind: JsonValueKind.Object } evidence
                    ? DocumentProfileProjector
                        .ParseContentCardEvidenceFromMetadata(
                            evidence.GetRawText())
                    : null,
                card.ContentCardId))
            .ToArray();
        var baseline = new DocumentProfileSnapshot(
            input.Baseline.RevisionId,
            input.Document.DocId,
            input.Baseline.ProfileVersion,
            input.Baseline.Language,
            input.Baseline.SummaryText,
            input.Baseline.Keywords,
            input.Baseline.Entities,
            input.Baseline.Topics,
            input.Baseline.HypotheticalQuestions,
            input.Baseline.Limits,
            input.Baseline.SearchText,
            input.Baseline.TokenCount,
            baselineCards);
        var document = new CapabilityBDocumentRow(
            input.Document.DocId,
            input.Document.DocPath,
            input.Document.DocName,
            input.Document.Category,
            input.Document.PageCount,
            input.Document.IndexedVersion,
            input.Baseline.Language,
            input.Document.SourceHash,
            input.Baseline.RevisionId);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var httpFactory = new CapturingHttpClientFactory(llmBaseUrl);
        var llmClient = new LocalLlmChatClient(
            httpFactory,
            new ChatOptions
            {
                LlmBaseUrl = EnsureTrailingSlash(llmBaseUrl),
                LlmModel = model,
                LlmApiMode = "chat_completions",
                LlmPromptFormat = "chatml",
                LlmMaxTokens = 1000,
                LlmTimeoutSeconds = 720,
                LlmFirstTokenTimeoutSeconds = 120
            });
        var service = new DocumentProfileEnrichmentService(llmClient);
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var projected = await service.BuildEnrichedProfileAsync(
            document,
            baseline,
            input.SectionTitles,
            input.Excerpts,
            cts.Token);
        wall.Stop();

        var completion = TryReadCompletionContent(httpFactory.ResponseBody);
        var proposedCards = ReadProposedCards(completion);
        var projectedCards = projected?.ContentCards?.ToArray()
            ?? Array.Empty<DocumentProfileContentCard>();
        var proposedTitles = proposedCards
            .Select(static card => NormalizeTitle(card.Title))
            .Where(static title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var retainedLlmCards = projectedCards
            .Where(card => proposedTitles.Contains(NormalizeTitle(card.Title)))
            .ToArray();
        var retainedTitles = retainedLlmCards
            .Select(static card => NormalizeTitle(card.Title))
            .ToHashSet(StringComparer.Ordinal);
        var filteredProposedCards = proposedCards
            .Where(card => !retainedTitles.Contains(NormalizeTitle(card.Title)))
            .ToArray();
        var baselineTitles = baselineCards
            .Select(static card => NormalizeTitle(card.Title))
            .ToHashSet(StringComparer.Ordinal);
        var newOrReplacedCards = retainedLlmCards
            .Where(card => !baselineTitles.Contains(NormalizeTitle(card.Title))
                           || baselineCards.Any(baselineCard =>
                               string.Equals(
                                   NormalizeTitle(baselineCard.Title),
                                   NormalizeTitle(card.Title),
                                   StringComparison.Ordinal)
                               && !string.Equals(
                                   baselineCard.Kind,
                                   card.Kind,
                                   StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var report = new
        {
            experiment = "EXP-032",
            variant = "N.0",
            generatedAt = DateTimeOffset.Now,
            approval = "TESTE_NON_APPROUVE",
            input = new
            {
                sha256 = inputHash,
                path = inputPath,
                input.Document.DocPath,
                input.Document.IndexedVersion,
                input.Document.PageCount,
                input.Baseline.ProfileVersion,
                baselineCardCount = baselineCards.Length,
                sectionTitleCount = input.SectionTitles.Length,
                excerptCount = input.Excerpts.Length,
                exact = input
            },
            configuration = new
            {
                llmBaseUrl,
                model,
                maxTokens = 1000,
                temperature = 0.1,
                timeoutMinutes = 12
            },
            transport = new
            {
                httpFactory.StatusCode,
                httpFactory.RequestBody,
                httpFactory.ResponseBody,
                completion
            },
            result = new
            {
                profileCreated = projected is not null,
                profileVersion = projected?.ProfileVersion,
                projectedCardCount = projectedCards.Length,
                proposedCardCount = proposedCards.Count,
                retainedLlmCardCount = retainedLlmCards.Length,
                filteredProposedCardCount = filteredProposedCards.Length,
                newOrReplacedCardCount = newOrReplacedCards.Length,
                proposedCards,
                retainedLlmCards,
                filteredProposedCards,
                newOrReplacedCards,
                projectedCards
            },
            timing = new
            {
                wallMilliseconds = wall.ElapsedMilliseconds
            },
            humanInspection = new
            {
                status = "A_FAIRE",
                instruction =
                    "Verifier chaque carte proposee/retenue, son autonomie, son grounding et sa page avant toute correction produit."
            }
        };
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(report, JsonOptions),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"N.0 profile: {projected is not null}; proposed: "
            + $"{proposedCards.Count}; retained: {retainedLlmCards.Length}; "
            + $"new/replaced: {newOrReplacedCards.Length}; "
            + $"wall: {wall.ElapsedMilliseconds} ms");

        Assert.Equal(HttpStatusCode.OK, httpFactory.StatusCode);
        Assert.NotNull(projected);
        Assert.Equal("llm_backoffice_v1", projected!.ProfileVersion);
    }

    private static string RequireEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(name + " is required.");

    private static string EnsureTrailingSlash(string value)
        => value.Trim().TrimEnd('/') + "/";

    private static string NormalizeTitle(string? value)
        => string.Join(
                ' ',
                (value ?? string.Empty).Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();

    private static string? TryReadCompletionContent(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (!json.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return null;
            }

            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ProposedCard> ReadProposedCards(
        string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
            return Array.Empty<ProposedCard>();

        var value = completion.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                value = value[(firstNewLine + 1)..lastFence].Trim();
        }
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start >= 0 && end > start)
            value = value[start..(end + 1)];

        try
        {
            using var json = JsonDocument.Parse(value);
            if (!json.RootElement.TryGetProperty("cards", out var cards)
                || cards.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ProposedCard>();
            }

            return cards.EnumerateArray()
                .Select(static card => new ProposedCard(
                    card.TryGetProperty("title", out var title)
                        ? title.GetString() ?? string.Empty
                        : string.Empty,
                    card.TryGetProperty("pageStart", out var pageStart)
                    && pageStart.TryGetInt32(out var parsedPageStart)
                        ? parsedPageStart
                        : null,
                    card.TryGetProperty("pageEnd", out var pageEnd)
                    && pageEnd.TryGetInt32(out var parsedPageEnd)
                        ? parsedPageEnd
                        : null,
                    card.TryGetProperty("kind", out var kind)
                        ? kind.GetString() ?? string.Empty
                        : string.Empty,
                    card.TryGetProperty("evidence", out var evidence)
                        ? evidence.Clone()
                        : null))
                .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<ProposedCard>();
        }
    }

    private sealed class CapturingHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public CapturingHttpClientFactory(string baseUrl)
        {
            var handler = new CapturingHandler(this)
            {
                InnerHandler = new HttpClientHandler()
            };
            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri(EnsureTrailingSlash(baseUrl)),
                Timeout = TimeSpan.FromMinutes(12)
            };
        }

        public string? RequestBody { get; private set; }
        public string? ResponseBody { get; private set; }
        public HttpStatusCode? StatusCode { get; private set; }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();

        private sealed class CapturingHandler(
            CapturingHttpClientFactory owner) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner.RequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var response = await base.SendAsync(request, cancellationToken);
                owner.StatusCode = response.StatusCode;
                owner.ResponseBody = await response.Content
                    .ReadAsStringAsync(cancellationToken);
                response.Content = new StringContent(
                    owner.ResponseBody,
                    Encoding.UTF8,
                    "application/json");
                return response;
            }
        }
    }

    private sealed record ProposedCard(
        string Title,
        int? PageStart,
        int? PageEnd,
        string Kind,
        JsonElement? Evidence);

    private sealed record LiveBackofficeInput(
        LiveDocument Document,
        LiveBaseline Baseline,
        string[] SectionTitles,
        string[] Excerpts);

    private sealed record LiveDocument(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int? PageCount,
        int IndexedVersion,
        string? SourceHash);

    private sealed record LiveBaseline(
        Guid RevisionId,
        string ProfileVersion,
        string Language,
        string SummaryText,
        string[] Keywords,
        string[] Entities,
        string[] Topics,
        string[] HypotheticalQuestions,
        string[] Limits,
        string SearchText,
        int TokenCount,
        LiveContentCard[] ContentCards);

    private sealed record LiveContentCard(
        string? ContentCardId,
        string Title,
        int? PageStart,
        int? PageEnd,
        string Kind,
        string[] Signals,
        JsonElement? Evidence);
}
