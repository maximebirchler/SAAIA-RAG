using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Backend;

internal sealed partial class DocumentProfileEnrichmentService
{
    private readonly LocalLlmChatClient _llmClient;

    public DocumentProfileEnrichmentService(LocalLlmChatClient llmClient)
    {
        _llmClient = llmClient;
    }

    internal async Task<ProjectedDocumentProfile?> BuildEnrichedProfileAsync(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        if (!_llmClient.IsConfigured)
            return null;

        var targetLanguage = ResolveOutputLanguage(baseline.Language, doc, sectionTitles, excerpts);
        var systemPrompt = """
Create a compact retrieval profile. Return JSON only:
{"language":"BCP-47 language tag or und","summary":"...","keywords":["..."],"entities":["..."],"topics":["..."],"questions":["..."],"limits":["..."],"cards":[{"title":"...","pageStart":1,"pageEnd":1,"kind":"content_item","signals":["..."],"evidence":{"schemaVersion":"content_card_evidence_v1","language":"BCP-47 or und","scaleBasis":{"count":4,"label":"items"},"quantityFacts":[{"value":10,"unit":"kg","label":"load","sourceText":"short exact source span"}],"nonScalableReasons":["safety_or_parameter_context"],"facts":[{"kind":"requirement|procedure|definition|parameter|quantity|entity|general","label":"...","value":"...","unit":"...","sourceText":"short exact source span","pageStart":1,"pageEnd":1,"confidence":0.8}]}}]}
Use only provided data. No invented facts, page refs, quantities, or certifications. Summary max 80 words. Arrays must be short and deduplicated. Cards must describe concrete reusable content found in the provided section titles, excerpts, or baseline cards.
Prefer cards that help retrieve a specific reusable item, procedure, section, table, policy, configuration, workflow, or checklist.
Evidence is optional and corpus-agnostic. Add scaleBasis, quantityFacts, nonScalableReasons, and facts only when directly grounded in the provided section titles, excerpts, or baseline cards. Use short exact sourceText when available.
Use nonScalableReasons only for grounded safety, compliance, technical parameter, threshold, pressure, temperature, speed, voltage, page, time, or currency contexts. Do not invent custom reason labels.
Write summary, questions, limits, and natural-language topics in the document language requested by the prompt.
When extraction/OCR quality requires review, include a short limits entry in the requested document language and do not treat the source excerpts as a complete normal source.
""";

        var userPrompt = BuildUserPrompt(doc, baseline, sectionTitles, excerpts, targetLanguage);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 1000, temperature: 0.1, ct);
        if (!TryParseProfile(completion, out var parsed))
            return null;

        var summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? baseline.SummaryText
            : parsed.Summary!;
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        var groundingCorpus = BuildGroundingCorpus(doc, baseline, sectionTitles, excerpts);
        var profileLanguage = ResolveProfileLanguage(parsed.Language, targetLanguage);
        var parsedLimits = FilterGroundedQuestions(parsed.Limits, groundingCorpus, allowGenericNoSignal: true);
        var extractionQualityLimits = CapabilityBExtractionQualityPrompting.BuildProfileLimits(doc.ExtractionQuality, profileLanguage);
        return DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: profileLanguage,
            summaryText: summary,
            keywords: Merge(FilterGroundedTerms(parsed.Keywords, groundingCorpus), baseline.Keywords, 32),
            entities: Merge(FilterGroundedTerms(parsed.Entities, groundingCorpus), baseline.Entities, 32),
            topics: Merge(FilterGroundedTerms(parsed.Topics, groundingCorpus), baseline.Topics, 20),
            hypotheticalQuestions: Merge(FilterGroundedQuestions(parsed.Questions, groundingCorpus, allowGenericNoSignal: false), baseline.HypotheticalQuestions, 10),
            limits: Merge(extractionQualityLimits.Concat(parsedLimits).ToArray(), baseline.Limits, 8),
            docPath: doc.DocPath,
            docName: doc.DocName,
            contentCards: MergeCards(
                FilterGroundedCards(parsed.Cards, doc, groundingCorpus),
                baseline.ContentCards ?? []));
    }

    private static string BuildUserPrompt(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string targetLanguage)
        => $"""
Doc: {doc.DocName}
Path: {doc.DocPath}
Category: {doc.Category ?? "unknown"}; pages: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}; indexedVersion: {doc.IndexedVersion}
Baseline language: {baseline.Language}
Document/output language: {targetLanguage}
{CapabilityBExtractionQualityPrompting.BuildPromptBlock(doc.ExtractionQuality)}
Baseline summary: {TrimText(baseline.SummaryText, 420)}
Keywords: {string.Join(", ", baseline.Keywords.Take(16))}
Entities: {string.Join(", ", baseline.Entities.Take(12))}
Topics: {string.Join(", ", baseline.Topics.Take(8))}
Questions: {string.Join(" | ", baseline.HypotheticalQuestions.Take(4))}
Baseline cards: {string.Join(" | ", (baseline.ContentCards ?? []).Take(30).Select(FormatPromptCard))}
Sections: {string.Join(" | ", sectionTitles.Where(static title => !string.IsNullOrWhiteSpace(title)).Select(static title => title.Trim()).Take(12))}
Representative excerpts: {string.Join(" | ", excerpts.Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt)).Select(TrimExcerpt).Take(8))}
""";

    private static bool TryParseProfile(string? content, out ParsedProfile profile)
    {
        profile = default;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var cleaned = ExtractJsonObject(StripCodeFence(content.Trim()));
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        try
        {
            using var json = JsonDocument.Parse(cleaned);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = json.RootElement;
            profile = new ParsedProfile(
                Language: ReadString(root, "language"),
                Summary: ReadString(root, "summary"),
                Keywords: ReadArray(root, "keywords"),
                Entities: ReadArray(root, "entities"),
                Topics: ReadArray(root, "topics"),
                Questions: ReadArray(root, "questions"),
                Limits: ReadArray(root, "limits"),
                Cards: ReadCards(root, "cards"));
            return !string.IsNullOrWhiteSpace(profile.Summary)
                || profile.Keywords.Count > 0
                || profile.Topics.Count > 0
                || profile.Questions.Count > 0
                || profile.Cards.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeLlmJsonText(value.GetString())
            : null;

    private static IReadOnlyList<string> ReadArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => NormalizeLlmJsonText(item.GetString()))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> Merge(
        IReadOnlyList<string> preferred,
        IReadOnlyList<string> fallback,
        int maxItems)
        => preferred
            .Concat(fallback)
            .Select(NormalizeText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();

    private static IReadOnlyList<DocumentProfileContentCard> MergeCards(
        IReadOnlyList<DocumentProfileContentCard> preferred,
        IReadOnlyList<DocumentProfileContentCard> fallback)
        => preferred
            .Concat(fallback)
            .GroupBy(card => NormalizeText(card.Title)?.ToLowerInvariant() ?? string.Empty, StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => MergeCardGroup(group))
            .Take(240)
            .ToArray();

    private static DocumentProfileContentCard MergeCardGroup(IEnumerable<DocumentProfileContentCard> group)
    {
        var cards = group.ToArray();
        var primary = cards[0];
        var evidence = cards.Select(static card => card.Evidence).FirstOrDefault(static evidence => evidence is not null);
        var signals = cards
            .SelectMany(static card => card.Signals ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return primary with
        {
            Evidence = primary.Evidence ?? evidence,
            Signals = signals.Length == 0 ? primary.Signals : signals
        };
    }

    private static IReadOnlyList<DocumentProfileContentCard> FilterGroundedCards(
        IReadOnlyList<DocumentProfileContentCard> cards,
        CapabilityBDocumentRow doc,
        string groundingCorpus)
    {
        if (cards.Count == 0)
            return cards;

        if (string.IsNullOrWhiteSpace(groundingCorpus))
            return [];

        return cards
            .Select(card => NormalizeCardPageRange(card, doc.PageCount))
            .Select(card => card with { Evidence = NormalizeEvidencePageRanges(FilterGroundedEvidence(card.Evidence, groundingCorpus), doc.PageCount) })
            .Select(SanitizeUngroundedStructuredSignals)
            .Where(card => card.Evidence is not null || IsGroundedCard(card, groundingCorpus))
            .ToArray();
    }

    private static DocumentProfileContentCard SanitizeUngroundedStructuredSignals(DocumentProfileContentCard card)
    {
        if (card.Evidence is not null || card.Signals.Count == 0)
            return card;

        var signals = card.Signals
            .Where(static signal => !IsEvidenceDerivedStructuredSignal(signal))
            .ToArray();
        return signals.Length == card.Signals.Count
            ? card
            : card with { Signals = signals };
    }

    private static bool IsEvidenceDerivedStructuredSignal(string? signal)
    {
        var normalized = StructuredContentLexicon.NormalizeStructuredSignalLabel(signal);
        return normalized is
            "structured_facts" or
            "scale_basis" or
            "quantity_list" or
            "scalable_quantities" or
            "non_scalable_quantities";
    }

    private static DocumentProfileCardEvidence? FilterGroundedEvidence(
        DocumentProfileCardEvidence? evidence,
        string groundingCorpus)
    {
        if (evidence is null || string.IsNullOrWhiteSpace(groundingCorpus))
            return null;

        var quantityFacts = (evidence.QuantityFacts ?? [])
            .Where(fact => IsGroundedEvidenceText($"{fact.Label} {fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Unit} {fact.SourceText}", groundingCorpus))
            .ToArray();
        var facts = (evidence.Facts ?? [])
            .Where(fact => IsGroundedEvidenceText($"{fact.Label} {fact.Value} {fact.Unit} {fact.SourceText}", groundingCorpus))
            .ToArray();
        var hasNonScalableContext = StructuredContentLexicon.ContainsNonScalableQuantityContext(groundingCorpus);
        var nonScalableReasons = (evidence.NonScalableReasons ?? [])
            .Where(reason => IsGroundedEvidenceText(reason, groundingCorpus)
                || (hasNonScalableContext && IsKnownNonScalableReason(reason)))
            .ToArray();
        var scaleBasis = evidence.ScaleBasis is { } basis
            && (quantityFacts.Length > 0
                || facts.Any(static fact => string.Equals(fact.Kind, "scale_basis", StringComparison.OrdinalIgnoreCase))
                || IsGroundedEvidenceText($"{basis.Count} {basis.Label}", groundingCorpus))
            ? evidence.ScaleBasis
            : null;

        if (scaleBasis is null && quantityFacts.Length == 0 && facts.Length == 0 && nonScalableReasons.Length == 0)
            return null;

        return new DocumentProfileCardEvidence(
            SchemaVersion: string.IsNullOrWhiteSpace(evidence.SchemaVersion) ? "content_card_evidence_v1" : evidence.SchemaVersion,
            ScaleBasis: scaleBasis,
            QuantityFacts: quantityFacts,
            NonScalableReasons: nonScalableReasons,
            Confidence: evidence.Confidence is >= 0 and <= 1 ? evidence.Confidence : null,
            Language: NormalizeLanguage(evidence.Language, "und"),
            Facts: facts);
    }

    private static DocumentProfileCardEvidence? NormalizeEvidencePageRanges(
        DocumentProfileCardEvidence? evidence,
        int? pageCount)
    {
        if (evidence is null || pageCount is not > 0 || (evidence.Facts ?? []).Count == 0)
            return evidence;

        var facts = evidence.Facts!
            .Select(fact => NormalizeEvidenceFactPageRange(fact, pageCount.Value))
            .ToArray();
        return evidence with { Facts = facts };
    }

    private static DocumentProfileEvidenceFact NormalizeEvidenceFactPageRange(
        DocumentProfileEvidenceFact fact,
        int pageCount)
    {
        var pageStart = fact.PageStart is > 0 && fact.PageStart <= pageCount ? fact.PageStart : null;
        var pageEnd = fact.PageEnd is > 0 && fact.PageEnd <= pageCount ? fact.PageEnd : null;
        if (pageStart is > 0 && pageEnd is > 0 && pageEnd < pageStart)
            pageEnd = pageStart;

        return fact with
        {
            PageStart = pageStart,
            PageEnd = pageEnd
        };
    }

    private static bool IsGroundedEvidenceText(string? value, string groundingCorpus)
    {
        var normalized = NormalizeForGrounding(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Length >= 4 && groundingCorpus.Contains(normalized, StringComparison.Ordinal))
            return true;

        var tokens = BuildGroundingTokens(normalized);
        if (tokens.Length == 0)
            return false;

        var matched = tokens.Count(token => TokenOccursInCorpus(token, groundingCorpus));
        return matched >= Math.Min(2, tokens.Length)
            && matched >= (int)Math.Ceiling(tokens.Length * 0.60d);
    }

    private static bool IsKnownNonScalableReason(string? value)
        => string.Equals(
            StructuredContentLexicon.NormalizeStructuredSignalLabel(value),
            "safety_or_parameter_context",
            StringComparison.Ordinal);

    private static IReadOnlyList<string> FilterGroundedTerms(IReadOnlyList<string> terms, string groundingCorpus)
    {
        if (terms.Count == 0)
            return terms;
        if (string.IsNullOrWhiteSpace(groundingCorpus))
            return [];

        return terms
            .Where(term => IsGroundedTerm(term, groundingCorpus))
            .ToArray();
    }

    private static IReadOnlyList<string> FilterGroundedQuestions(IReadOnlyList<string> values, string groundingCorpus, bool allowGenericNoSignal)
    {
        if (values.Count == 0)
            return values;
        if (string.IsNullOrWhiteSpace(groundingCorpus))
            return [];

        return values
            .Where(value => IsGroundedQuestion(value, groundingCorpus, allowGenericNoSignal))
            .ToArray();
    }

    private static bool IsGroundedQuestion(string value, string groundingCorpus, bool allowGenericNoSignal)
    {
        var normalized = NormalizeForGrounding(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (Match match in QuestionReferenceRegex().Matches(value))
        {
            var reference = NormalizeForGrounding(match.Value);
            if (!string.IsNullOrWhiteSpace(reference) && !groundingCorpus.Contains(reference, StringComparison.Ordinal))
                return false;
        }

        var tokens = BuildGroundingTokens(normalized);
        if (tokens.Length == 0)
            return allowGenericNoSignal;

        var matched = tokens.Count(token => TokenOccursInCorpus(token, groundingCorpus));
        if (tokens.Length <= 3)
            return matched == tokens.Length;

        return matched >= 3 && matched >= (int)Math.Ceiling(tokens.Length * 0.75d);
    }

    private static bool IsGroundedTerm(string term, string groundingCorpus)
    {
        var normalized = NormalizeForGrounding(term);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 3)
            return false;

        if (groundingCorpus.Contains(normalized, StringComparison.Ordinal))
            return true;

        var tokens = BuildGroundingTokens(normalized);
        if (tokens.Length == 0)
            return false;
        if (tokens.Length == 1)
            return TokenOccursInCorpus(tokens[0], groundingCorpus);

        var matched = tokens.Count(token => TokenOccursInCorpus(token, groundingCorpus));
        return matched >= 2 && matched >= (int)Math.Ceiling(tokens.Length * 0.55d);
    }

    private static string BuildGroundingCorpus(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var sb = new StringBuilder();
        sb.AppendLine(doc.DocName);
        sb.AppendLine(doc.DocPath);
        sb.AppendLine(doc.Category);
        sb.AppendLine(baseline.SummaryText);
        sb.AppendLine(string.Join(' ', baseline.Keywords));
        sb.AppendLine(string.Join(' ', baseline.Entities));
        sb.AppendLine(string.Join(' ', baseline.Topics));
        sb.AppendLine(string.Join(' ', baseline.HypotheticalQuestions));
        foreach (var card in baseline.ContentCards ?? [])
        {
            sb.AppendLine(card.Title);
            sb.AppendLine(string.Join(' ', card.Signals));
            sb.AppendLine(FormatPromptEvidence(card.Evidence));
        }

        foreach (var title in sectionTitles)
            sb.AppendLine(title);
        foreach (var excerpt in excerpts)
            sb.AppendLine(excerpt);

        return NormalizeForGrounding(sb.ToString());
    }

    private static DocumentProfileContentCard NormalizeCardPageRange(DocumentProfileContentCard card, int? pageCount)
    {
        var pageStart = card.PageStart;
        var pageEnd = card.PageEnd;
        if (pageCount is > 0)
        {
            pageStart = pageStart is > 0 && pageStart <= pageCount.Value ? pageStart : null;
            pageEnd = pageEnd is > 0 && pageEnd <= pageCount.Value ? pageEnd : null;
        }

        if (pageStart is > 0 && pageEnd is > 0 && pageEnd < pageStart)
            pageEnd = pageStart;

        return card with { PageStart = pageStart, PageEnd = pageEnd };
    }

    private static bool IsGroundedCard(DocumentProfileContentCard card, string groundingCorpus)
    {
        var normalizedTitle = NormalizeForGrounding(card.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || normalizedTitle.Length < 4)
            return false;

        if (groundingCorpus.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTokens = BuildGroundingTokens(normalizedTitle);
        if (titleTokens.Length == 0)
            return false;

        var matchedTitleTokens = titleTokens.Count(token => TokenOccursInCorpus(token, groundingCorpus));
        if (titleTokens.Length <= 2)
            return matchedTitleTokens == titleTokens.Length;

        if (matchedTitleTokens >= 2 && matchedTitleTokens >= (int)Math.Ceiling(titleTokens.Length * 0.55d))
            return true;

        var signalTokens = BuildGroundingTokens(string.Join(' ', card.Signals));
        return signalTokens.Length > 0
            && signalTokens.Count(token => TokenOccursInCorpus(token, groundingCorpus)) >= Math.Min(2, signalTokens.Length);
    }

    private static string[] BuildGroundingTokens(string value)
        => NormalizeForGrounding(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !GroundingStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();

    private static bool TokenOccursInCorpus(string token, string groundingCorpus)
        => groundingCorpus.Contains(token, StringComparison.Ordinal)
            || (token.Length > 5 && groundingCorpus.Contains(token.TrimEnd('s'), StringComparison.Ordinal))
            || (token.Length > 5 && groundingCorpus.Contains(token.TrimEnd('e', 's'), StringComparison.Ordinal));

    private static string NormalizeForGrounding(string? value)
        => FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value ?? string.Empty));

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IReadOnlyList<DocumentProfileContentCard> ReadCards(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<DocumentProfileContentCard>();

        var cards = new List<DocumentProfileContentCard>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            cards.Add(new DocumentProfileContentCard(
                title,
                ReadInt(item, "pageStart"),
                ReadInt(item, "pageEnd"),
                ReadString(item, "kind") ?? "llm_content_card",
                ReadArray(item, "signals"),
                ReadCardEvidence(item)));
        }

        return cards;
    }

    private static DocumentProfileCardEvidence? ReadCardEvidence(JsonElement item)
    {
        if (!item.TryGetProperty("evidence", out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        return DocumentProfileProjector.ParseContentCardEvidenceFromMetadata(value.GetRawText());
    }

    private static int? ReadInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) && parsed > 0
            ? parsed
            : null;

    private static string FormatPromptCard(DocumentProfileContentCard card)
    {
        var pages = card.PageStart is > 0
            ? $" p.{card.PageStart}{(card.PageEnd is > 0 && card.PageEnd != card.PageStart ? "-" + card.PageEnd : string.Empty)}"
            : string.Empty;
        var evidence = FormatPromptEvidence(card.Evidence);
        return $"{card.Title}{pages} [{string.Join(", ", card.Signals.Take(4))}]{(string.IsNullOrWhiteSpace(evidence) ? string.Empty : " evidence: " + evidence)}";
    }

    private static string FormatPromptEvidence(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();
        if (evidence.ScaleBasis is { Count: > 0 } basis)
            parts.Add($"scale_basis {basis.Count} {basis.Label}".Trim());
        parts.AddRange((evidence.QuantityFacts ?? []).Take(6).Select(static fact =>
            $"{fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Unit} {fact.Label} {fact.SourceText}".Trim()));
        parts.AddRange((evidence.NonScalableReasons ?? []).Take(4));
        parts.AddRange((evidence.Facts ?? []).Take(6).Select(static fact =>
            $"{fact.Kind} {fact.Label} {fact.Value} {fact.Unit} {fact.SourceText}".Trim()));
        return TrimText(string.Join(" | ", parts.Where(static part => !string.IsNullOrWhiteSpace(part))), 420);
    }

    private static string TrimExcerpt(string value)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        return TrimText(normalized, 260);
    }

    private static string TrimText(string value, int maxLength)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        if (normalized.Length <= maxLength)
            return normalized;

        var cut = normalized.LastIndexOf(' ', Math.Max(0, maxLength - 1));
        if (cut < maxLength / 3)
            cut = maxLength;
        return normalized[..cut].TrimEnd();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string? NormalizeLlmJsonText(string? value)
        => NormalizeText(TextEncodingSanitizer.RepairCommonMojibake(value ?? string.Empty));

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        var match = CodeFenceRegex().Match(trimmed);
        return match.Success ? match.Groups["json"].Value.Trim() : trimmed;
    }

    private static string? ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start
            ? trimmed[start..(end + 1)].Trim()
            : null;
    }

    private static string NormalizeLanguage(string? preferred, string fallback)
    {
        var language = DocumentLanguageResolver.NormalizeLanguageTag(string.IsNullOrWhiteSpace(preferred) ? fallback : preferred);
        if (!string.IsNullOrWhiteSpace(language))
            return language;

        var normalizedFallback = DocumentLanguageResolver.NormalizeLanguageTag(fallback);
        return string.IsNullOrWhiteSpace(normalizedFallback) ? "und" : normalizedFallback;
    }

    private static string ResolveProfileLanguage(string? parsedLanguage, string targetLanguage)
    {
        var normalizedTarget = NormalizeLanguage(targetLanguage, "und");
        if (!string.Equals(normalizedTarget, "und", StringComparison.Ordinal))
            return normalizedTarget;

        return NormalizeLanguage(parsedLanguage, normalizedTarget);
    }

    private static string ResolveOutputLanguage(
        string? preferredLanguage,
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var normalizedPreferred = NormalizeLanguage(preferredLanguage, "und");
        if (!string.Equals(normalizedPreferred, "und", StringComparison.Ordinal))
            return normalizedPreferred;

        var normalizedProfileLanguage = NormalizeLanguage(doc.ProfileLanguage, "und");
        if (!string.Equals(normalizedProfileLanguage, "und", StringComparison.Ordinal))
            return normalizedProfileLanguage;

        return DetectDominantLanguage(doc, sectionTitles, excerpts) ?? "und";
    }

    private static string? DetectDominantLanguage(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var corpus = string.Join(' ', new[]
        {
            doc.DocName,
            doc.DocPath,
            doc.Category ?? string.Empty,
            string.Join(' ', sectionTitles),
            string.Join(' ', excerpts)
        });

        return DocumentLanguageResolver.DetectDominantLanguage(corpus);
    }

    private readonly record struct ParsedProfile(
        string? Language,
        string? Summary,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> Entities,
        IReadOnlyList<string> Topics,
        IReadOnlyList<string> Questions,
        IReadOnlyList<string> Limits,
        IReadOnlyList<DocumentProfileContentCard> Cards);

    [GeneratedRegex(@"^```(?:json)?\s*(?<json>.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"\b(?:[A-Za-z]{2,}\d{2,}[A-Za-z0-9\-]*|\d{2,}[A-Za-z]{2,}[A-Za-z0-9\-]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionReferenceRegex();

    private static readonly HashSet<string> GroundingStopwords = new(StringComparer.Ordinal)
    {
        "about", "avec", "cette", "dans", "document", "documents", "from", "pour",
        "section", "sections", "that", "this", "with", "sobre", "para", "esta",
        "este", "questo", "questa", "dokument", "seite", "pages", "page",
        "what", "which", "does", "says", "discuss", "describe", "describes",
        "comment", "quels", "quelles", "quoi", "points", "cles", "principaux",
        "cuales", "quais", "welche", "wichtigsten", "quali", "punti",
        "use", "using", "exact", "facts", "fact", "parameter", "parameters",
        "chunks", "chunk", "provided", "available"
    };
}
