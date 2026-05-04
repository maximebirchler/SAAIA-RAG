using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class DocumentProfileProjector
{
    private const int MaxSummaryChars = 1100;
    private const int MaxContentCards = 240;

    public static ProjectedDocumentProfile Project(
        string docPath,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries)
    {
        var docName = Path.GetFileName(docPath.Replace('\\', '/'));
        var corpus = BuildCorpus(docName, sections, units);
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
        var summary = BuildSummary(docName, pages.Count, sectionTitles, units, language);
        var hypotheticalQuestions = BuildHypotheticalQuestions(docName, keywords, entities, language);
        var limits = BuildLimits(language);
        var contentCards = BuildContentCards(sections, units, exactMatchEntries, keywords);
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

    private static string DetectLanguage(string text)
    {
        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return "und";

        var scores = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["fr"] = CountHits(normalized, [" le ", " la ", " les ", " des ", " une ", " pour ", " avec ", " dans ", " cette ", " vous ", " etape ", " ingredients "]),
            ["en"] = CountHits(normalized, [" the ", " and ", " with ", " from ", " this ", " that ", " for ", " section ", " chapter ", " safety ", " requirements "]),
            ["es"] = CountHits(normalized, [" el ", " la ", " los ", " las ", " una ", " para ", " con ", " esta ", " receta ", " ingredientes "]),
            ["pt"] = CountHits(normalized, [" de ", " para ", " com ", " uma ", " esta ", " receita ", " ingredientes ", " seguranca "]),
            ["de"] = CountHits(normalized, [" der ", " die ", " das ", " und ", " mit ", " fuer ", " ist ", " sicherheit ", " kapitel "]),
            ["it"] = CountHits(normalized, [" il ", " lo ", " la ", " gli ", " con ", " per ", " una ", " ricetta ", " ingredienti "])
        };

        var best = scores.OrderByDescending(static item => item.Value).First();
        return best.Value <= 0 ? "und" : best.Key;
    }

    private static int CountHits(string normalized, IReadOnlyList<string> needles)
    {
        var padded = $" {normalized} ";
        return needles.Count(needle => padded.Contains(needle, StringComparison.Ordinal));
    }

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
                _ => $"Que dit {docName} sur {subject} ?"
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
            _ => ["Profil deterministe genere depuis le texte extrait.", "Utiliser les extraits de pages pour les faits, quantites, etapes et citations exacts."]
        };

    private static IReadOnlyList<DocumentProfileContentCard> BuildContentCards(
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
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

        foreach (var unit in units.OrderBy(static unit => unit.Ordinal))
        {
            sectionTitleByOrdinal.TryGetValue(unit.SectionOrdinal ?? -1, out var sectionTitle);
            foreach (var title in ExtractLeadTitles(unit.Text).Take(3))
            {
                AddContentCardCandidate(
                    candidates,
                    title,
                    unit.PageStart,
                    unit.PageEnd,
                    "unit_lead",
                    $"{sectionTitle} {unit.Text}",
                    keywords,
                    score: ComputeContentCardScore("unit_lead", title, unit.Text, 75));
            }
        }

        foreach (var entry in exactMatchEntries
                     .Where(static entry => entry.Kind == "verbatim_excerpt")
                     .OrderBy(static entry => entry.EntryIndex))
        {
            foreach (var title in ExtractLeadTitles(entry.Text).Take(2))
            {
                AddContentCardCandidate(
                    candidates,
                    title,
                    entry.PageStart,
                    entry.PageEnd,
                    "exact_lead",
                    entry.Text,
                    keywords,
                    score: ComputeContentCardScore("exact_lead", title, entry.Text, 90));
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

            AddBalancedCandidate(selected, seen, group[0]);
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

    private static void AddContentCardCandidate(
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
            return;
        if (LooksLikeLowSignalContentCardLead(cleanTitle, kind))
            return;

        var signals = BuildCardSignals(cleanTitle, context, documentKeywords);
        candidates.Add(new DocumentProfileContentCardCandidate(
            new DocumentProfileContentCard(
                cleanTitle,
                pageStart,
                pageEnd,
                string.IsNullOrWhiteSpace(kind) ? "content_item" : kind,
                signals),
            score));
    }

    private static IEnumerable<string> ExtractLeadTitles(string? text)
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

        var compactHead = CollapseWhitespace(text);
        if (compactHead.Length > 0)
        {
            foreach (var candidate in ExtractLeadTitleCandidatesFromLine(TrimTo(compactHead, 220)))
                yield return candidate;
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

        return ContainsAny(
            normalizedContext,
            "ingredient",
            "ingredients",
            "zutaten",
            "method",
            "procedure",
            "procedures",
            "preparation",
            "preparations",
            "preparacion",
            "preparacao",
            "etape",
            "etapes",
            "steps",
            "instructions",
            "requirements",
            "warning",
            "caution",
            "consigne",
            "safety");
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

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    private static bool LooksLikeLowSignalContentCardLead(string title, string kind)
    {
        if (string.Equals(kind, "section", StringComparison.Ordinal))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokenCount = CountTokens(title);
        if (ImperativeInstructionLeadRegex().IsMatch(normalized) && tokenCount >= 3)
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
        if (tokenCount is < 2 or > 14)
            return false;

        if (!title.Any(char.IsLetter))
            return false;

        if (title.Count(char.IsDigit) > Math.Max(4, title.Length / 3))
            return false;

        if (title.Count(static ch => ch is ',' or ';' or ':' or '|' or '/') > 4)
            return false;

        if (LooksLikeGluedNavigationOrHeaderTitle(title))
            return false;

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        var normalizedFolded = FoldDiacritics(normalized);

        if (ContentCardTitleStopwords.Contains(normalizedFolded))
            return false;
        if (LooksLikeGenericContentCardTitle(normalizedFolded))
            return false;
        if (PageReferenceFragmentRegex().IsMatch(normalizedFolded)
            && (tokenCount >= 4 || normalizedFolded.Contains("table des matieres", StringComparison.Ordinal)))
        {
            return false;
        }
        if (LooksLikeSentenceOrInstructionTitle(normalizedFolded, tokenCount))
            return false;
        if (ParameterFragmentTitleRegex().IsMatch(normalizedFolded))
            return false;

        if (tokenCount <= 2 && title.Any(char.IsDigit))
            return false;

        var firstToken = normalizedFolded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstToken is not null && ContentCardLeadStopwords.Contains(firstToken) && tokenCount <= 3)
            return false;

        return true;
    }

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

        if (SentenceLeadTitleRegex().IsMatch(normalizedFolded))
            return true;

        if (tokenCount >= 4 && SentenceVerbTitleRegex().IsMatch(normalizedFolded))
            return true;

        return normalizedFolded.Contains("ingredientspreparation", StringComparison.Ordinal)
            || normalizedFolded.Contains("ingredients preparation", StringComparison.Ordinal)
            || normalizedFolded.Contains("par portion", StringComparison.Ordinal)
            || normalizedFolded.Contains("ppréparation", StringComparison.Ordinal)
            || normalizedFolded.Contains("ppreparation", StringComparison.Ordinal)
            || normalizedFolded.EndsWith(" a votre gout", StringComparison.Ordinal)
            || normalizedFolded.Contains("be a master", StringComparison.Ordinal)
            || normalizedFolded.Contains("become a chef", StringComparison.Ordinal);
    }

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
            || ServingLeadTitleRegex().IsMatch(normalizedTitle)
            || normalizedTitle.Contains("table des matieres", StringComparison.Ordinal)
            || normalizedTitle.Contains("table of contents", StringComparison.Ordinal)
            || normalizedTitle.Contains(" indd ", StringComparison.Ordinal)
            || normalizedTitle.StartsWith("couv ", StringComparison.Ordinal)
            || normalizedTitle.StartsWith("cover ", StringComparison.Ordinal);

    private static string[] BuildCardSignals(
        string title,
        string? context,
        IReadOnlyList<string> documentKeywords)
    {
        var localKeywords = ExtractKeywords($"{title} {context}", maxKeywords: 8);
        return NormalizeList(
            new[] { title }
                .Concat(localKeywords)
                .Concat(documentKeywords.Take(6)),
            10);
    }

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

            var key = ExactMatchEntryExtractor.NormalizeForLookup(title);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            normalized.Add(new DocumentProfileContentCard(
                title,
                card.PageStart,
                card.PageEnd,
                string.IsNullOrWhiteSpace(card.Kind) ? "content_item" : CollapseWhitespace(card.Kind),
                NormalizeList(card.Signals, 10)));

            if (normalized.Count >= MaxContentCards)
                break;
        }

        return normalized;
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
                parsed.Add(new DocumentProfileContentCard(title ?? string.Empty, pageStart, pageEnd, kind, signals));
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
            string.Join(' ', contentCards.Select(static card => $"{card.Title} {BuildContentCardLookupText(card)} {string.Join(' ', card.Signals)}"))
        }));

    private static string BuildContentCardLookupText(DocumentProfileContentCard card)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(card.Title)),
            FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(string.Join(' ', card.Signals)))
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
        "ingredients", "ingredient", "preparation", "preparations", "method", "methods",
        "etapes", "etape", "steps", "step", "notes", "note", "source", "sources",
        "sommaire", "contents", "table of contents", "index", "menu", "menus",
        "temps total", "total time", "duree totale", "durée totale",
        "document", "documents", "page", "pages"
    };

    private static readonly HashSet<string> ContentCardLeadStopwords = new(StringComparer.Ordinal)
    {
        "this", "that", "these", "those", "cette", "cela", "voici", "pour", "avec",
        "dans", "vous", "nous", "the", "and", "from", "para", "como", "esta",
        "este", "oder", "und", "der", "die", "das", "per", "con"
    };

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-/]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(?:[A-Z]{2,}(?:[-\s]?[A-Z0-9]{2,})+|[A-Z]{1,6}\s?\d{2,}(?:[-/]\d{1,})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLikeRegex();

    [GeneratedRegex(@"(?<left>[\p{Ll}\p{Lo}])(?<right>[\p{Lu}][\p{Ll}]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LowerToUpperBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=[\p{Lu}]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex LowerOrDigitToUpperTitleBoundaryRegex();

    [GeneratedRegex(@"^\s*(?:page\s*)?(?:\d+[\.)\]\-:]*\s*|[IVXLCDM]+(?:[\.)\]\-:]\s*|\s+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"\s*\[(?:index|contents?|sommaire).*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BracketedIndexSuffixRegex();

    [GeneratedRegex(@"^(?:ingredients?|preparations?|method|methods|steps?|etapes?|sources?|references?|notes?|materiel|matériel|technique|suggestions?|par portion|nutrition|valeurs nutritionnelles|temps total|total time|duree totale|durée totale|temps de preparation|temps de préparation|temps de cuisson|nombre de personnes|number of servings|serving count)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericContentCardTitlePrefixRegex();

    [GeneratedRegex(@"^(?:ajouter|ajoutez?|add|au bout de|cassez?|contournez?|couper|coupez?|cut|dans le robot|decorer|d[ée]corer|disposer|elaborer|[ée]laborer|enlever|ensuite|faites?|farinez?|fermer|filtrer|gouter|go[ûu]ter|incorporez?|lancez?|laissez?|melanger|m[ée]langer|m[ée]langez?|mettez?|mettre|mixez?|nettoyer|ouvrir|placer|preparer|pr[ée]parer|programmer|puis|quand|raclez?|ramenez?|recommencer|remplacez?|repartir|repartissez?|r[ée]partir|r[ée]partissez?|retirer|salez?|servir|triturer|utilisez?|utiliser|verser|versez?|verifier|v[ée]rifier)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InstructionLeadTitleRegex();

    [GeneratedRegex(@"^(?:avec des|ce|cela|celle|celui|cette|elle|elles|est|facultatif\)?|fonctionne|il|ils|it|pour cette|pour le|pour la|pour les|se|sel,?\s|si vous|this|vous)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentenceLeadTitleRegex();

    [GeneratedRegex(@"\b(?:est|sont|doit|doivent|peut|peuvent|pouvez|pourrez|permet|permettent|recommande|recommandons|utilisez|utiliser|trouver|trouvez|preparez|pr[ée]parez|pr[ée]par[ée]s?|m[ée]langez|ajoutez|ouvrez|fermez|retirez|servez|is|are|can|must|should|allows?|use|uses|using|prepare|prepared|serves?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SentenceVerbTitleRegex();

    [GeneratedRegex(@"^(?:abaissez|ajoutez?|arrosez|assaisonnez|assaisonnez-les|badigeonnez|battez|beurrez|cassez|choisissez|couvrez|creusez|d[ée]coupez|d[ée]posez|dressez|[ée]crasez|[ée]gouttez|emportez|enduisez|enfournez|enlevez|[ée]talez|farinez|filtrez|foncez|garnissez|glissez|incorporez|lavez|manipulez|m[ée]langez|passez|p[ée]trissez|piquez|placez|placez-les|posez|pr[ée]chauffez|ramenez|r[ée]alisez|recouvrez|rectifiez|remettez|r[ée]partissez|repartissez|r[ée]servez|roulez|saisissez?|saupoudrez|sortez)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ImperativeInstructionLeadRegex();

    [GeneratedRegex(@"^(?:arroser|badigeonner|casser|cuire|d[ée]poser|[ée]paissir|epaissir|[ée]plucher|faire|farcir|garnir|hacher|incorporer|laisser|laver|m[ée]langer|porter|r[ée]aliser|recouvrir|r[ée]server|rincer)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InfinitiveInstructionLeadRegex();

    [GeneratedRegex(@"^(?:a l aide|a la fin|apres|bien|bonne nouvelle|c est|ca|ceci|cela|dans tous les cas|emportez|garder|gardez|glissez|l idee|le repas|manipulez|mais la aussi|n hesitez|on|onne|ou saisir|pendant ce temps|pour connaitre|pour des preparations|pour des recettes|pour l|pour vous|pourtant|quellesatisfaction|rectifiez|roulez|saisir|saupoudrez|si vous|suivant le|voici)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowSignalSentenceLeadRegex();

    [GeneratedRegex(@"^(?:pour|for|para|per)\s+\d+\s+(?:personnes?|people|servings?|portions?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ServingLeadTitleRegex();

    [GeneratedRegex(@"^(?:vitesse|speed|temperature|température|temp|mode|programme|program|rpm|tr/min|minutes?|mins?|seconds?|secondes?|heures?|hours?)\b.*\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ParameterFragmentTitleRegex();

    [GeneratedRegex(@"\b(?:page|pages?|p\.?)\s*\d+\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PageReferenceFragmentRegex();

    [GeneratedRegex(@"^\s*(?<title>.{4,90}?)(?:\s{2,}|[\.:\-\u2013\u2014]\s+|(?=\b(?:for|pour|para|per|mit|avec|with|ingredients?|ingredienti|zutaten|preparation|method|steps?)\b))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadBoundaryRegex();

    [GeneratedRegex(@"(?<title>\b[\p{Lu}][\p{Lu}\p{Nd}'’\-\s]{4,90})(?=\d|\s{2,}|[\.:\-\u2013\u2014]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseTitleRegex();

    [GeneratedRegex(@"(?<![\p{Lu}\p{Nd}])(?<title>[\p{Lu}][\p{Lu}\p{Nd}'’\-\s]{4,90}?)(?=\p{Lu}\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex GluedUppercaseTitleRegex();

    [GeneratedRegex("(?:^|(?<=[\\d.!?;:\\)]))(?<title>[\\p{Lu}][\\p{L}'\\u2019\\-\\s]{3,90}?)[0-9]{5,}(?=\\s|\\p{Lu}|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactNumericSuffixTitleRegex();

    [GeneratedRegex("(?<=[\\p{Ll}\\p{Lo}])\\d{5,}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTrailingNumericSuffixRegex();

    [GeneratedRegex("(?<=[\\p{Lu}]{3})\\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTrailingMeasureNumberRegex();
}

internal sealed record DocumentProfileContentCard(
    string Title,
    int? PageStart,
    int? PageEnd,
    string Kind,
    IReadOnlyList<string> Signals);

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
