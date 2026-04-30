using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class DocumentProfileProjector
{
    private const int MaxSummaryChars = 1100;

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
            docName);
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
        string docName)
    {
        var normalizedSummary = TrimTo(summaryText, MaxSummaryChars);
        var normalizedKeywords = NormalizeList(keywords, 32);
        var normalizedEntities = NormalizeList(entities, 32);
        var normalizedTopics = NormalizeList(topics, 20);
        var normalizedQuestions = NormalizeList(hypotheticalQuestions, 10);
        var normalizedLimits = NormalizeList(limits, 8);
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "und" : language.Trim().ToLowerInvariant();
        var searchText = BuildSearchText(
            docPath,
            docName,
            normalizedSummary,
            normalizedKeywords,
            normalizedEntities,
            normalizedTopics,
            normalizedQuestions,
            normalizedLimits);
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
            Checksum: checksum);
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

    private static string BuildSearchText(
        string docPath,
        string docName,
        string summary,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string> entities,
        IReadOnlyList<string> topics,
        IReadOnlyList<string> questions,
        IReadOnlyList<string> limits)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            docPath,
            docName,
            summary,
            string.Join(' ', keywords),
            string.Join(' ', entities),
            string.Join(' ', topics),
            string.Join(' ', questions),
            string.Join(' ', limits)
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

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-/]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(?:[A-Z]{2,}(?:[-\s]?[A-Z0-9]{2,})+|[A-Z]{1,6}\s?\d{2,}(?:[-/]\d{1,})?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLikeRegex();
}

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
    byte[] Checksum);
