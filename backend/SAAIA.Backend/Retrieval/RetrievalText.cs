using System.Text.RegularExpressions;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalText
{

    internal static IReadOnlyList<Match> SafeRegexMatches(Regex regex, string input)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<Match>();

        try
        {
            var matches = regex.Matches(input);
            var copy = new List<Match>(matches.Count);
            foreach (Match match in matches)
                copy.Add(match);
            return copy;
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<Match>();
        }
    }


    internal static IReadOnlyList<Match> SafeRegexMatches(
        string input,
        string pattern,
        RegexOptions options,
        TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<Match>();

        try
        {
            var matches = Regex.Matches(input, pattern, options, timeout);
            var copy = new List<Match>(matches.Count);
            foreach (Match match in matches)
                copy.Add(match);
            return copy;
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<Match>();
        }
    }


    internal static Match SafeRegexMatch(Regex regex, string input)
    {
        if (string.IsNullOrEmpty(input))
            return Match.Empty;

        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }


    internal static bool SafeRegexIsMatch(
        string input,
        string pattern,
        RegexOptions options,
        TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        try
        {
            return Regex.IsMatch(input, pattern, options, timeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }


    internal static string SafeRegexReplace(
        string input,
        string pattern,
        string replacement,
        RegexOptions options,
        TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        try
        {
            return Regex.Replace(input, pattern, replacement, options, timeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }


    internal static readonly HashSet<string> LexicalStopwords = new(StringComparer.Ordinal)
    {
        "dans", "avec", "sans", "pour", "vers", "entre", "apres", "avant",
        "quel", "quelle", "quels", "quelles", "trouve", "trouver", "montre",
        "montrez", "ou", "sont", "sous", "plus", "moins", "comme", "cela",
        "cette", "cet", "ces", "leurs", "leur", "par", "sur", "des", "une",
        "bien", "irait", "iraient", "convient", "conviendrait",
        "les", "que", "quoi", "dont", "when", "where", "which", "with", "from",
        "would", "could", "should", "well", "goes",
        "this", "that", "those", "these", "what", "into", "pdf", "doc", "document",
        "manuel", "manual", "guide", "please", "stp", "svp", "cherche", "show",
        "category", "categorie", "difficult", "difficulty", "difficile", "grounded",
        "donne", "donner", "donnez", "give",
        "retrouve", "retrouver", "retrouvez",
        "need", "have", "has", "just", "juste", "moi", "peux", "avoir",
        "faire", "fais", "fait", "make", "help", "aide", "aider",
        "parle", "parler", "parlent", "documents", "compare", "comparer", "comparison",
        "difference", "differences", "different", "deux", "corpus",
        "methode", "method", "style", "lequel", "veux", "veut", "simple",
        "fiche", "claire", "clair", "detail", "details", "detailee", "detaillee",
        "etape", "etapes", "step", "steps", "source", "sources", "citation",
        "citations", "reponse", "answer", "format", "liste", "list",
        "passage", "passages", "reponds", "repondre", "respond",
        "plusieurs", "multiple", "several", "many",
        "temps", "time", "duration", "duree",
        "existe", "existes", "existent", "exister", "existing", "exist", "exists",
        "disponible", "disponibles", "available", "availability",
        "utile", "utiles", "useful", "pertinent", "pertinents", "relevant",
        "contient", "contiennent", "containing", "contain", "contains",
        "mentionne", "mentionnent", "mentioning", "mentions",
        "presente", "presentent", "present", "presents",
        "explique", "expliquer", "expliquez", "explain", "explains",
        "enchainement"
    };


    internal static readonly HashSet<string> SpecificAnchorStopwords = new(StringComparer.Ordinal)
    {
        "items", "elements",
        "preparation", "materials", "materiaux", "components", "composants", "procedure", "procedures",
        "methode", "method", "methods", "instruction", "instructions",
        "materiel", "material", "materials", "exigence", "exigences", "requirement", "requirements",
        "warning", "warnings", "caution", "cautions", "summary", "overview",
        "checklist", "source", "sources", "citation", "citations", "detail",
        "details", "format", "answer", "reponse", "liste", "list",
        "listes", "tableau", "tableaux", "table", "tables",
        "valeur", "valeurs", "value", "values",
        "critere", "criteres", "criteria", "criterion",
        "associe", "associes", "associee", "associees", "associated",
        "installe", "installer", "installation", "install", "installed", "installing",
        "verifier", "verification", "verify", "verified", "checking",
        "temps", "time", "duration", "duree"
    };


    internal static readonly HashSet<string> DocumentOverviewTopicStopwords = new(StringComparer.Ordinal)
    {
        "document", "documents", "doc", "docs", "pdf", "fichier", "fichiers",
        "file", "files", "source", "sources", "livre", "livres", "book", "books",
        "rapport", "rapports", "report", "reports", "manuel", "manuels", "manual", "manuals",
        "corpus", "base", "knowledge", "database", "disponible", "disponibles", "available",
        "inventaire", "inventory", "overview", "panorama", "panoramica", "uberblick",
        "liste", "list", "lister", "vois", "voir", "servir", "sert", "servent",
        "documentos", "fuentes", "archivos", "libros", "ficheiros", "arquivos", "livros",
        "dokumente", "quellen", "dateien", "bucher", "berichte", "handbucher",
        "documenti", "fonti", "libri", "manuali",
        "category", "categorie", "difficult", "difficulty", "difficile", "grounded"
    };


    internal static readonly HashSet<string> PrimaryAnchorStopwords = new(StringComparer.Ordinal)
    {
        "organiser", "organise", "organization", "organisation", "planifier",
        "planning", "prevoir", "preparer", "prepare", "prepared", "avance",
        "weekly", "hebdomadaire", "semaine",
        "question", "questions", "utilisateur", "client", "besoin", "conseil",
        "conseils", "recommendation", "recommendations"
    };


    internal static readonly HashSet<string> FocusedLookupRequestLeadTokens = new(StringComparer.Ordinal)
    {
        "comment", "how", "como", "come", "wie"
    };


    internal static readonly HashSet<string> FocusedLookupSecondaryQualifierTokens = new(StringComparer.Ordinal)
    {
        "blanc", "blanche", "blanches", "blancs", "white",
        "noir", "noire", "noires", "noirs", "black",
        "rouge", "rouges", "red",
        "vert", "verte", "vertes", "verts", "green",
        "jaune", "jaunes", "yellow",
        "bleu", "bleue", "bleues", "bleus", "blue",
        "brun", "brune", "brunes", "bruns", "brown",
        "rose", "roses", "pink",
        "clair", "claire", "claires", "clairs", "clear",
        "fonce", "foncee", "foncees", "fonces", "dark",
        "doux", "douce", "douces", "mild",
        "sec", "seche", "seches", "secs", "dry",
        "frais", "fraiche", "fraiches", "fresh"
    };


    internal static readonly HashSet<string> TitleConnectorTokens = new(StringComparer.Ordinal)
    {
        "a", "al", "alla", "all", "an", "and", "as", "at", "au", "aux",
        "avec", "com", "con", "d", "da", "de", "del", "della", "des", "di", "do",
        "dos", "du", "e", "em", "en", "et", "for", "la", "las", "le", "les",
        "lo", "los", "mit", "of", "on", "the", "to", "und", "with", "y", "zu",
        "zum", "zur"
    };


    internal static readonly HashSet<string> ComparativeSubjectStopwords = new(StringComparer.Ordinal)
    {
        "est", "sont", "etre", "etaient", "sera", "serait", "is", "are", "was", "were",
        "the", "le", "la", "les", "un", "une", "des", "du", "de", "del", "der", "die",
        "quel", "quelle", "quels", "quelles", "which", "what", "cual", "qual", "quale",
        "welches", "welcher", "welche", "plus", "most", "mas", "mais", "mehr"
    };


    internal static readonly HashSet<string> ComparativeOperandConceptStopwords = new(StringComparer.Ordinal)
    {
            "artificiel", "artificielle", "artificial", "choix", "chose", "comment", "contradiction",
            "creer", "create", "creating", "different", "difference", "differences", "disent",
        "deduire", "deduit", "depui", "depuis", "document", "documents", "exactement", "explain",
        "explique", "expliquer", "illisible", "meme", "nuance", "autre", "other", "same",
        "thing", "valeur", "value", "without"
    };


    internal static readonly HashSet<string> DocumentHintStopwords = new(StringComparer.Ordinal)
    {
        "PDF", "DOC", "DOCUMENT", "GUIDE", "GUIDANCE", "MANUAL", "MANUEL", "NOTICE",
        "SYSTEM", "SYSTEMS", "PROCESS", "PROCESSUS", "PROCEDURE",
        "THE", "FOR", "AND", "WITH", "SUR",
        "POUR", "DES", "LES", "UNE", "UN", "DU", "DE", "LA", "LE", "ET", "ON", "OF"
    };


    internal sealed record RrfAccumulator(double Score, RagMatch Representative);

    internal sealed record MatchedRetrievalContext(IReadOnlyList<string> DocHints);

    internal sealed record GuidanceQualityRisk(bool RequiresCaveat, string Reason);

}
