using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal static class DocumentRefResolver
{
    internal sealed record AnalysisResult(
        bool IsContentRequest,
        bool WantsAbout,
        bool WantsSummary,
        bool WantsStoredSummaryCheck,
        bool WantsStoredSummaryStore,
        string? ResolvedDocRef,
        bool NeedsClarification,
        string? ClarificationKind,
        string? ClarificationHint);

    public static AnalysisResult Analyze(
        string? userMessage,
        ToolMemory.DocumentItem? lastFocusedDocument,
        IReadOnlyList<ToolMemory.DocumentItem>? lastListedDocuments = null)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return new AnalysisResult(false, false, false, false, false, null, false, null, null);

        if (LooksLikeSourceOrOpenRequest(s))
            return new AnalysisResult(false, false, false, false, false, null, false, null, null);

        var wantsAbout =
            Regex.IsMatch(s, @"\bde\s+quoi\s+parle\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bwhat\s+(?:is|is this|is the document)\b.*\babout\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bwhat\s+is\s+the\s+purpose\s+of\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bpurpose\s+of\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bworum\s+geht\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bdi\s+cosa\s+parla\b", RegexOptions.IgnoreCase);

        var wantsSummary =
            Regex.IsMatch(s, @"\br[ée]sum[ée]?\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bsummar(?:y|ize)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bzusammenfass", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\briassunt", RegexOptions.IgnoreCase);

        var wantsStoredSummaryStore =
            Regex.IsMatch(s, @"\b(?:store|save|cache|refresh|regenerate|generate)\b.*\b(?:summary|résumé|resume)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:stocke|stocker|enregistre|enregistrer|sauvegarde|sauvegarder|rafraichis|rafraîchis|regenere|régénère|g[ée]n[ée]re|g[ée]n[ée]rer|actualise)\b.*\b(?:r[ée]sum[ée]?|summary)\b", RegexOptions.IgnoreCase)
            || (wantsSummary && Regex.IsMatch(s,
                @"\b(?:pour|pour\s+être|afin\s+de|afin\s+d['’]être|dans\s+le\s+but\s+de|dans\s+le\s+but\s+d['’]être|to\s+be|for|per)\b.*\b(?:stock[ée]?|stored|saved|cached|enregistr[ée]?|sauvegard[ée]?|memorizzat[oa]|salvat[oa])\b",
                RegexOptions.IgnoreCase));

        var wantsStoredSummaryCheck =
            !wantsStoredSummaryStore && (
                Regex.IsMatch(s, @"\b(?:verify|check|confirm)\b.*\b(?:stored|saved|cached)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(s, @"\bis\s+(?:it|the summary)\s+(?:stored|saved|cached)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(s, @"\bv[ée]rif(?:ie|ier)\b.*\b(?:stock[ée]?|enregistr[ée]?)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(s, @"\b(?:est-ce\s+que\s+)?(?:le\s+document|ce\s+document|ce\s+fichier|le\s+fichier).{0,120}\ba\s+un\s+r[ée]sum[ée]?\s+stock[ée]?\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(s, @"\b(?:r[ée]sum[ée]?|summary)\b.*\b(?:stock[ée]?|saved|stored|cached)\b", RegexOptions.IgnoreCase));

        var hasTreeCue =
            s.Contains("arborescence", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(s, @"\btree\b", RegexOptions.IgnoreCase)
            || s.Contains("structure des documents", StringComparison.OrdinalIgnoreCase)
            || s.Contains("catalog structure", StringComparison.OrdinalIgnoreCase)
            || s.Contains("folder structure", StringComparison.OrdinalIgnoreCase);

        var hasTreeScope = LooksLikeTreeScopeAnswer(s);

        if (hasTreeCue && !hasTreeScope && !LooksLikeWordMeaningQuestion(s))
            return new AnalysisResult(false, false, false, false, false, null, true, "tree_scope", "tree_scope");

        var isContentRequest = wantsAbout || wantsSummary || wantsStoredSummaryCheck || wantsStoredSummaryStore;
        if (!isContentRequest)
            return new AnalysisResult(false, false, false, false, false, null, false, null, null);

        var resolvedDocRef = TryResolveDocumentReference(
            s,
            lastFocusedDocument,
            lastListedDocuments,
            allowImplicitFocus: true,
            allowBareIndexWithoutLabel: true);

        if (string.IsNullOrWhiteSpace(resolvedDocRef))
            return new AnalysisResult(true, wantsAbout, wantsSummary, wantsStoredSummaryCheck, wantsStoredSummaryStore, null, true, "doc_reference", "document_reference");

        return new AnalysisResult(true, wantsAbout, wantsSummary, wantsStoredSummaryCheck, wantsStoredSummaryStore, resolvedDocRef, false, null, null);
    }

    public static bool LooksLikeDocumentReferenceAnswer(
        string? userMessage,
        ToolMemory.DocumentItem? lastFocusedDocument,
        IReadOnlyList<ToolMemory.DocumentItem>? lastListedDocuments = null)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(TryResolveDocumentReference(
                s,
                lastFocusedDocument,
                lastListedDocuments,
                allowImplicitFocus: false,
                allowBareIndexWithoutLabel: true)))
        {
            return true;
        }

        return LooksLikePathAnswer(s);
    }

    public static bool LooksLikeTreeScopeAnswer(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        var hasRepositoryScope = s.Contains("server", StringComparison.OrdinalIgnoreCase)
               || s.Contains("serveur", StringComparison.OrdinalIgnoreCase)
               || s.Contains("catalog", StringComparison.OrdinalIgnoreCase)
               || s.Contains("catalogue", StringComparison.OrdinalIgnoreCase)
               || s.Contains("document", StringComparison.OrdinalIgnoreCase)
               || s.Contains("documents", StringComparison.OrdinalIgnoreCase)
               || s.Contains("folder", StringComparison.OrdinalIgnoreCase)
               || s.Contains("folders", StringComparison.OrdinalIgnoreCase)
               || s.Contains("dossier", StringComparison.OrdinalIgnoreCase)
               || s.Contains("dossiers", StringComparison.OrdinalIgnoreCase)
               || s.Contains("filesystem", StringComparison.OrdinalIgnoreCase)
               || s.Contains("file system", StringComparison.OrdinalIgnoreCase);

        if (hasRepositoryScope)
            return true;

        var hasTreeCue = Regex.IsMatch(s, @"\b(?:tree|arborescence|arbre|baum|albero|árbol)\b", RegexOptions.IgnoreCase);
        var hasFullScopeCue = Regex.IsMatch(s, @"\b(?:all(?:\s+the)?|full|whole|entire|complete|complet|compl[eè]te?|tous|toutes|alle|tutti|tutte|todos|todas)\b", RegexOptions.IgnoreCase);
        var hasLevelCue = Regex.IsMatch(s, @"\b(?:level|levels|niveau|niveaux|ebene|ebenen|livello|livelli|nivel|niveles|depth|depths)\b", RegexOptions.IgnoreCase);

        if (hasTreeCue && (hasFullScopeCue || hasLevelCue))
            return true;

        if (hasFullScopeCue && hasLevelCue)
            return true;

        return Regex.IsMatch(s, @"\b(?:full\s+tree|whole\s+tree|entire\s+tree|complete\s+tree|all\s+levels|all\s+the\s+levels|the\s+tree\s+with\s+all\s+the\s+levels|tous\s+les\s+niveaux|toutes\s+les\s+niveaux|alle\s+ebenen|tutti\s+i\s+livelli|todos\s+los\s+niveles|compl[eè]te?|complet)\b", RegexOptions.IgnoreCase);
    }

    public static bool IsRepairMessage(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:tu\s+n'?as\s+rien\s+compris|tu\s+as\s+mal\s+compris|ce\s+n'?est\s+pas\s+ce\s+que\s+j'?ai\s+demande|non\s+ce\s+n'?est\s+pas\s+ca)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:you\s+did\s+not\s+understand|that\s+is\s+not\s+what\s+i\s+asked|that's\s+not\s+what\s+i\s+asked|you\s+misunderstood)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:ich\s+meinte\s+nicht|das\s+ist\s+nicht\s+was\s+ich\s+gefragt\s+habe)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:no\s+es\s+eso\s+que\s+pregunte|no\s+has\s+entendido)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:nao\s+foi\s+isso\s+que\s+eu\s+perguntei|voce\s+nao\s+entendeu)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:non\s+e\s+quello\s+che\s+ho\s+chiesto|non\s+hai\s+capito)\b", RegexOptions.IgnoreCase);
    }

    private static string? TryResolveDocumentReference(
        string s,
        ToolMemory.DocumentItem? lastFocusedDocument,
        IReadOnlyList<ToolMemory.DocumentItem>? lastListedDocuments,
        bool allowImplicitFocus,
        bool allowBareIndexWithoutLabel)
    {
        var explicitPdfRef = TryExtractExplicitPdfReference(s);
        if (!string.IsNullOrWhiteSpace(explicitPdfRef))
            return explicitPdfRef;

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var pdfNum) && pdfNum > 0)
            return $"PDF{pdfNum:00}";

        var mNamed = Regex.Match(s, @"(?i)\b(?:document|doc|file|pdf|fichier)\b(?:\s+num[ée]ro|\s+number|\s+no\.?|\s*#)?\s*0*(?<n>\d{1,4})\b");
        if (mNamed.Success && int.TryParse(mNamed.Groups["n"].Value, out var namedNum) && namedNum > 0)
            return namedNum.ToString();

        if (allowBareIndexWithoutLabel)
        {
            var mBare = Regex.Match(s, @"\b(?<n>\d{1,4})\b");
            if (mBare.Success && lastListedDocuments is { Count: > 0 } && int.TryParse(mBare.Groups["n"].Value, out var bareNum) && bareNum > 0 && bareNum <= lastListedDocuments.Count)
                return bareNum.ToString();
        }

        if (TryResolveRelativeListReference(s, lastFocusedDocument, lastListedDocuments, out var relativeDoc))
            return SelectDocRef(relativeDoc);

        if (TryResolveOrdinalListReference(s, lastListedDocuments, out var ordinalDoc))
            return SelectDocRef(ordinalDoc);

        if (LooksLikePathAnswer(s))
            return s.Trim();

        if (lastFocusedDocument is not null && UsesCurrentDocumentPronoun(s))
            return SelectDocRef(lastFocusedDocument);

        if (allowImplicitFocus && lastFocusedDocument is not null)
            return SelectDocRef(lastFocusedDocument);

        return null;
    }

    private static string? TryExtractExplicitPdfReference(string s)
    {
        var prefixedMatch = Regex.Match(
            s,
            @"(?i)\b(?:document|doc|fichier|file)\b\s+(?<ref>(?:[A-Za-z0-9_.\-]+[\\/])+[A-Za-z0-9_][A-Za-z0-9_.\- ]*\.pdf|[A-Za-z0-9_][A-Za-z0-9_.\- ]*\.pdf)\b");
        if (prefixedMatch.Success)
            return SanitizePdfReference(prefixedMatch.Groups["ref"].Value);

        var pathMatch = Regex.Match(
            s,
            @"(?i)(?<ref>(?:[A-Za-z0-9_.\-]+[\\/])+[A-Za-z0-9_][A-Za-z0-9_.\- ]*\.pdf)\b");
        if (pathMatch.Success)
            return SanitizePdfReference(pathMatch.Groups["ref"].Value);

        var bareNameMatch = Regex.Match(
            s,
            @"(?i)\b(?<ref>[A-Za-z0-9_][A-Za-z0-9_.\-]*\.pdf)\b");
        if (bareNameMatch.Success)
            return SanitizePdfReference(bareNameMatch.Groups["ref"].Value);

        return null;
    }
    private static string? SanitizePdfReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();

        candidate = Regex.Replace(
            candidate,
            @"(?i)^.*?\b(?:document|doc|fichier|file|pdf)\b\s+",
            string.Empty);

        candidate = candidate.Trim(' ', '"', '\'', '`');
        candidate = candidate.Replace("«", string.Empty)
                             .Replace("»", string.Empty)
                             .Replace("“", string.Empty)
                             .Replace("”", string.Empty)
                             .Replace("‘", string.Empty)
                             .Replace("’", string.Empty)
                             .Trim();

        return candidate.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static bool TryResolveRelativeListReference(
        string s,
        ToolMemory.DocumentItem? lastFocusedDocument,
        IReadOnlyList<ToolMemory.DocumentItem>? lastListedDocuments,
        out ToolMemory.DocumentItem resolved)
    {
        resolved = default!;
        if (lastListedDocuments is not { Count: > 0 })
            return false;

        var focusIndex = FindFocusedIndex(lastFocusedDocument, lastListedDocuments);

        if (ContainsAny(s, "previous", "previous one", "previous file", "previous document", "the one before", "before that", "précédent", "précédente", "document précédent", "fichier précédent", "celui d'avant", "celui avant", "d'avant", "vorherige", "vorherigen", "precedente"))
        {
            if (focusIndex > 0)
            {
                resolved = lastListedDocuments[focusIndex.Value - 1];
                return true;
            }
        }

        if (ContainsAny(s, "next", "next one", "next file", "next document", "suivant", "suivante", "document suivant", "fichier suivant", "nächste", "successivo", "seguente"))
        {
            if (focusIndex.HasValue && focusIndex.Value + 1 < lastListedDocuments.Count)
            {
                resolved = lastListedDocuments[focusIndex.Value + 1];
                return true;
            }
        }

        if (ContainsAny(s, "last", "last one", "the last file", "the last document", "dernier", "dernière", "le dernier", "la dernière", "ultimo", "letzte"))
        {
            resolved = lastListedDocuments[lastListedDocuments.Count - 1];
            return true;
        }

        if (ContainsAny(s, "first", "first one", "the first file", "the first document", "premier", "première", "le premier", "la première", "primo", "erste"))
        {
            resolved = lastListedDocuments[0];
            return true;
        }

        return false;
    }

    private static bool TryResolveOrdinalListReference(string s, IReadOnlyList<ToolMemory.DocumentItem>? lastListedDocuments, out ToolMemory.DocumentItem resolved)
    {
        resolved = default!;
        if (lastListedDocuments is not { Count: > 0 })
            return false;

        var index = TryResolveOrdinalIndex(s);
        if (!index.HasValue)
            return false;

        var zeroBased = index.Value - 1;
        if (zeroBased < 0 || zeroBased >= lastListedDocuments.Count)
            return false;

        resolved = lastListedDocuments[zeroBased];
        return true;
    }

    private static int? TryResolveOrdinalIndex(string s)
    {
        var normalized = s.ToLowerInvariant();

        foreach (var (term, index) in OrdinalTerms)
        {
            if (normalized.Contains(term, StringComparison.Ordinal))
                return index;
        }

        var ordinalMatch = Regex.Match(normalized, @"\b(?<n>\d{1,2})(?:st|nd|rd|th|er|e|eme|ème)\b", RegexOptions.IgnoreCase);
        if (ordinalMatch.Success && int.TryParse(ordinalMatch.Groups["n"].Value, out var ordinalNum) && ordinalNum > 0)
            return ordinalNum;

        return null;
    }

    private static int? FindFocusedIndex(ToolMemory.DocumentItem? focused, IReadOnlyList<ToolMemory.DocumentItem> lastListedDocuments)
    {
        if (focused is null)
            return null;

        for (var i = 0; i < lastListedDocuments.Count; i++)
        {
            var item = lastListedDocuments[i];
            if ((!string.IsNullOrWhiteSpace(focused.DocId) && string.Equals(item.DocId, focused.DocId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(focused.DocPath) && string.Equals(item.DocPath, focused.DocPath, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(focused.DocName) && string.Equals(item.DocName, focused.DocName, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return null;
    }

    private static string SelectDocRef(ToolMemory.DocumentItem doc)
        => !string.IsNullOrWhiteSpace(doc.DocId)
            ? doc.DocId
            : !string.IsNullOrWhiteSpace(doc.DocPath)
                ? doc.DocPath
                : doc.DocName;

    private static bool UsesCurrentDocumentPronoun(string s)
        => ContainsAny(s,
            "ce document",
            "ce fichier",
            "this document",
            "that document",
            "this file",
            "that file",
            "this one",
            "that one",
            "celui-ci",
            "celui ci",
            "celui-là",
            "celui la",
            "dieses dokument",
            "questo documento");

    private static bool LooksLikeSourceOrOpenRequest(string s)
        => s.Contains("source", StringComparison.OrdinalIgnoreCase)
           || s.Contains("lien", StringComparison.OrdinalIgnoreCase)
           || s.Contains("link", StringComparison.OrdinalIgnoreCase)
           || s.Contains("open", StringComparison.OrdinalIgnoreCase)
           || s.Contains("ouvrir", StringComparison.OrdinalIgnoreCase)
           || s.Contains("ouvre", StringComparison.OrdinalIgnoreCase)
           || s.Contains("öffn", StringComparison.OrdinalIgnoreCase)
           || s.Contains("oeffn", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePathAnswer(string s)
        => s.Contains('/', StringComparison.Ordinal)
           || s.Contains('\\', StringComparison.Ordinal)
           || s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWordMeaningQuestion(string s)
    {
        var hasMeaningCue =
            Regex.IsMatch(s, @"\bque\s+veux?\s+dire\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bveut\s+dire\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bmean(?:ing)?\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bwas\s+bedeutet\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bbedeutet\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bcosa\s+significa\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\bsignifica\b", RegexOptions.IgnoreCase);

        if (!hasMeaningCue)
            return false;

        return s.Contains("arborescence", StringComparison.OrdinalIgnoreCase)
               || s.Contains("ce mot", StringComparison.OrdinalIgnoreCase)
               || s.Contains("this word", StringComparison.OrdinalIgnoreCase)
               || s.Contains("dieses wort", StringComparison.OrdinalIgnoreCase)
               || s.Contains("questa parola", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(s, "['\"“”‘’][^'\"“”‘’]{2,40}['\"“”‘’]");
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static readonly (string term, int index)[] OrdinalTerms =
    {
        ("first", 1), ("1st", 1), ("premier", 1), ("première", 1), ("erste", 1), ("primo", 1),
        ("second", 2), ("2nd", 2), ("deuxième", 2), ("deuxieme", 2), ("zweite", 2), ("secondo", 2),
        ("third", 3), ("3rd", 3), ("troisième", 3), ("troisieme", 3), ("dritte", 3), ("terzo", 3),
        ("fourth", 4), ("4th", 4), ("quatrième", 4), ("quatrieme", 4), ("vierte", 4), ("quarto", 4),
        ("fifth", 5), ("5th", 5), ("cinquième", 5), ("cinquieme", 5), ("fünfte", 5), ("funfte", 5), ("quinto", 5)
    };
}
