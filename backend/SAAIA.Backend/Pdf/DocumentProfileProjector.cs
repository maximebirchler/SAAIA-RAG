using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static partial class DocumentProfileProjector
{
    private const int MaxSummaryChars = 1100;
    private const int MaxContentCards = 240;
    private const int LeadTitleCompactHeadLength = 220;
    private const int EmbeddedTitleScanLength = 1200;
    private const int PageEmbeddedTitleScanLength = 2400;
    private const int MaxUnitLeadContentCardCandidates = 8;
    private const int MaxPageContentCardCandidates = 8;
    private const int MaxExactLeadContentCardCandidates = 4;
    private const int MaxEvidenceDerivedContentCardPageSpan = 8;

    public static ProjectedDocumentProfile Project(
        string docPath,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries)
    {
        var docName = Path.GetFileName(docPath.Replace('\\', '/'));
        var profileUnits = SelectProfileContentUnits(units);
        var corpus = BuildCorpus(docName, sections, profileUnits);
        var language = DetectLanguage(corpus);
        var sectionTitles = sections
            .OrderBy(static section => section.Ordinal)
            .Select(static section => CollapseWhitespace(section.Title))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        var keywords = ExtractKeywords(corpus, maxKeywords: 24);
        var entities = ExtractEntities(docName, exactMatchEntries, corpus, maxEntities: 24);
        var topics = sectionTitles
            .Concat(keywords.Take(8))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(14)
            .ToArray();
        var summary = BuildSummary(docName, pages.Count, sectionTitles, profileUnits, language);
        var hypotheticalQuestions = BuildHypotheticalQuestions(docName, keywords, entities, language);
        var limits = BuildLimits(language);
        var contentCards = BuildContentCards(sections, units, pages, exactMatchEntries, keywords);
        return BuildProfile(
            profileVersion: "deterministic_v1",
            language,
            summaryText: summary,
            keywords,
            entities,
            topics,
            hypotheticalQuestions,
            limits,
            docPath,
            docName,
            contentCards);
    }

    internal static ProjectedDocumentProfile BuildProfile(
        string profileVersion,
        string? language,
        string summaryText,
        IEnumerable<string> keywords,
        IEnumerable<string> entities,
        IEnumerable<string> topics,
        IEnumerable<string> hypotheticalQuestions,
        IEnumerable<string> limits,
        string docPath,
        string docName,
        IEnumerable<DocumentProfileContentCard>? contentCards = null)
    {
        var normalizedSummary = TrimTo(summaryText, MaxSummaryChars);
        var normalizedKeywords = NormalizeList(keywords, 32);
        var normalizedEntities = NormalizeList(entities, 32);
        var normalizedTopics = NormalizeList(topics, 20);
        var normalizedQuestions = NormalizeList(hypotheticalQuestions, 10);
        var normalizedLimits = NormalizeList(limits, 8);
        var normalizedContentCards = NormalizeContentCards(contentCards ?? []);
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "und" : language.Trim().ToLowerInvariant();
        var searchText = BuildSearchText(
            docPath,
            docName,
            normalizedSummary,
            normalizedKeywords,
            normalizedEntities,
            normalizedTopics,
            normalizedQuestions,
            normalizedLimits,
            normalizedContentCards);
        var checksum = SHA256.HashData(Encoding.UTF8.GetBytes(searchText));

        return new ProjectedDocumentProfile(
            ProfileVersion: string.IsNullOrWhiteSpace(profileVersion) ? "deterministic_v1" : profileVersion.Trim(),
            Language: normalizedLanguage,
            SummaryText: normalizedSummary,
            Keywords: normalizedKeywords,
            Entities: normalizedEntities,
            Topics: normalizedTopics,
            HypotheticalQuestions: normalizedQuestions,
            Limits: normalizedLimits,
            SearchText: searchText,
            TokenCount: CountTokens(searchText),
            Checksum: checksum,
            ContentCards: normalizedContentCards);
    }

    private static string BuildCorpus(
        string docName,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units)
    {
        var sb = new StringBuilder();
        sb.AppendLine(docName);
        foreach (var section in sections.OrderBy(static section => section.Ordinal).Take(40))
            sb.AppendLine(section.Title);
        foreach (var unit in units.OrderBy(static unit => unit.Ordinal).Take(160))
            sb.AppendLine(unit.Text);
        return sb.ToString();
    }

    private static IReadOnlyList<ExtractedDocumentUnit> SelectProfileContentUnits(IReadOnlyList<ExtractedDocumentUnit> units)
    {
        if (units.Count == 0)
            return units;

        var contentUnits = units
            .Where(static unit => !string.Equals(
                RetrievalContentClassifier.AnalyzeChunk(unit.Text).ContentRole,
                RetrievalContentClassifier.NavigationRole,
                StringComparison.Ordinal))
            .ToArray();

        return contentUnits.Length == 0 ? units : contentUnits;
    }

    private static string DetectLanguage(string text)
        => DocumentLanguageResolver.DetectDominantLanguage(text) ?? "und";

    private static string[] ExtractKeywords(string text, int maxKeywords)
    {
        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return WordRegex().Matches(normalized)
            .Select(static match => match.Value)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !ProfileStopwords.Contains(token))
            .GroupBy(static token => token, StringComparer.Ordinal)
            .Select(static group => new { Term = group.Key, Count = group.Count() })
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.Term, StringComparer.Ordinal)
            .Take(maxKeywords)
            .Select(static item => item.Term)
            .ToArray();
    }

    private static string[] ExtractEntities(
        string docName,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        string corpus,
        int maxEntities)
    {
        var entities = new List<string>();
        entities.AddRange(ExactMatchEntryExtractor.ExtractTargetedReferences(docName));
        entities.AddRange(exactMatchEntries
            .Where(static entry => entry.Kind is "standard_ref" or "code_ref")
            .Select(static entry => entry.Text));
        entities.AddRange(TechnicalIdentifierRegex().Matches(corpus)
            .Select(static match => match.Value));
        entities.AddRange(ReferenceLikeRegex().Matches(corpus)
            .Select(static match => match.Value));

        return entities
            .Select(CollapseWhitespace)
            .Where(static value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static value => value.Length)
            .ThenBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntities)
            .ToArray();
    }

    private static string BuildSummary(
        string docName,
        int pageCount,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<ExtractedDocumentUnit> units,
        string language)
    {
        if (!HasLocalizedProfileTemplate(language))
            return BuildNeutralExtractiveSummary(docName, pageCount, sectionTitles, units);

        var lead = language switch
        {
            "en" => $"Document {docName}, {pageCount} page(s).",
            "es" => $"Documento {docName}, {pageCount} pagina(s).",
            "pt" => $"Documento {docName}, {pageCount} pagina(s).",
            "de" => $"Dokument {docName}, {pageCount} Seite(n).",
            "it" => $"Documento {docName}, {pageCount} pagina/e.",
            _ => $"Document {docName}, {pageCount} page(s)."
        };

        var sb = new StringBuilder(lead);
        if (sectionTitles.Count > 0)
        {
            sb.Append(' ');
            sb.Append(language switch
            {
                "en" => "Main headings: ",
                "es" => "Secciones principales: ",
                "pt" => "Secoes principais: ",
                "de" => "Hauptabschnitte: ",
                "it" => "Sezioni principali: ",
                _ => "Sections principales : "
            });
            sb.Append(string.Join("; ", sectionTitles.Take(6)));
            sb.Append('.');
        }

        var excerpts = units
            .OrderBy(static unit => unit.Ordinal)
            .Select(static unit => CollapseWhitespace(unit.Text))
            .Where(static text => text.Length >= 40)
            .Take(3)
            .ToArray();
        if (excerpts.Length > 0)
        {
            sb.Append(' ');
            sb.Append(language switch
            {
                "en" => "Early excerpts mention: ",
                "es" => "Los primeros extractos mencionan: ",
                "pt" => "Os primeiros excertos mencionam: ",
                "de" => "Fruehe Auszuege erwaehnen: ",
                "it" => "I primi estratti menzionano: ",
                _ => "Premiers extraits : "
            });
            sb.Append(string.Join(" / ", excerpts.Select(excerpt => TrimTo(excerpt, 180))));
        }

        return TrimTo(CollapseWhitespace(sb.ToString()), MaxSummaryChars);
    }

    private static string BuildNeutralExtractiveSummary(
        string docName,
        int pageCount,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<ExtractedDocumentUnit> units)
    {
        var parts = new List<string> { $"{docName}, {pageCount} page(s)" };
        if (sectionTitles.Count > 0)
            parts.Add(string.Join("; ", sectionTitles.Take(6)));

        var excerpts = units
            .OrderBy(static unit => unit.Ordinal)
            .Select(static unit => CollapseWhitespace(unit.Text))
            .Where(static text => text.Length >= 40)
            .Take(3)
            .Select(static excerpt => TrimTo(excerpt, 180))
            .ToArray();
        if (excerpts.Length > 0)
            parts.Add(string.Join(" / ", excerpts));

        return TrimTo(CollapseWhitespace(string.Join(". ", parts) + "."), MaxSummaryChars);
    }

    private static string[] BuildHypotheticalQuestions(
        string docName,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string> entities,
        string language)
    {
        var subjects = keywords.Concat(entities)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        if (subjects.Length == 0)
            subjects = [Path.GetFileNameWithoutExtension(docName)];

        return subjects.Select(subject => language switch
            {
                "en" => $"What does {docName} say about {subject}?",
                "es" => $"Que dice {docName} sobre {subject}?",
                "pt" => $"O que diz {docName} sobre {subject}?",
                "de" => $"Was sagt {docName} ueber {subject}?",
                "it" => $"Che cosa dice {docName} su {subject}?",
                "fr" => $"Que dit {docName} sur {subject} ?",
                _ => $"{docName}: {subject}"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static string[] BuildLimits(string language)
        => language switch
        {
            "en" => ["Deterministic profile generated from extracted text.", "Use page chunks for exact facts, quantities, steps and citations."],
            "es" => ["Perfil determinista generado desde el texto extraido.", "Usar fragmentos de pagina para hechos, cantidades, pasos y citas exactas."],
            "pt" => ["Perfil deterministico gerado a partir do texto extraido.", "Usar excertos de paginas para factos, quantidades, passos e citacoes exatas."],
            "de" => ["Deterministisches Profil aus extrahiertem Text.", "Fuer exakte Fakten, Mengen, Schritte und Zitate Seiten-Chunks verwenden."],
            "it" => ["Profilo deterministico generato dal testo estratto.", "Usare i chunk di pagina per fatti, quantita, passaggi e citazioni esatte."],
            "fr" => ["Profil deterministe genere depuis le texte extrait.", "Utiliser les extraits de pages pour les faits, quantites, etapes et citations exacts."],
            _ => ["deterministic_profile_from_extracted_text", "use_page_chunks_for_exact_facts_quantities_steps_and_citations"]
        };

    private static bool HasLocalizedProfileTemplate(string language)
        => language is "fr" or "en" or "es" or "pt" or "de" or "it";

    private static IReadOnlyList<DocumentProfileContentCard> BuildContentCards(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        IReadOnlyList<string> keywords)
    {
        var candidates = new List<DocumentProfileContentCardCandidate>();
        var sectionTitleByOrdinal = sections
            .GroupBy(static section => section.Ordinal)
            .ToDictionary(static group => group.Key, static group => CollapseWhitespace(group.First().Title));

        foreach (var section in sections.OrderBy(static section => section.Ordinal).Take(80))
        {
            AddContentCardCandidate(
                candidates,
                section.Title,
                section.PageStart,
                section.PageEnd,
                "section",
                section.Title,
                keywords,
                score: 65);
        }

        foreach (var entry in exactMatchEntries
                     .Where(static entry => entry.Kind is "standard_ref" or "code_ref")
                     .OrderBy(static entry => entry.EntryIndex))
        {
            var title = CleanTitleCandidate(entry.Text);
            if (!LooksLikeTechnicalIdentifier(title))
                continue;

            AddContentCardCandidate(
                candidates,
                title,
                entry.PageStart,
                entry.PageEnd,
                entry.Kind,
                entry.Text,
                keywords,
                score: 88);
        }

        foreach (var unit in units.OrderBy(static unit => unit.Ordinal))
        {
            sectionTitleByOrdinal.TryGetValue(unit.SectionOrdinal ?? -1, out var sectionTitle);
            var acceptedTitles = 0;
            foreach (var title in ExtractLeadTitles(unit.Text))
            {
                if (AddContentCardCandidate(
                    candidates,
                    title,
                    unit.PageStart,
                    unit.PageEnd,
                    "unit_lead",
                    $"{sectionTitle} {unit.Text}",
                    keywords,
                    score: ComputeContentCardScore("unit_lead", title, unit.Text, 75)))
                {
                    acceptedTitles++;
                    if (acceptedTitles >= MaxUnitLeadContentCardCandidates)
                        break;
                }
            }
        }

        foreach (var page in pages.OrderBy(static page => page.PageNumber))
        {
            var acceptedTitles = 0;
            foreach (var title in ExtractLeadTitles(page.Text, PageEmbeddedTitleScanLength))
            {
                if (AddContentCardCandidate(
                    candidates,
                    title,
                    page.PageNumber,
                    page.PageNumber,
                    "page_embedded_title",
                    page.Text,
                    keywords,
                    score: ComputeContentCardScore("page_embedded_title", title, page.Text, 82)))
                {
                    acceptedTitles++;
                    if (acceptedTitles >= MaxPageContentCardCandidates)
                        break;
                }
            }
        }

        foreach (var entry in exactMatchEntries
                     .Where(static entry => entry.Kind == "verbatim_excerpt")
                     .OrderBy(static entry => entry.EntryIndex))
        {
            var acceptedTitles = 0;
            foreach (var title in ExtractLeadTitles(entry.Text))
            {
                if (AddContentCardCandidate(
                    candidates,
                    title,
                    entry.PageStart,
                    entry.PageEnd,
                    "exact_lead",
                    entry.Text,
                    keywords,
                    score: ComputeContentCardScore("exact_lead", title, entry.Text, 90)))
                {
                    acceptedTitles++;
                    if (acceptedTitles >= MaxExactLeadContentCardCandidates)
                        break;
                }
            }
        }

        return NormalizeContentCards(OrderContentCardsForBalancedCoverage(candidates));
    }

    private static IEnumerable<DocumentProfileContentCard> OrderContentCardsForBalancedCoverage(
        IReadOnlyList<DocumentProfileContentCardCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<DocumentProfileContentCardCandidate>();
        var groups = candidates
            .GroupBy(static candidate => candidate.Card.PageStart ?? int.MaxValue)
            .OrderBy(static group => group.Key)
            .Select(static group => group
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Card.PageStart ?? int.MaxValue)
                .ThenBy(static candidate => candidate.Card.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();

        foreach (var group in groups)
        {
            if (selected.Count >= MaxContentCards)
                break;

            foreach (var candidate in group)
            {
                if (AddBalancedCandidate(selected, seen, candidate))
                    break;
            }
        }

        foreach (var candidate in candidates
                     .OrderByDescending(static candidate => candidate.Score)
                     .ThenBy(static candidate => candidate.Card.PageStart ?? int.MaxValue)
                     .ThenBy(static candidate => candidate.Card.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Count >= MaxContentCards)
                break;

            AddBalancedCandidate(selected, seen, candidate);
        }

        return selected.Select(static candidate => candidate.Card);
    }

    private static bool AddBalancedCandidate(
        List<DocumentProfileContentCardCandidate> selected,
        HashSet<string> seen,
        DocumentProfileContentCardCandidate candidate)
    {
        var key = ExactMatchEntryExtractor.NormalizeForLookup(candidate.Card.Title);
        if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            return false;

        selected.Add(candidate);
        return true;
    }

    private static bool AddContentCardCandidate(
        List<DocumentProfileContentCardCandidate> candidates,
        string? title,
        int? pageStart,
        int? pageEnd,
        string kind,
        string? context,
        IReadOnlyList<string> documentKeywords,
        int score)
    {
        var cleanTitle = CleanTitleCandidate(title);
        if (!IsUsefulContentCardTitle(cleanTitle))
            return false;
        if (LooksLikeLowSignalContentCardLead(cleanTitle, kind))
            return false;

        var evidence = BuildStructuredCardEvidence($"{cleanTitle} {context}");
        var signals = BuildCardSignals(cleanTitle, context, documentKeywords, evidence);
        candidates.Add(new DocumentProfileContentCardCandidate(
            new DocumentProfileContentCard(
                cleanTitle,
                pageStart,
                pageEnd,
                string.IsNullOrWhiteSpace(kind) ? "content_item" : kind,
                signals,
                evidence,
                ContentCardId: null),
            score));
        return true;
    }

    private static IEnumerable<string> ExtractLeadTitles(string? text, int embeddedTitleScanLength = EmbeddedTitleScanLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Take(4))
        {
            var normalizedLine = CollapseWhitespace(line);
            if (string.IsNullOrWhiteSpace(normalizedLine))
                continue;

            foreach (var candidate in ExtractLeadTitleCandidatesFromLine(normalizedLine))
                yield return candidate;
        }

        var compactText = CollapseWhitespace(text);
        if (compactText.Length > 0)
        {
            foreach (var candidate in ExtractLeadTitleCandidatesFromLine(TrimTo(compactText, LeadTitleCompactHeadLength)))
                yield return candidate;

            if (compactText.Length > LeadTitleCompactHeadLength)
            {
                foreach (var candidate in ExtractWideEmbeddedTitleCandidates(TrimTo(compactText, embeddedTitleScanLength)))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExtractLeadTitleCandidatesFromLine(string line)
    {
        var spacedLine = LowerOrDigitToUpperTitleBoundaryRegex().Replace(line, " ");
        if (!string.Equals(spacedLine, line, StringComparison.Ordinal))
        {
            foreach (var candidate in ExtractEmbeddedUppercaseTitleCandidates(spacedLine))
                yield return candidate;
        }

        var cleaned = CleanTitleCandidate(line);
        if (IsUsefulContentCardTitle(cleaned))
            yield return cleaned;

        foreach (var candidate in ExtractCompactNumericSuffixTitleCandidates(line))
            yield return candidate;

        foreach (var candidate in ExtractEmbeddedUppercaseTitleCandidates(line))
            yield return candidate;

        var deglued = LowerToUpperBoundaryRegex().Replace(line, "${left}\n${right}");
        foreach (var segment in deglued.Split('\n').Take(3))
        {
            var candidate = CleanTitleCandidate(segment);
            if (IsUsefulContentCardTitle(candidate))
                yield return candidate;
        }

        var boundaryMatch = LeadBoundaryRegex().Match(line);
        if (boundaryMatch.Success)
        {
            var candidate = CleanTitleCandidate(boundaryMatch.Groups["title"].Value);
            if (IsUsefulContentCardTitle(candidate))
                yield return candidate;
        }

        foreach (var candidate in ExtractEmbeddedUppercaseTitleCandidates(line))
            yield return candidate;
    }

    private static IEnumerable<string> ExtractEmbeddedUppercaseTitleCandidates(string line)
    {
        foreach (Match match in UppercaseTitleRegex().Matches(line))
        {
            var candidate = CleanTitleCandidate(match.Groups["title"].Value);
            if (IsUsefulContentCardTitle(candidate))
                yield return candidate;
        }

        foreach (Match match in GluedUppercaseTitleRegex().Matches(line))
        {
            var candidate = CleanTitleCandidate(match.Groups["title"].Value);
            if (IsUsefulContentCardTitle(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> ExtractWideEmbeddedTitleCandidates(string text)
    {
        var spacedText = LowerOrDigitToUpperTitleBoundaryRegex().Replace(text, " ");

        foreach (var candidate in ExtractCompactNumericSuffixTitleCandidates(spacedText))
            yield return candidate;

        foreach (var candidate in ExtractEmbeddedUppercaseTitleCandidates(spacedText))
            yield return candidate;

        if (!string.Equals(spacedText, text, StringComparison.Ordinal))
        {
            foreach (var candidate in ExtractEmbeddedUppercaseTitleCandidates(text))
                yield return candidate;
        }
    }

    private static IEnumerable<string> ExtractCompactNumericSuffixTitleCandidates(string line)
    {
        foreach (Match match in CompactNumericSuffixTitleRegex().Matches(line))
        {
            var candidate = CleanTitleCandidate(match.Groups["title"].Value);
            if (IsUsefulContentCardTitle(candidate))
                yield return candidate;
        }
    }

    private static int ComputeContentCardScore(string kind, string title, string? context, int baseScore)
    {
        var score = baseScore;
        var normalizedTitle = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        var normalizedContext = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(context ?? string.Empty));

        if (LooksLikeStructuredContentContext(normalizedContext))
            score += 18;
        if (LooksLikeCompactStandaloneTitle(normalizedTitle))
            score += 12;
        if (string.Equals(kind, "exact_lead", StringComparison.Ordinal) && LooksLikeStructuredContentContext(normalizedContext))
            score += 10;

        return score;
    }

    private static bool LooksLikeStructuredContentContext(string normalizedContext)
    {
        if (string.IsNullOrWhiteSpace(normalizedContext))
            return false;

        return StructuredContentLexicon.ContainsStructuredContentContext(normalizedContext);
    }

    private static bool LooksLikeCompactStandaloneTitle(string normalizedTitle)
    {
        var tokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 7)
            return false;

        if (tokens.Any(static token => ContentCardTitleStopwords.Contains(token)))
            return false;

        return tokens.Count(static token => token.Length >= 4) >= Math.Min(2, tokens.Length);
    }

    private static bool LooksLikeLowSignalContentCardLead(string title, string kind)
    {
        if (string.Equals(kind, "section", StringComparison.Ordinal))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokenCount = CountTokens(title);
        if ((ImperativeInstructionLeadRegex().IsMatch(normalized) || AdditionalImperativeInstructionLeadRegex().IsMatch(normalized))
            && tokenCount >= 3)
            return true;

        if (InfinitiveInstructionLeadRegex().IsMatch(normalized)
            && tokenCount >= 5
            && !LooksLikeMostlyUppercaseTitle(title))
        {
            return true;
        }

        if (LooksLikeLowercaseLead(title))
            return true;

        if (LowSignalSentenceLeadRegex().IsMatch(normalized))
            return true;

        if (!LooksLikeMostlyUppercaseTitle(title) && ContainsNoisyInlinePunctuation(title, tokenCount))
            return true;

        if (ContainsActionSentencePunctuation(title)
            && (ImperativeInstructionLeadRegex().IsMatch(normalized) || InfinitiveInstructionLeadRegex().IsMatch(normalized)))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsActionSentencePunctuation(string title)
        => title.Contains('.', StringComparison.Ordinal)
            || title.Contains(';', StringComparison.Ordinal)
            || title.Contains('•', StringComparison.Ordinal)
            || title.Contains(":", StringComparison.Ordinal);

    private static bool ContainsNoisyInlinePunctuation(string title, int tokenCount)
    {
        if (title.Contains("...", StringComparison.Ordinal) || title.Contains('…', StringComparison.Ordinal))
            return true;

        if (title.Contains('•', StringComparison.Ordinal)
            || title.Contains('€', StringComparison.Ordinal)
            || title.Contains(';', StringComparison.Ordinal)
            || title.Contains('!', StringComparison.Ordinal)
            || title.Contains('?', StringComparison.Ordinal))
        {
            return true;
        }

        if (title.Contains('.', StringComparison.Ordinal))
            return true;

        return title.Contains(':', StringComparison.Ordinal) && (tokenCount >= 4 || title.Any(char.IsDigit));
    }

    private static bool LooksLikeLowercaseLead(string title)
    {
        foreach (var ch in title)
        {
            if (!char.IsLetter(ch))
                continue;

            return char.IsLower(ch);
        }

        return false;
    }

    private static bool LooksLikeMostlyUppercaseTitle(string title)
    {
        var letters = title.Where(char.IsLetter).ToArray();
        if (letters.Length < 4)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.72);
    }

    private static string CleanTitleCandidate(string? value)
    {
        var title = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = LeadingNumberRegex().Replace(title, string.Empty);
        title = BracketedIndexSuffixRegex().Replace(title, string.Empty);
        if (CountTokens(title) >= 2 && LooksLikeMostlyUppercaseTitle(title))
            title = CompactTrailingMeasureNumberRegex().Replace(title, string.Empty);
        if (CountTokens(title) >= 2)
            title = CompactTrailingNumericSuffixRegex().Replace(title, string.Empty);
        title = CollapseWhitespace(title.Trim(' ', '-', ':', ';', '.', ',', '|', '/', '\\', '(', ')', '•', '·'));
        return title.Length <= 140 ? title : TrimTo(title, 140);
    }

    private static bool IsUsefulContentCardTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (title.Length is < 4 or > 120)
            return false;

        var tokenCount = CountTokens(title);
        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        var normalizedFolded = FoldDiacritics(normalized);
        var hasTechnicalIdentifier = LooksLikeTechnicalIdentifier(title);

        if ((tokenCount < 2 && !hasTechnicalIdentifier) || tokenCount > 14)
            return false;

        if (!title.Any(char.IsLetter))
            return false;

        if (title.Count(char.IsDigit) > Math.Max(4, title.Length / 3) && !hasTechnicalIdentifier)
            return false;

        if (title.Count(static ch => ch is ',' or ';' or ':' or '|' or '/') > 4 && !hasTechnicalIdentifier)
            return false;

        if (LooksLikeGluedNavigationOrHeaderTitle(title) && !hasTechnicalIdentifier)
            return false;
        if (!hasTechnicalIdentifier
            && RetrievalContentClassifier.DetectNavigationReason(title) is not null)
        {
            return false;
        }

        if (ContentCardTitleStopwords.Contains(normalizedFolded))
            return false;
        if (LooksLikeGenericContentCardTitle(normalizedFolded) && !hasTechnicalIdentifier)
            return false;
        if (LooksLikeOcrNoiseTitle(title, normalizedFolded, tokenCount) && !hasTechnicalIdentifier)
            return false;
        if (LooksLikeDanglingFragmentContentCardTitle(title, normalizedFolded, tokenCount) && !hasTechnicalIdentifier)
            return false;
        if (PageReferenceFragmentRegex().IsMatch(normalizedFolded)
            && !hasTechnicalIdentifier
            && (tokenCount >= 4 || normalizedFolded.Contains("table des matieres", StringComparison.Ordinal)))
        {
            return false;
        }
        if (LooksLikeSentenceOrInstructionTitle(normalizedFolded, tokenCount) && !hasTechnicalIdentifier)
            return false;
        if (ParameterFragmentTitleRegex().IsMatch(normalizedFolded) && !hasTechnicalIdentifier)
            return false;

        if (tokenCount <= 2 && title.Any(char.IsDigit) && !hasTechnicalIdentifier)
            return false;

        var firstToken = normalizedFolded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstToken is not null
            && ContentCardLeadStopwords.Contains(firstToken)
            && tokenCount <= 3
            && !hasTechnicalIdentifier)
            return false;

        return true;
    }

    private static bool LooksLikeTechnicalIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value)
            && TechnicalIdentifierRegex().IsMatch(value);

    private static bool LooksLikeGluedNavigationOrHeaderTitle(string title)
    {
        if (LowerToUpperBoundaryRegex().IsMatch(title))
            return true;

        if (Regex.IsMatch(title, @"[\p{Lu}]{2,}[\p{Lu}][\p{Ll}]+", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(title, @"[\p{Ll}][0-9]", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(title, @"[\p{L}][0-9]{1,3}$", RegexOptions.CultureInvariant)
            && CountTokens(title) >= 2)
        {
            return true;
        }

        var tokens = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(static token => token.Length >= 28 && token.Count(char.IsLetter) >= 24))
            return true;

        return false;
    }

    private static bool LooksLikeSentenceOrInstructionTitle(string normalizedFolded, int tokenCount)
    {
        if (InstructionLeadTitleRegex().IsMatch(normalizedFolded))
            return true;

        if (AdditionalImperativeInstructionLeadRegex().IsMatch(normalizedFolded))
            return true;

        if (SentenceLeadTitleRegex().IsMatch(normalizedFolded))
            return true;

        if (tokenCount >= 4 && SentenceVerbTitleRegex().IsMatch(normalizedFolded))
            return true;

        return normalizedFolded.Contains("materialsprocedure", StringComparison.Ordinal)
            || normalizedFolded.Contains("materials procedure", StringComparison.Ordinal)
            || normalizedFolded.Contains("componentsprocedure", StringComparison.Ordinal)
            || normalizedFolded.Contains("components procedure", StringComparison.Ordinal)
            || LooksLikeAllCapsMarketingHeadline(normalizedFolded, tokenCount);
    }

    private static bool LooksLikeOcrNoiseTitle(string title, string normalizedFolded, int tokenCount)
    {
        if (tokenCount < 2)
            return false;

        var tokens = normalizedFolded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var noiseTokens = tokens.Count(static token =>
            token.Length == 1
            || token.All(char.IsDigit)
            || IsShortRomanNumeral(token));
        if (noiseTokens >= Math.Max(2, (int)Math.Ceiling(tokens.Length * 0.60)))
            return true;

        if (tokens.Length is >= 3 and <= 4
            && tokens.All(static token => token.Length <= 3 && !token.Any(char.IsDigit))
            && tokens.Average(static token => token.Length) < 2.8)
        {
            return true;
        }

        var repeatedRunTokens = tokens.Count(static token =>
            token.Length >= 8
            && (RepeatedOcrCharacterRunRegex().IsMatch(token) || RepeatedOcrSyllableRunRegex().IsMatch(token)));
        if (repeatedRunTokens > 0 && (repeatedRunTokens >= 2 || tokenCount <= 4))
            return true;

        if (noiseTokens >= Math.Max(2, (int)Math.Ceiling(tokens.Length * 0.45)))
            return true;

        var invertedPunctuation = title.Count(static ch => ch is '¡' or '¿');
        return invertedPunctuation > 0 && noiseTokens >= 2;
    }

    private static bool LooksLikeDanglingFragmentContentCardTitle(string title, string normalizedFolded, int tokenCount)
    {
        if (tokenCount < 2)
            return false;

        var tokens = normalizedFolded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var strongTokens = tokens.Count(static token =>
            token.Length >= 4
            && !DanglingFragmentTitleTokens.Contains(token)
            && !ContentCardLeadStopwords.Contains(token));
        var firstToken = tokens[0];
        var lastToken = tokens[^1];
        var startsWithFragment = DanglingFragmentTitleTokens.Contains(firstToken);
        var endsWithFragment = DanglingFragmentTitleTokens.Contains(lastToken);

        if (startsWithFragment && tokenCount <= 6 && strongTokens <= 2)
            return true;

        if (endsWithFragment
            && tokenCount >= 3
            && tokenCount <= 8
            && (startsWithFragment
                || LooksLikeLowercaseLead(title)
                || strongTokens <= 1
                || ContainsNoisyInlinePunctuation(title, tokenCount)))
        {
            return true;
        }

        return HasUnbalancedContentCardQuote(title)
            && (LooksLikeLowercaseLead(title) || strongTokens <= 2 || tokenCount <= 6);
    }

    private static bool HasUnbalancedContentCardQuote(string title)
    {
        var leftFrenchQuoteCount = title.Count(static ch => ch == '\u00ab');
        var rightFrenchQuoteCount = title.Count(static ch => ch == '\u00bb');
        if (leftFrenchQuoteCount != rightFrenchQuoteCount)
            return true;

        var doubleQuoteCount = title.Count(static ch => ch is '"');
        return doubleQuoteCount % 2 != 0;
    }

    private static bool IsShortRomanNumeral(string token)
        => token.Length <= 5
            && token.All(static ch => ch is 'i' or 'v' or 'x')
            && token.Any(static ch => ch is 'i' or 'v' or 'x');

    private static bool LooksLikeAllCapsMarketingHeadline(string normalizedFolded, int tokenCount)
        => tokenCount >= 4
            && Regex.IsMatch(normalizedFolded, @"\b[a-z]{2,}\s+a\s+[a-z]{2,}\b", RegexOptions.CultureInvariant);

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

    private static bool LooksLikeGenericContentCardTitle(string normalizedTitle)
        => GenericContentCardTitlePrefixRegex().IsMatch(normalizedTitle)
            || InstructionLeadTitleRegex().IsMatch(normalizedTitle)
            || CountLeadTitleRegex().IsMatch(normalizedTitle)
            || normalizedTitle.Contains("table des matieres", StringComparison.Ordinal)
            || normalizedTitle.Contains("table of contents", StringComparison.Ordinal)
            || normalizedTitle.Contains(" indd ", StringComparison.Ordinal)
            || normalizedTitle.StartsWith("couv ", StringComparison.Ordinal)
            || normalizedTitle.StartsWith("cover ", StringComparison.Ordinal);

    private static string[] BuildCardSignals(
        string title,
        string? context,
        IReadOnlyList<string> documentKeywords,
        DocumentProfileCardEvidence? evidence)
    {
        var localKeywords = ExtractKeywords($"{title} {context}", maxKeywords: 8);
        var structuredSignals = BuildStructuredCardSignals(evidence);
        return NormalizeList(
            new[] { title }
                .Concat(structuredSignals)
                .Concat(localKeywords)
                .Concat(documentKeywords.Take(6)),
            10);
    }

    private static DocumentProfileCardEvidence? BuildStructuredCardEvidence(string? context)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(context ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var hasScaleBasis = TryExtractStructuredScaleBasis(normalized, out var scaleBasisCount, out var scaleBasisLabel);
        var quantityFacts = ExtractStructuredQuantityFacts(context).ToArray();
        var nonScalableReasons = LooksLikeNonScalableStructuredQuantityContext(normalized)
            ? new[] { "safety_or_parameter_context" }
            : [];

        if (!hasScaleBasis && quantityFacts.Length < 2 && nonScalableReasons.Length == 0)
            return null;

        var genericFacts = new List<DocumentProfileEvidenceFact>();
        if (hasScaleBasis)
        {
            genericFacts.Add(new DocumentProfileEvidenceFact(
                Kind: "scale_basis",
                Label: NormalizeOptionalStructuredSignalLabel(scaleBasisLabel) ?? "scale_basis",
                Value: scaleBasisCount.ToString(CultureInfo.InvariantCulture),
                Unit: null,
                SourceText: null,
                PageStart: null,
                PageEnd: null,
                Confidence: 0.82));
        }

        genericFacts.AddRange(quantityFacts.Select(static fact => new DocumentProfileEvidenceFact(
            Kind: "quantity",
            Label: fact.Label,
            Value: fact.Value.ToString(CultureInfo.InvariantCulture),
            Unit: fact.Unit,
            SourceText: fact.SourceText,
            PageStart: null,
            PageEnd: null,
            Confidence: 0.82)));

        return new DocumentProfileCardEvidence(
            SchemaVersion: "content_card_evidence_v1",
            ScaleBasis: hasScaleBasis ? new DocumentProfileScaleBasis(scaleBasisCount, NormalizeOptionalStructuredSignalLabel(scaleBasisLabel)) : null,
            QuantityFacts: quantityFacts,
            NonScalableReasons: nonScalableReasons,
            Confidence: hasScaleBasis && quantityFacts.Length >= 2 && nonScalableReasons.Length == 0 ? 0.82 : 0.55,
            Language: null,
            Facts: genericFacts);
    }

    private static IReadOnlyList<string> BuildStructuredCardSignals(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return [];

        var signals = new List<string>();
        if (evidence.ScaleBasis is { Count: > 0 } basis)
        {
            signals.Add("scale_basis");
            signals.Add($"scale_basis_count:{basis.Count}");
            if (!string.IsNullOrWhiteSpace(basis.Label))
                signals.Add($"scale_basis_label:{NormalizeStructuredSignalLabel(basis.Label)}");
        }

        if (evidence.QuantityFacts.Count >= 2)
            signals.Add("quantity_list");

        if ((evidence.Facts ?? []).Count > 0)
            signals.Add("structured_facts");

        if (evidence.NonScalableReasons.Count > 0)
            signals.Add("non_scalable_quantities");

        if (evidence.ScaleBasis is { Count: > 0 }
            && evidence.QuantityFacts.Count >= 2
            && evidence.NonScalableReasons.Count == 0)
        {
            signals.Add("scalable_quantities");
        }

        return signals;
    }

    private static bool TryExtractStructuredScaleBasis(string normalizedContext, out int count, out string? label)
        => StructuredContentLexicon.TryExtractScaleBasis(normalizedContext, out count, out label);

    private static IEnumerable<DocumentProfileQuantityFact> ExtractStructuredQuantityFacts(string? context)
    {
        var text = CollapseWhitespace((context ?? string.Empty)
            .Replace('\u2022', '|')
            .Replace('\u00b7', '|'));
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in Regex.Matches(
                     text,
                     @"\b(?<value>\d+(?:[,.]\d+)?)\s*(?<unit>%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12})\s+(?<label>[^|;\.\n\r]{2,90})",
                     RegexOptions.CultureInvariant))
        {
            if (!double.TryParse(match.Groups["value"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                continue;
            }

            var unit = NormalizeStructuredSignalLabel(match.Groups["unit"].Value);
            if (string.IsNullOrWhiteSpace(unit) || LooksLikeNonScalableStructuredQuantityUnit(unit))
                continue;

            var label = CleanQuantityFactLabel(match.Groups["label"].Value);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            yield return new DocumentProfileQuantityFact(
                Value: value,
                Unit: unit,
                Label: label,
                SourceText: CollapseWhitespace(match.Value));
        }
    }

    private static string CleanQuantityFactLabel(string? value)
    {
        var label = CollapseWhitespace(value)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        label = Regex.Replace(label, @"^(?:de|d['\u2019]|du|des|of|for|pour|para|per)\s+", string.Empty, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        label = Regex.Replace(
            label,
            @"\b(?:procedure|procedures?|process|execution|operation|operations|instructions?|method|methods?|methode|methodes|mode\s+operatoire|etapes?|steps?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Trim();
        return TrimTo(label, 80);
    }

    private static bool LooksLikeNonScalableStructuredQuantityContext(string normalizedContext)
        => StructuredContentLexicon.ContainsNonScalableQuantityContext(normalizedContext);

    private static DocumentProfileCardEvidence? NormalizeContentCardEvidence(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return null;

        var rawQuantityFacts = evidence.QuantityFacts ?? [];
        var rawNonScalableReasons = evidence.NonScalableReasons ?? [];
        var rawFacts = evidence.Facts ?? [];
        var quantityFacts = rawQuantityFacts
            .Where(static fact => fact.Value > 0)
            .Select(static fact => fact with
            {
                Unit = NormalizeStructuredSignalLabel(fact.Unit),
                Label = TrimTo(CollapseWhitespace(fact.Label), 80),
                SourceText = TrimTo(CollapseWhitespace(fact.SourceText), 120)
            })
            .Where(static fact => !string.IsNullOrWhiteSpace(fact.Unit) && !string.IsNullOrWhiteSpace(fact.Label))
            .Take(24)
            .ToArray();

        var scaleBasis = evidence.ScaleBasis is { Count: > 0 and <= 200 } basis
            ? new DocumentProfileScaleBasis(basis.Count, NormalizeOptionalStructuredSignalLabel(basis.Label))
            : null;
        var nonScalableReasons = NormalizeList(rawNonScalableReasons, 8);
        var facts = rawFacts
            .Select(NormalizeContentCardEvidenceFact)
            .Where(static fact => fact is not null)
            .Select(static fact => fact!)
            .DistinctBy(static fact => $"{fact.Kind}\u001f{fact.Label}\u001f{fact.Value}\u001f{fact.Unit}", StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        if (scaleBasis is null && quantityFacts.Length == 0 && nonScalableReasons.Length == 0 && facts.Length == 0)
            return null;

        return new DocumentProfileCardEvidence(
            string.IsNullOrWhiteSpace(evidence.SchemaVersion) ? "content_card_evidence_v1" : CollapseWhitespace(evidence.SchemaVersion),
            scaleBasis,
            quantityFacts,
            nonScalableReasons,
            evidence.Confidence is >= 0 and <= 1 ? evidence.Confidence : null,
            NormalizeOptionalLanguageTag(evidence.Language),
            facts);
    }

    private static DocumentProfileEvidenceFact? NormalizeContentCardEvidenceFact(DocumentProfileEvidenceFact? fact)
    {
        if (fact is null)
            return null;

        var kind = NormalizeStructuredSignalLabel(fact.Kind);
        var label = TrimTo(CollapseWhitespace(fact.Label), 100);
        var value = TrimTo(CollapseWhitespace(fact.Value ?? string.Empty), 120);
        var unit = NormalizeOptionalStructuredSignalLabel(fact.Unit);
        var sourceText = TrimTo(CollapseWhitespace(fact.SourceText ?? string.Empty), 180);
        var pageStart = fact.PageStart is > 0 and <= 100000 ? fact.PageStart : null;
        var pageEnd = fact.PageEnd is > 0 and <= 100000 ? fact.PageEnd : null;
        if (pageStart is > 0 && pageEnd is > 0 && pageEnd < pageStart)
            pageEnd = pageStart;

        if (string.IsNullOrWhiteSpace(kind))
            kind = "fact";
        if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(sourceText))
            return null;

        return new DocumentProfileEvidenceFact(
            Kind: kind,
            Label: string.IsNullOrWhiteSpace(label) ? "fact" : label,
            Value: string.IsNullOrWhiteSpace(value) ? null : value,
            Unit: unit,
            SourceText: string.IsNullOrWhiteSpace(sourceText) ? null : sourceText,
            PageStart: pageStart,
            PageEnd: pageEnd,
            Confidence: fact.Confidence is >= 0 and <= 1 ? fact.Confidence : null);
    }

    private static string? NormalizeOptionalLanguageTag(string? value)
    {
        var normalized = DocumentLanguageResolver.NormalizeLanguageTag(value);
        return string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "und", StringComparison.Ordinal)
            ? null
            : normalized;
    }

    internal static DocumentProfileCardEvidence? ParseContentCardEvidenceFromMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var json = JsonDocument.Parse(metadataJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (json.RootElement.TryGetProperty("evidence", out var nested))
                return ParseContentCardEvidence(nested);

            return ParseContentCardEvidence(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DocumentProfileCardEvidence? ParseContentCardEvidence(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var normalized = NormalizeContentCardEvidence(value.Deserialize<DocumentProfileCardEvidence>(JsonOptions));
            return normalized ?? ParseLooseContentCardEvidence(value);
        }
        catch (JsonException)
        {
            return ParseLooseContentCardEvidence(value);
        }
    }

    private static DocumentProfileCardEvidence? ParseLooseContentCardEvidence(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var schemaVersion = ReadLooseString(value, "schemaVersion") ?? "content_card_evidence_v1";
        var language = ReadLooseString(value, "language");
        var confidence = ReadLooseDouble(value, "confidence");
        var scaleBasis = ParseLooseScaleBasis(value);
        var quantityFacts = ParseLooseQuantityFacts(value).ToArray();
        var nonScalableReasons = ReadLooseStringArray(value, "nonScalableReasons");
        var facts = ParseLooseEvidenceFacts(value).ToArray();

        return NormalizeContentCardEvidence(new DocumentProfileCardEvidence(
            SchemaVersion: schemaVersion,
            ScaleBasis: scaleBasis,
            QuantityFacts: quantityFacts,
            NonScalableReasons: nonScalableReasons,
            Confidence: confidence,
            Language: language,
            Facts: facts));
    }

    private static DocumentProfileScaleBasis? ParseLooseScaleBasis(JsonElement root)
    {
        if (!root.TryGetProperty("scaleBasis", out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        var count = ReadLooseInt(value, "count") ?? ReadLooseInt(value, "value");
        if (count is null or <= 0 or > 200)
            return null;

        return new DocumentProfileScaleBasis(
            count.Value,
            ReadLooseString(value, "label") ?? ReadLooseString(value, "unit") ?? ReadLooseString(value, "basis"));
    }

    private static IEnumerable<DocumentProfileQuantityFact> ParseLooseQuantityFacts(JsonElement root)
    {
        if (!root.TryGetProperty("quantityFacts", out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var parsedValue = ReadLooseDouble(item, "value") ?? ReadLooseDouble(item, "amount");
            var unit = ReadLooseString(item, "unit");
            var label = ReadLooseString(item, "label") ?? ReadLooseString(item, "name");
            if (parsedValue is not > 0 || string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(label))
                continue;

            yield return new DocumentProfileQuantityFact(
                parsedValue.Value,
                unit,
                label,
                ReadLooseString(item, "sourceText") ?? ReadLooseString(item, "text") ?? string.Empty);
        }
    }

    private static IEnumerable<DocumentProfileEvidenceFact> ParseLooseEvidenceFacts(JsonElement root)
    {
        if (!root.TryGetProperty("facts", out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            yield return new DocumentProfileEvidenceFact(
                Kind: ReadLooseString(item, "kind") ?? ReadLooseString(item, "type") ?? "fact",
                Label: ReadLooseString(item, "label") ?? ReadLooseString(item, "name") ?? "fact",
                Value: ReadLooseString(item, "value") ?? ReadLooseString(item, "amount"),
                Unit: ReadLooseString(item, "unit"),
                SourceText: ReadLooseString(item, "sourceText") ?? ReadLooseString(item, "text") ?? ReadLooseString(item, "quote"),
                PageStart: ReadLooseInt(item, "pageStart") ?? ReadLooseInt(item, "page"),
                PageEnd: ReadLooseInt(item, "pageEnd") ?? ReadLooseInt(item, "page"),
                Confidence: ReadLooseDouble(item, "confidence"));
        }
    }

    private static string[] ReadLooseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }

    private static string? ReadLooseString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? ReadLooseInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadLooseDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string BuildContentCardEvidenceLookupText(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();
        if (evidence.ScaleBasis is { Count: > 0 } basis)
        {
            parts.Add("scale_basis");
            parts.Add($"scale_basis_count:{basis.Count}");
            if (!string.IsNullOrWhiteSpace(basis.Label))
                parts.Add($"scale_basis_label:{basis.Label}");
        }

        if (evidence.QuantityFacts.Count >= 2)
            parts.Add("quantity_list");

        if (!string.IsNullOrWhiteSpace(evidence.Language))
            parts.Add($"language:{evidence.Language}");
        if ((evidence.Facts ?? []).Count > 0)
            parts.Add("structured_facts");

        parts.AddRange(evidence.QuantityFacts.Select(static fact => $"{fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Unit} {fact.Label}"));
        parts.AddRange(evidence.NonScalableReasons);
        parts.AddRange((evidence.Facts ?? []).Select(static fact => string.Join(' ', new[]
        {
            fact.Kind,
            fact.Label,
            fact.Value,
            fact.Unit,
            fact.SourceText
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
        return CollapseWhitespace(string.Join(' ', parts));
    }

    private static string? NormalizeOptionalStructuredSignalLabel(string? value)
    {
        var normalized = NormalizeStructuredSignalLabel(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool LooksLikeNonScalableStructuredQuantityUnit(string unit)
        => StructuredContentLexicon.IsNonScalableQuantityUnit(unit);

    private static string NormalizeStructuredSignalLabel(string? value)
        => StructuredContentLexicon.NormalizeStructuredSignalLabel(value);

    private static IReadOnlyList<DocumentProfileContentCard> NormalizeContentCards(
        IEnumerable<DocumentProfileContentCard> cards)
    {
        var normalized = new List<DocumentProfileContentCard>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in cards)
        {
            var title = CleanTitleCandidate(card.Title);
            if (!IsUsefulContentCardTitle(title))
                continue;

            var key = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            var normalizedEvidence = NormalizeContentCardEvidence(card.Evidence);
            var (pageStart, pageEnd) = NormalizeContentCardPageRange(card.PageStart, card.PageEnd, normalizedEvidence);
            var normalizedSignals = NormalizeList(
                BuildStructuredCardSignals(normalizedEvidence)
                    .Concat(card.Signals ?? []),
                10);

            normalized.Add(new DocumentProfileContentCard(
                title,
                pageStart,
                pageEnd,
                string.IsNullOrWhiteSpace(card.Kind) ? "content_item" : CollapseWhitespace(card.Kind),
                normalizedSignals,
                normalizedEvidence,
                card.ContentCardId));

            if (normalized.Count >= MaxContentCards)
                break;
        }

        return normalized;
    }

    private static (int? PageStart, int? PageEnd) NormalizeContentCardPageRange(
        int? rawPageStart,
        int? rawPageEnd,
        DocumentProfileCardEvidence? evidence)
    {
        var pageStart = rawPageStart is > 0 and <= 100000 ? rawPageStart : null;
        var pageEnd = rawPageEnd is > 0 and <= 100000 ? rawPageEnd : null;
        if (pageStart is > 0 && pageEnd is > 0 && pageEnd < pageStart)
            pageEnd = pageStart;

        if (pageStart is null)
        {
            var derived = DeriveContentCardPageRangeFromEvidence(evidence);
            pageStart = derived.PageStart;
            pageEnd = derived.PageEnd;
        }

        if (pageStart is > 0 && pageEnd is null)
            pageEnd = pageStart;

        return (pageStart, pageEnd);
    }

    private static (int? PageStart, int? PageEnd) DeriveContentCardPageRangeFromEvidence(
        DocumentProfileCardEvidence? evidence)
    {
        if (evidence?.Facts is not { Count: > 0 } facts)
            return (null, null);

        int? pageStart = null;
        int? pageEnd = null;
        foreach (var fact in facts)
        {
            if (fact.PageStart is not > 0)
                continue;

            var factStart = fact.PageStart.Value;
            var factEnd = fact.PageEnd is > 0 ? fact.PageEnd.Value : factStart;
            if (factEnd < factStart)
                factEnd = factStart;

            pageStart = pageStart is null ? factStart : Math.Min(pageStart.Value, factStart);
            pageEnd = pageEnd is null ? factEnd : Math.Max(pageEnd.Value, factEnd);
        }

        if (pageStart is null || pageEnd is null)
            return (null, null);

        if (pageEnd.Value - pageStart.Value + 1 > MaxEvidenceDerivedContentCardPageSpan)
            return (null, null);

        return (pageStart, pageEnd);
    }

    internal static IReadOnlyList<DocumentProfileContentCard> ParseContentCards(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return [];

        try
        {
            using var json = JsonDocument.Parse(metadataJson);
            if (!json.RootElement.TryGetProperty("contentCards", out var contentCards)
                || contentCards.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<DocumentProfileContentCard>();
            foreach (var item in contentCards.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var title = ReadJsonString(item, "title");
                var kind = ReadJsonString(item, "kind") ?? "content_item";
                var pageStart = ReadJsonInt(item, "pageStart");
                var pageEnd = ReadJsonInt(item, "pageEnd");
                var signals = ReadJsonStringArray(item, "signals");
                var contentCardId = ReadJsonString(item, "contentCardId") ?? ReadJsonString(item, "content_card_id");
                var evidence = item.TryGetProperty("evidence", out var evidenceElement)
                    ? ParseContentCardEvidence(evidenceElement)
                    : null;
                parsed.Add(new DocumentProfileContentCard(title ?? string.Empty, pageStart, pageEnd, kind, signals, evidence, contentCardId));
            }

            return NormalizeContentCards(parsed);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadJsonString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadJsonInt(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string[] ReadJsonStringArray(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(static child => child.ValueKind == JsonValueKind.String)
            .Select(static child => child.GetString())
            .Where(static child => !string.IsNullOrWhiteSpace(child))
            .Select(static child => child!)
            .ToArray();
    }

    private static string BuildSearchText(
        string docPath,
        string docName,
        string summary,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string> entities,
        IReadOnlyList<string> topics,
        IReadOnlyList<string> questions,
        IReadOnlyList<string> limits,
        IReadOnlyList<DocumentProfileContentCard> contentCards)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            docPath,
            docName,
            summary,
            string.Join(' ', keywords),
            string.Join(' ', entities),
            string.Join(' ', topics),
            string.Join(' ', questions),
            string.Join(' ', limits),
            string.Join(' ', contentCards.Select(static card => $"{card.Title} {BuildContentCardLookupText(card)} {string.Join(' ', card.Signals)} {BuildContentCardEvidenceLookupText(card.Evidence)}"))
        }));

    private static string BuildContentCardLookupText(DocumentProfileContentCard card)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(card.Title)),
            FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(string.Join(' ', card.Signals))),
            FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(BuildContentCardEvidenceLookupText(card.Evidence)))
        }));

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string TrimTo(string value, int maxChars)
    {
        value = CollapseWhitespace(value);
        if (value.Length <= maxChars)
            return value;

        var cut = value.LastIndexOf(' ', maxChars - 1);
        if (cut < maxChars / 2)
            cut = maxChars;
        return value[..cut].TrimEnd();
    }

    private static string CollapseWhitespace(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string[] NormalizeList(IEnumerable<string> values, int maxItems)
        => values
            .Select(CollapseWhitespace)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToArray();

    private static readonly HashSet<string> ProfileStopwords = new(StringComparer.Ordinal)
    {
        "avec", "dans", "pour", "sans", "cette", "cela", "sont", "vous", "nous", "leur", "leurs",
        "document", "documents", "page", "pages", "section", "sections", "chapitre", "chapter",
        "the", "and", "that", "this", "with", "from", "into", "have", "has", "were", "been",
        "para", "como", "esta", "este", "estos", "estas", "sobre", "entre", "mais", "mais",
        "oder", "und", "der", "die", "das", "eine", "einer", "fuer", "ueber", "mit",
        "per", "con", "gli", "che", "dei", "delle", "una", "uno", "sono"
    };

    private static readonly HashSet<string> ContentCardTitleStopwords = new(StringComparer.Ordinal)
    {
        "materials", "material", "components", "component", "method", "methods",
        "etapes", "etape", "steps", "step", "notes", "note", "source", "sources",
        "sommaire", "contents", "table of contents", "index",
        "total time", "duree totale", "durée totale",
        "document", "documents", "page", "pages"
    };

    private static readonly HashSet<string> ContentCardLeadStopwords = new(StringComparer.Ordinal)
    {
        "this", "that", "these", "those", "cette", "cela", "voici", "pour", "avec",
        "dans", "vous", "nous", "the", "and", "from", "para", "como", "esta",
        "este", "oder", "und", "der", "die", "das", "per", "con"
    };

    private static readonly HashSet<string> DanglingFragmentTitleTokens = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "as", "at", "by", "d", "da", "dans", "das", "de", "del",
        "della", "des", "di", "die", "du", "el", "en", "et", "for", "from", "in",
        "l", "la", "las", "le", "les", "lo", "los", "mit", "of", "on", "or", "ou",
        "para", "per", "por", "sur", "the", "to", "und", "with", "y", "zu"
    };

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-/]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(?:[A-Z]{2,}(?:[-\s]?[A-Z0-9]{2,})+|[A-Z]{1,6}\s?\d{2,}(?:[-/]\d{1,})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLikeRegex();

    [GeneratedRegex(@"\b(?:EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR|TS|PD|BS|NF|SN|UL|CSA)(?:[\s._/\-]+[A-Z]{1,6}){0,4}[\s._/\-]*\d[A-Z0-9._/\-:]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalIdentifierRegex();

    [GeneratedRegex(@"(?<left>[\p{Ll}\p{Lo}])(?<right>[\p{Lu}][\p{Ll}]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToUpperBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=[\p{Lu}]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LowerOrDigitToUpperTitleBoundaryRegex();

    [GeneratedRegex(@"^\s*(?:page\s*)?(?:\d+[\.)\]\-:]*\s*|[IVXLCDM]+(?:[\.)\]\-:]\s*|\s+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"\s*\[(?:index|contents?|sommaire).*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BracketedIndexSuffixRegex();

    [GeneratedRegex(@"^(?:materials?|components?|procedures?|method|methods|steps?|etapes?|sources?|references?|notes?|materiel|matériel|technique|suggestions?|requirements?|warnings?|cautions?|instructions?|parameters?|settings?|total time|duree totale|durée totale)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericContentCardTitlePrefixRegex();

    [GeneratedRegex(@"^(?:add|ajouter|ajoutez?|appliquer|apply|arreter|attendre|check|choisir|close|configurer|configure|connect|connecter|copy|copier|deconnecter|delete|demarrer|ensuite|enter|fermer|install|installer|lancer|mettre|open|ouvrir|placer|place|programmer|programmez|puis|quand|remove|remplacer|replace|restart|retirer|run|save|select|selectionner|set|start|stop|supprimer|update|use|utilisez?|utiliser|validate|valider|verify|verifier|v[ée]rifier)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InstructionLeadTitleRegex();

    [GeneratedRegex(@"^(?:avec des|ce|cela|celle|celui|cette|elle|elles|est|facultatif\)?|fonctionne|il|ils|it|pour cette|pour le|pour la|pour les|se|si vous|this|vous)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentenceLeadTitleRegex();

    [GeneratedRegex(@"\b(?:est|sont|doit|doivent|peut|peuvent|pouvez|pourrez|permet|permettent|recommande|recommandons|utilisez|utiliser|trouver|trouvez|ajoutez|ouvrez|fermez|retirez|verifiez|v[ée]rifiez|is|are|can|must|should|allows?|use|uses|using|open|close|remove|verify|check)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentenceVerbTitleRegex();

    [GeneratedRegex(@"^(?:ajoutez?|appliquez|arretez|choisissez|configurez|connectez|copiez|demarrez|deconnectez|enlevez|fermez|installez|lancez?|ouvrez|placez|placez-les|posez|programmez|redemarrez|remettez|remplacez?|retirez|saisissez?|selectionnez|supprimez|utilisez?|validez|verifiez|v[ée]rifiez)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ImperativeInstructionLeadRegex();

    [GeneratedRegex(@"^(?:coupez?|couvrez?|deposez|enfournez|faites|formez|melangez|mixez?|servez|trempez|versez)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AdditionalImperativeInstructionLeadRegex();

    [GeneratedRegex(@"^(?:ajouter|appliquer|arreter|choisir|configurer|connecter|copier|demarrer|deconnecter|enlever|fermer|installer|lancer|ouvrir|placer|programmer|redemarrer|remettre|remplacer|retirer|selectionner|supprimer|utiliser|valider|verifier|v[ée]rifier)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InfinitiveInstructionLeadRegex();

    [GeneratedRegex(@"^(?:a l aide|a la fin|apres|bien|c est|ca|ceci|cela|dans tous les cas|garder|gardez|l idee|mais la aussi|n hesitez|on|onne|pour connaitre|pour l|pour vous|pourtant|quellesatisfaction|si vous|suivant le|voici)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowSignalSentenceLeadRegex();

    [GeneratedRegex(@"^(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountLeadTitleRegex();

    [GeneratedRegex(@"^(?:vitesse|speed|temperature|température|temp|mode|programme|program|rpm|tr/min|minutes?|mins?|seconds?|secondes?|heures?|hours?)\b.*\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ParameterFragmentTitleRegex();

    [GeneratedRegex(@"\b(?:page|pages?|p\.?)\s*\d+\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PageReferenceFragmentRegex();

    [GeneratedRegex(@"^\s*(?<title>.{4,90}?)(?:\s{2,}|[\.:\-\u2013\u2014]\s+|(?=\b(?:for|pour|para|per|mit|avec|with|materials?|components?|procedure|procedures|method|steps?|requirements?|instructions?)\b))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadBoundaryRegex();

    [GeneratedRegex(@"(?<title>\b[\p{Lu}][\p{Lu}\p{Nd}'\u2019\-\s]{4,90})(?=\d|\s{2,}|[\.:\-\u2013\u2014]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseTitleRegex();

    [GeneratedRegex(@"(?<![\p{Lu}\p{Nd}])(?<title>[\p{Lu}][\p{Lu}\p{Nd}'\u2019\-\s]{4,90}?)(?=\p{Lu}(?:['\u2019])?\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex GluedUppercaseTitleRegex();

    [GeneratedRegex("(?:^|(?<=[\\d.!?;:\\)]))(?<title>[\\p{Lu}][\\p{L}'\\u2019\\-\\s]{3,90}?)[0-9]{5,}(?=\\s|\\p{Lu}|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactNumericSuffixTitleRegex();

    [GeneratedRegex("(?<=[\\p{Ll}\\p{Lo}])\\d{5,}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTrailingNumericSuffixRegex();

    [GeneratedRegex("(?<=[\\p{Lu}]{3})\\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTrailingMeasureNumberRegex();

    [GeneratedRegex(@"(.)\1{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedOcrCharacterRunRegex();

    [GeneratedRegex(@"([a-z]{1,3})\1{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedOcrSyllableRunRegex();
}

internal sealed record DocumentProfileContentCard(
    string Title,
    int? PageStart,
    int? PageEnd,
    string Kind,
    IReadOnlyList<string> Signals,
    DocumentProfileCardEvidence? Evidence = null,
    string? ContentCardId = null);

internal sealed record DocumentProfileCardEvidence(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("scaleBasis")] DocumentProfileScaleBasis? ScaleBasis,
    [property: JsonPropertyName("quantityFacts")] IReadOnlyList<DocumentProfileQuantityFact> QuantityFacts,
    [property: JsonPropertyName("nonScalableReasons")] IReadOnlyList<string> NonScalableReasons,
    [property: JsonPropertyName("confidence")] double? Confidence = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("facts")] IReadOnlyList<DocumentProfileEvidenceFact>? Facts = null);

internal sealed record DocumentProfileScaleBasis(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("label")] string? Label);

internal sealed record DocumentProfileQuantityFact(
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("sourceText")] string SourceText);

internal sealed record DocumentProfileEvidenceFact(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("sourceText")] string? SourceText,
    [property: JsonPropertyName("pageStart")] int? PageStart,
    [property: JsonPropertyName("pageEnd")] int? PageEnd,
    [property: JsonPropertyName("confidence")] double? Confidence = null);

internal sealed record DocumentProfileContentCardCandidate(
    DocumentProfileContentCard Card,
    int Score);

internal sealed record ProjectedDocumentProfile(
    string ProfileVersion,
    string Language,
    string SummaryText,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> HypotheticalQuestions,
    IReadOnlyList<string> Limits,
    string SearchText,
    int TokenCount,
    byte[] Checksum,
    IReadOnlyList<DocumentProfileContentCard> ContentCards);
