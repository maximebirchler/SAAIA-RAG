using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record ResolvedDocRef(
        string DocId,
        string DocPath,
        string DocName,
        string? Category,
        string? CategoryPath,
        int? Pages,
        string? CategoryRef = null,
        string? SourceHash = null,
        string? DocLanguage = null,
        string? ProfileLanguage = null);
    private sealed record SummaryChunk(
        string Text,
        int PageStart,
        int PageEnd,
        int ChunkIndex,
        string DocPath,
        string DocName,
        string? DocId = null,
        string? SourceHash = null,
        string? DocLanguage = null,
        string? ProfileLanguage = null,
        string? Category = null,
        string? CategoryRef = null,
        string? CategoryPath = null,
        string? ChunkId = null,
        string? SectionTitle = null,
        string? HeadingPath = null,
        string? PrevChunkId = null,
        string? NextChunkId = null,
        string? SameSectionChunkId = null,
        string? OriginalChunkType = null,
        int? OffsetStart = null,
        int? OffsetEnd = null,
        string? ExtractionSource = null,
        string? DocumentQualityStatus = null,
        string? PageQualityStatus = null,
        string? TextStatus = null,
        string? ChunkTextStatus = null,
        bool? ChunkTextSparse = null,
        bool? ChunkOcrCandidate = null,
        string? QualityStatus = null,
        double? ExtractionConfidence = null,
        double? DocumentExtractionConfidence = null,
        double? PageExtractionConfidence = null,
        bool ManualReviewRecommended = false,
        bool DocumentManualReviewRecommended = false,
        bool PageManualReviewRecommended = false,
        bool OcrAttempted = false,
        bool OcrApplied = false,
        bool OcrRecommended = false,
        ToolMemory.SourceExtractionDiagnosticRef? ExtractionDiagnosticSummary = null,
        IReadOnlyList<string>? QualitySignals = null,
        IReadOnlyList<string>? ChunkQualitySignals = null,
        IReadOnlyList<ToolMemory.SourceContentCardRef>? MatchedContentCards = null,
        ToolMemory.SourceProfileSignalsRef? ProfileSignals = null,
        string? SelectionHintEvidenceRole = null,
        int? SelectionHintActionabilityScore = null,
        int? SelectionHintSupportScore = null,
        int? SelectionHintFragmentScore = null,
        int? SelectionHintNavigationScore = null,
        int? SelectionHintQualityPenalty = null,
        string? ContentRole = null,
        string? NavigationReason = null,
        double? RetrievalNavigationScore = null,
        double? ContentDensityScore = null);

    private static string? GetRagCategoryScopeArg(JsonElement args)
        => NormalizeCategoryPathArg(
            GetStringArg(args, "categoryPath")
            ?? GetNestedStringArg(args, "filters", "categoryPath")
            ?? GetStringArg(args, "categoryRef")
            ?? GetNestedStringArg(args, "filters", "categoryRef")
            ?? GetStringArg(args, "category")
            ?? GetNestedStringArg(args, "filters", "category"));

    private static bool LooksLikeReindexableDocumentPath(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }


    private async Task<(string? categoryPath, string? categoryRef)> ResolveCategoryScopeArgsAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = GetStringArg(args, "categoryPath") ?? GetStringArg(args, "path") ?? GetStringArg(args, "category");
        var rawRef = GetStringArg(args, "categoryRef");

        var normalizedPath = NormalizeCategoryPathArg(rawPath);
        var normalizedRef = string.IsNullOrWhiteSpace(rawRef) ? null : rawRef.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedRef) || !string.IsNullOrWhiteSpace(normalizedPath))
        {
            try
            {
                var resolved = await _api.ResolveCategoryAsync(normalizedPath, normalizedRef, ct).ConfigureAwait(false);
                if (resolved.ValueKind == JsonValueKind.Object
                    && resolved.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array)
                {
                    var item = items.EnumerateArray().FirstOrDefault();
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var resolvedPath = GetStringArg(item, "categoryPath");
                        var resolvedRef = GetStringArg(item, "categoryRef");
                        if (!string.IsNullOrWhiteSpace(resolvedPath) || !string.IsNullOrWhiteSpace(resolvedRef))
                        {
                            return (string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath.Trim(),
                                    string.IsNullOrWhiteSpace(resolvedRef) ? null : resolvedRef.Trim());
                        }
                    }
                }
            }
            catch
            {
                // Keep the current local fallback behavior if the backend contract is not available yet.
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedRef) || !string.IsNullOrWhiteSpace(normalizedPath))
        {
            var snapshot = ResolveCategorySnapshotFromReference(normalizedRef, normalizedPath);
            if (snapshot is not null)
            {
                var resolvedPath = !string.IsNullOrWhiteSpace(snapshot.CategoryPath) ? snapshot.CategoryPath : normalizedPath;
                var resolvedRef = !string.IsNullOrWhiteSpace(snapshot.CategoryRef) ? snapshot.CategoryRef : normalizedRef;
                return (string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath,
                        string.IsNullOrWhiteSpace(resolvedRef) ? null : resolvedRef);
            }
        }

        return (string.IsNullOrWhiteSpace(normalizedPath) ? null : normalizedPath,
                string.IsNullOrWhiteSpace(normalizedRef) ? null : normalizedRef);
    }

    private void RememberFocusedDocument(ResolvedDocRef resolved)
    {
        _mem.LastFocusedDocument = new ToolMemory.DocumentItem
        {
            DocId = resolved.DocId,
            DocPath = resolved.DocPath,
            DocName = resolved.DocName,
            Category = resolved.Category ?? string.Empty,
            CategoryRef = resolved.CategoryRef,
            CategoryPath = resolved.CategoryPath ?? string.Empty,
            PdfRef = _mem.LastFocusedDocument?.PdfRef ?? string.Empty,
            SourceHash = resolved.SourceHash,
            DocLanguage = resolved.DocLanguage,
            ProfileLanguage = resolved.ProfileLanguage,
            Pages = resolved.Pages
        };

        _mem.LastRequestedDocumentRef = !string.IsNullOrWhiteSpace(resolved.DocPath)
            ? resolved.DocPath
            : !string.IsNullOrWhiteSpace(resolved.DocId)
                ? resolved.DocId
                : resolved.DocName;

        _mem.PromoteDocumentsToWorkspace(new[] { _mem.LastFocusedDocument });
    }

    private static ResolvedDocRef BuildResolvedDocRefFromDocumentItem(ToolMemory.DocumentItem d)
        => new(
            d.DocId,
            d.DocPath,
            d.DocName,
            d.Category,
            d.CategoryPath,
            d.Pages,
            d.CategoryRef,
            d.SourceHash,
            d.DocLanguage,
            d.ProfileLanguage);

    private IEnumerable<ToolMemory.DocumentItem> EnumerateKnownDocuments()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_mem.LastFocusedDocument is not null)
        {
            var key = !string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocId)
                ? _mem.LastFocusedDocument.DocId
                : _mem.LastFocusedDocument.DocPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return _mem.LastFocusedDocument;
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            var key = !string.IsNullOrWhiteSpace(doc.DocId) ? doc.DocId : doc.DocPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return doc;
        }

        foreach (var doc in _mem.PdfMap.Values)
        {
            var key = !string.IsNullOrWhiteSpace(doc.DocId) ? doc.DocId : doc.DocPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return doc;
        }

        foreach (var doc in _mem.WorkspaceKnownDocuments)
        {
            var key = !string.IsNullOrWhiteSpace(doc.DocId) ? doc.DocId : doc.DocPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return doc;
        }
    }

    private IEnumerable<ToolMemory.CategorySnapshot> EnumerateKnownCategories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_mem.LastResolvedCategory is not null)
        {
            var key = !string.IsNullOrWhiteSpace(_mem.LastResolvedCategory.CategoryRef)
                ? _mem.LastResolvedCategory.CategoryRef
                : _mem.LastResolvedCategory.CategoryPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return _mem.LastResolvedCategory;
        }

        if (_mem.LastPresentedCategories is not null)
        {
            foreach (var category in _mem.LastPresentedCategories)
            {
                var key = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.CategoryPath;
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    yield return category;
            }
        }

        if (_mem.CatalogSnapshotCache?.Categories is { Count: > 0 })
        {
            foreach (var category in _mem.CatalogSnapshotCache.Categories)
            {
                var key = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.CategoryPath;
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    yield return category;
            }
        }
    }

    private bool LooksLikeKnownCategoryReference(string? value)
    {
        var normalized = NormalizeDocumentLookupText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var category in EnumerateKnownCategories())
        {
            if (NormalizeDocumentLookupText(category.CategoryRef) == normalized
                || NormalizeDocumentLookupText(category.DisplayName) == normalized
                || NormalizeDocumentLookupText(category.CategoryPath) == normalized
                || NormalizeDocumentLookupText(category.Ordinal.ToString()) == normalized)
            {
                return true;
            }

            foreach (var alias in category.Aliases)
            {
                if (NormalizeDocumentLookupText(alias) == normalized)
                    return true;
            }
        }

        return false;
    }


    private static string NormalizeDocumentLookupText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var s = value.Trim();
        s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
        s = Regex.Replace(s, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");
        s = s.Replace('_', ' ').Replace('-', ' ').Replace('/', ' ').Replace('\\', ' ');
        s = Regex.Replace(s, @"(?i)\.pdf\b", " ");
        s = StripDiacritics(s);
        s = Regex.Replace(s, @"[^a-z0-9 ]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string NormalizeDocumentLookupCompact(string? value)
        => Regex.Replace(NormalizeDocumentLookupText(value), @"\s+", string.Empty);

    private static int ScoreDocumentMatch(string query, string? docName, string? docPath)
    {
        var normalizedQuery = NormalizeDocumentLookupText(query);
        var compactQuery = NormalizeDocumentLookupCompact(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery) && string.IsNullOrWhiteSpace(compactQuery))
            return 0;

        var names = new[]
        {
            docName ?? string.Empty,
            docPath ?? string.Empty,
            string.IsNullOrWhiteSpace(docPath) ? string.Empty : Path.GetFileName(docPath)
        };

        var best = 0;
        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var raw in names)
        {
            var normalizedCandidate = NormalizeDocumentLookupText(raw);
            var compactCandidate = NormalizeDocumentLookupCompact(raw);
            if (string.IsNullOrWhiteSpace(normalizedCandidate) && string.IsNullOrWhiteSpace(compactCandidate))
                continue;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(compactQuery) && compactCandidate == compactQuery)
                score += 300;
            else if (!string.IsNullOrWhiteSpace(normalizedQuery) && normalizedCandidate == normalizedQuery)
                score += 280;
            else if (!string.IsNullOrWhiteSpace(compactQuery) && compactCandidate.Contains(compactQuery, StringComparison.Ordinal))
                score += 220;
            else if (!string.IsNullOrWhiteSpace(normalizedQuery) && normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
                score += 200;

            var matchedTokens = 0;
            foreach (var token in queryTokens)
            {
                if (normalizedCandidate.Contains(token, StringComparison.Ordinal)
                    || compactCandidate.Contains(token, StringComparison.Ordinal))
                {
                    matchedTokens++;
                    score += token.Length >= 4 ? 28 : 16;
                }
            }

            if (queryTokens.Length > 0 && matchedTokens == queryTokens.Length)
                score += 60;

            if (Path.GetFileName(raw ?? string.Empty).Equals(docName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                score += 4;

            best = Math.Max(best, score);
        }

        return best;
    }

    private ResolvedDocRef? TryResolveKnownDocumentByFuzzyReference(string docRef)
    {
        if (!QueryLooksSpecificEnoughForFuzzyResolution(docRef))
            return null;

        ToolMemory.DocumentItem? best = null;
        var bestScore = 0;
        var secondScore = 0;

        foreach (var doc in EnumerateKnownDocuments())
        {
            var score = ScoreDocumentMatch(docRef, doc.DocName, doc.DocPath);
            if (score <= 0)
                continue;

            if (score > bestScore)
            {
                secondScore = bestScore;
                best = doc;
                bestScore = score;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (best is null || bestScore < 140)
            return null;

        if (secondScore > 0 && bestScore - secondScore < 40)
            return null;

        return BuildResolvedDocRefFromDocumentItem(best);
    }

    private static bool QueryLooksSpecificEnoughForFuzzyResolution(string? query)
    {
        var raw = (query ?? string.Empty).Trim();
        if (raw.Length == 0)
            return false;

        if (Guid.TryParse(raw, out _))
            return true;

        if (raw.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || raw.Contains('/', StringComparison.Ordinal)
            || raw.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = NormalizeDocumentLookupText(raw)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length >= 2)
            return true;

        return tokens.Length == 1 && Regex.IsMatch(raw, @"\d", RegexOptions.CultureInvariant);
    }

    private ResolvedDocRef? TryResolveKnownDocumentByExactReference(string docRef)
    {
        var s = (docRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        foreach (var d in EnumerateKnownDocuments())
        {
            if (string.Equals(d.DocId, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocName, s, StringComparison.OrdinalIgnoreCase)
                || IsExactDocumentReferenceMatch(s, d.DocName, d.DocPath))
            {
                return BuildResolvedDocRefFromDocumentItem(d);
            }
        }

        return null;
    }

    private static bool IsExactDocumentReferenceMatch(string query, string? docName, string? docPath)
    {
        var normalizedQuery = NormalizeDocumentLookupText(query);
        var compactQuery = NormalizeDocumentLookupCompact(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery) && string.IsNullOrWhiteSpace(compactQuery))
            return false;

        static IEnumerable<string> BuildCandidates(string? docNameValue, string? docPathValue)
        {
            if (!string.IsNullOrWhiteSpace(docNameValue))
                yield return docNameValue!;
            if (!string.IsNullOrWhiteSpace(docPathValue))
                yield return docPathValue!;
            var fileName = Path.GetFileName(docPathValue ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(fileName))
                yield return fileName;
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(docPathValue ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                yield return fileNameWithoutExtension;
            var docNameWithoutExtension = Path.GetFileNameWithoutExtension(docNameValue ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(docNameWithoutExtension))
                yield return docNameWithoutExtension;
        }

        foreach (var candidate in BuildCandidates(docName, docPath))
        {
            var normalizedCandidate = NormalizeDocumentLookupText(candidate);
            var compactCandidate = NormalizeDocumentLookupCompact(candidate);
            if ((!string.IsNullOrWhiteSpace(compactQuery) && compactCandidate == compactQuery)
                || (!string.IsNullOrWhiteSpace(normalizedQuery) && normalizedCandidate == normalizedQuery))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CandidateMatchesDocumentIdentity(string query, string? docName, string? docPath)
    {
        var normalizedQuery = NormalizeDocumentLookupText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length == 0)
            return false;

        var docNameNormalized = NormalizeDocumentLookupText(docName);
        var fileNameNormalized = NormalizeDocumentLookupText(Path.GetFileName(docPath ?? string.Empty));
        var fileNameWithoutExtensionNormalized = NormalizeDocumentLookupText(Path.GetFileNameWithoutExtension(docPath ?? string.Empty));

        static bool AllTokensIn(string[] tokens, string candidate)
            => !string.IsNullOrWhiteSpace(candidate) && tokens.All(token => candidate.Contains(token, StringComparison.Ordinal));

        return AllTokensIn(tokens, docNameNormalized)
            || AllTokensIn(tokens, fileNameNormalized)
            || AllTokensIn(tokens, fileNameWithoutExtensionNormalized);
    }

    private sealed class ExplicitDocumentResolution
    {
        public bool IsResolved { get; init; }
        public bool IsAmbiguous { get; init; }
        public bool IsCategoryReference { get; init; }
        public ResolvedDocRef? Document { get; init; }
    }

    private async Task<ExplicitDocumentResolution> ResolveExplicitDocumentReferenceStrictAsync(string docRef, CancellationToken ct)
    {
        var s = (docRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return new ExplicitDocumentResolution();

        _mem.LastRequestedDocumentRef = s;

        if (_mem.PdfMap.TryGetValue(s, out var mapped) && mapped is not null)
        {
            var resolved = BuildResolvedDocRefFromDocumentItem(mapped);
            RememberFocusedDocument(resolved);
            return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
        }

        var exactKnown = TryResolveKnownDocumentByExactReference(s);
        if (exactKnown is not null)
        {
            if (!LooksLikeReindexableDocumentPath(exactKnown.DocPath))
                return new ExplicitDocumentResolution { IsCategoryReference = true };

            RememberFocusedDocument(exactKnown);
            return new ExplicitDocumentResolution { IsResolved = true, Document = exactKnown };
        }

        if (LooksLikeKnownCategoryReference(s))
            return new ExplicitDocumentResolution { IsCategoryReference = true };

        if (Guid.TryParse(s, out _))
        {
            try
            {
                var doc = await _api.DocumentsGetAsync(s, ct).ConfigureAwait(false);
                var resolved = TryBuildResolvedDocRefFromDocumentJson(doc, s);
                if (resolved is not null)
                {
                    if (!LooksLikeReindexableDocumentPath(resolved.DocPath))
                        return new ExplicitDocumentResolution { IsCategoryReference = true };

                    RememberFocusedDocument(resolved);
                    return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
                }
            }
            catch
            {
                // Ignore exact get failures and continue with exact-search resolution only.
            }
        }

        var exactMatches = new Dictionary<string, ResolvedDocRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildDocumentSearchVariants(s).Take(5))
        {
            JsonElement search;
            try
            {
                search = await _api.DocumentsSearchAsync(query, categoryPath: null, categoryRef: null, limit: 20, offset: 0, ct: ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (!search.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var it in items.EnumerateArray())
            {
                var resolved = TryBuildResolvedDocRefFromSearchItem(it);
                if (resolved is null)
                    continue;

                if (!IsExactDocumentReferenceMatch(s, resolved.DocName, resolved.DocPath)
                    && !string.Equals(resolved.DocId, s, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(resolved.DocPath, s, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!LooksLikeReindexableDocumentPath(resolved.DocPath))
                    continue;

                exactMatches[resolved.DocId] = resolved;
            }
        }

        if (exactMatches.Count == 1)
        {
            var resolved = exactMatches.Values.First();
            RememberFocusedDocument(resolved);
            return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
        }

        if (exactMatches.Count > 1)
            return new ExplicitDocumentResolution { IsAmbiguous = true };

        return new ExplicitDocumentResolution();
    }

    private ResolvedDocRef? TryBuildResolvedDocRefFromDocumentJson(JsonElement doc, string fallbackDocId)
    {
        var gotId = TryGetString(doc, "DocId") ?? TryGetString(doc, "docId") ?? fallbackDocId;
        var path = TryGetString(doc, "DocPath") ?? TryGetString(doc, "docPath") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = TryGetString(doc, "DocName") ?? TryGetString(doc, "docName") ?? path;
        var category = TryGetString(doc, "Category") ?? TryGetString(doc, "category");
        var categoryRef = TryGetString(doc, "CategoryRef") ?? TryGetString(doc, "categoryRef");
        var categoryPath = TryGetString(doc, "CategoryPath") ?? TryGetString(doc, "categoryPath") ?? GuessCategoryPath(path);
        var pages = TryGetInt(doc, "PageCount") ?? TryGetInt(doc, "pageCount") ?? TryGetInt(doc, "pages");
        return new ResolvedDocRef(
            gotId,
            path,
            name,
            category,
            categoryPath,
            pages,
            categoryRef,
            NullIfWhiteSpace(TryGetString(doc, "SourceHash") ?? TryGetString(doc, "sourceHash")),
            NullIfWhiteSpace(TryGetDocumentLanguage(doc)),
            NullIfWhiteSpace(TryGetString(doc, "ProfileLanguage") ?? TryGetString(doc, "profileLanguage")));
    }

    private ResolvedDocRef? TryBuildResolvedDocRefFromSearchItem(JsonElement item)
    {
        var docId = TryGetString(item, "docId") ?? TryGetString(item, "DocId") ?? string.Empty;
        var docPath = TryGetString(item, "docPath") ?? TryGetString(item, "DocPath") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(docPath))
            return null;

        var docName = TryGetString(item, "docName") ?? TryGetString(item, "DocName") ?? docPath;
        var category = TryGetString(item, "category") ?? TryGetString(item, "Category");
        var categoryRef = TryGetString(item, "categoryRef") ?? TryGetString(item, "CategoryRef");
        var categoryPath = TryGetString(item, "categoryPath") ?? TryGetString(item, "CategoryPath") ?? GuessCategoryPath(docPath);
        var pages = TryGetInt(item, "pages") ?? TryGetInt(item, "PageCount") ?? TryGetInt(item, "pageCount");
        return new ResolvedDocRef(
            docId,
            docPath,
            docName,
            category,
            categoryPath,
            pages,
            categoryRef,
            NullIfWhiteSpace(TryGetString(item, "sourceHash") ?? TryGetString(item, "SourceHash")),
            NullIfWhiteSpace(TryGetDocumentLanguage(item)),
            NullIfWhiteSpace(TryGetString(item, "profileLanguage") ?? TryGetString(item, "ProfileLanguage")));
    }

    private async Task<ExplicitDocumentResolution> ResolveExplicitDocumentReferenceAsync(string docRef, CancellationToken ct)
    {
        var s = (docRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return new ExplicitDocumentResolution();

        _mem.LastRequestedDocumentRef = s;

        if (_mem.PdfMap.TryGetValue(s, out var mapped) && mapped is not null)
        {
            var resolved = BuildResolvedDocRefFromDocumentItem(mapped);
            RememberFocusedDocument(resolved);
            return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
        }

        var exactKnown = TryResolveKnownDocumentByExactReference(s);
        if (exactKnown is not null)
        {
            RememberFocusedDocument(exactKnown);
            return new ExplicitDocumentResolution { IsResolved = true, Document = exactKnown };
        }

        if (LooksLikeKnownCategoryReference(s))
            return new ExplicitDocumentResolution { IsCategoryReference = true };

        if (Guid.TryParse(s, out _))
        {
            var doc = await _api.DocumentsGetAsync(s, ct).ConfigureAwait(false);
            var resolved = TryBuildResolvedDocRefFromDocumentJson(doc, s);
            if (resolved is not null)
            {
                RememberFocusedDocument(resolved);
                return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
            }
        }

        var allowSearchResolution = LooksLikeSpecificDocumentReferenceQuery(s) || QueryLooksSpecificEnoughForFuzzyResolution(s);
        if (!allowSearchResolution)
            return new ExplicitDocumentResolution();

        var candidateMap = new Dictionary<string, (string docId, string docPath, string docName, string? category, string? categoryRef, string? categoryPath, int? pages, string? sourceHash, string? docLanguage, string? profileLanguage, int score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildDocumentSearchVariants(s).Take(5))
        {
            var search = await _api.DocumentsSearchAsync(query, categoryPath: null, categoryRef: null, limit: 20, offset: 0, ct: ct).ConfigureAwait(false);
            if (!search.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var it in items.EnumerateArray())
            {
                var docId = TryGetString(it, "docId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? docPath;
                if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(docPath))
                    continue;

                if (!CandidateMatchesDocumentIdentity(s, docName, docPath))
                    continue;

                var score = ScoreDocumentMatch(s, docName, docPath);
                if (!candidateMap.TryGetValue(docId, out var existing) || score > existing.score)
                {
                    candidateMap[docId] = (
                        docId,
                        docPath,
                        docName,
                        TryGetString(it, "category"),
                        TryGetString(it, "categoryRef"),
                        TryGetString(it, "categoryPath") ?? GuessCategoryPath(docPath),
                        TryGetInt(it, "pages"),
                        NullIfWhiteSpace(TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash")),
                        NullIfWhiteSpace(TryGetDocumentLanguage(it)),
                        NullIfWhiteSpace(TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage")),
                        score);
                }
            }
        }

        if (candidateMap.Count == 0)
            return new ExplicitDocumentResolution();

        var exactMatches = candidateMap.Values
            .Where(x => IsExactDocumentReferenceMatch(s, x.docName, x.docPath))
            .OrderByDescending(x => x.score)
            .ToList();

        if (exactMatches.Count == 1)
        {
            var winner = exactMatches[0];
            var resolved = new ResolvedDocRef(winner.docId, winner.docPath, winner.docName, winner.category, winner.categoryPath, winner.pages, winner.categoryRef, winner.sourceHash, winner.docLanguage, winner.profileLanguage);
            RememberFocusedDocument(resolved);
            return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
        }

        if (exactMatches.Count > 1)
            return new ExplicitDocumentResolution { IsAmbiguous = true };

        if (!QueryLooksSpecificEnoughForFuzzyResolution(s))
            return new ExplicitDocumentResolution { IsAmbiguous = true };

        var ranked = candidateMap.Values
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.docName.Length)
            .ToList();

        var best = ranked[0];
        var secondScore = ranked.Count > 1 ? ranked[1].score : 0;
        if (best.score >= 220 && (secondScore == 0 || best.score - secondScore >= 60))
        {
            var resolved = new ResolvedDocRef(best.docId, best.docPath, best.docName, best.category, best.categoryPath, best.pages, best.categoryRef, best.sourceHash, best.docLanguage, best.profileLanguage);
            RememberFocusedDocument(resolved);
            return new ExplicitDocumentResolution { IsResolved = true, Document = resolved };
        }

        return new ExplicitDocumentResolution { IsAmbiguous = true };
    }

    private static IReadOnlyList<string> BuildDocumentSearchVariants(string docRef)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(v))
                return;
            if (!variants.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                variants.Add(v);
        }

        var raw = (docRef ?? string.Empty).Trim();
        Add(raw);
        Add(Regex.Replace(raw, @"(?i)\.pdf\b", string.Empty).Trim());
        Add(Regex.Replace(raw, @"[_\-/]+", " ").Trim());
        Add(Regex.Replace(raw, @"(?<=[a-z])(?=[A-Z])", " ").Trim());

        var normalized = NormalizeDocumentLookupText(raw);
        Add(normalized);

        return variants;
    }

    private ToolMemory.SourceRef? ResolveSourceRef(string rawRef)
    {
        var s = (rawRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var explicitSourceOrdinal = Regex.Match(s, @"(?i)^\s*(?:source|citation|reference|r[eÃ©]f(?:[eÃ©]rence)?\.?)\s*(?:n[Â°o]\s*)?#?\s*0*(?<n>\d{1,4})\s*$");
        if (explicitSourceOrdinal.Success
            && int.TryParse(explicitSourceOrdinal.Groups["n"].Value, out var sourceOrdinal)
            && sourceOrdinal > 0
            && _mem.LastSourcesUsed is { Count: > 0 }
            && sourceOrdinal <= _mem.LastSourcesUsed.Count)
        {
            return _mem.LastSourcesUsed[sourceOrdinal - 1];
        }

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            var key = $"PDF{nPdf:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        var mNum = Regex.Match(s, @"\b(?<n>\d{1,4})\b");
        if (mNum.Success && int.TryParse(mNum.Groups["n"].Value, out var n) && n > 0)
        {
            if (_mem.LastListedDocuments is { Count: > 0 } && n <= _mem.LastListedDocuments.Count)
                return BuildSourceFromDocument(_mem.LastListedDocuments[n - 1]);

            var key = $"PDF{n:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        foreach (var used in _mem.LastSourcesUsed)
        {
            if (string.Equals(used.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.Label, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.DocId, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.SourceHash, s, StringComparison.OrdinalIgnoreCase))
            {
                return used;
            }
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            if (string.Equals(doc.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(doc);
            }
        }

        foreach (var kv in _mem.PdfMap.Values)
        {
            if (string.Equals(kv.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(kv);
            }
        }

        return null;
    }

    private ToolMemory.SourceRef BuildSourceFromDocument(ToolMemory.DocumentItem doc)
    {
        var pageStart = 1;
        var pageEnd = 1;
        var used = _mem.LastSourcesUsed?
            .FirstOrDefault(s => string.Equals((s.DocPath ?? string.Empty).Replace('\\', '/'), (doc.DocPath ?? string.Empty).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (used is not null)
        {
            pageStart = Math.Max(1, used.PageStart);
            pageEnd = Math.Max(pageStart, used.PageEnd);
        }

        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(doc.DocId) ?? used?.DocId,
            DocPath = doc.DocPath,
            DocName = NullIfWhiteSpace(doc.DocName) ?? used?.DocName,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = string.IsNullOrWhiteSpace(doc.DocName) ? doc.DocPath : doc.DocName,
            SourceHash = NullIfWhiteSpace(used?.SourceHash) ?? NullIfWhiteSpace(doc.SourceHash),
            DocLanguage = NullIfWhiteSpace(used?.DocLanguage) ?? NullIfWhiteSpace(doc.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(used?.ProfileLanguage) ?? NullIfWhiteSpace(doc.ProfileLanguage),
            Category = NullIfWhiteSpace(used?.Category) ?? NullIfWhiteSpace(doc.Category),
            CategoryRef = NullIfWhiteSpace(used?.CategoryRef) ?? NullIfWhiteSpace(doc.CategoryRef),
            CategoryPath = NullIfWhiteSpace(used?.CategoryPath) ?? NullIfWhiteSpace(doc.CategoryPath) ?? NullIfWhiteSpace(doc.Category),
            ChunkId = used?.ChunkId,
            SectionTitle = used?.SectionTitle,
            HeadingPath = used?.HeadingPath,
            PrevChunkId = used?.PrevChunkId,
            NextChunkId = used?.NextChunkId,
            SameSectionChunkId = used?.SameSectionChunkId,
            OriginalChunkType = used?.OriginalChunkType,
            OffsetStart = used?.OffsetStart,
            OffsetEnd = used?.OffsetEnd,
            ExtractionSource = used?.ExtractionSource,
            DocumentQualityStatus = used?.DocumentQualityStatus,
            PageQualityStatus = used?.PageQualityStatus,
            TextStatus = used?.TextStatus,
            ChunkTextStatus = used?.ChunkTextStatus,
            ChunkTextSparse = used?.ChunkTextSparse,
            ChunkOcrCandidate = used?.ChunkOcrCandidate,
            QualityStatus = used?.QualityStatus,
            ExtractionConfidence = used?.ExtractionConfidence,
            DocumentExtractionConfidence = used?.DocumentExtractionConfidence,
            PageExtractionConfidence = used?.PageExtractionConfidence,
            ManualReviewRecommended = used?.ManualReviewRecommended ?? false,
            DocumentManualReviewRecommended = used?.DocumentManualReviewRecommended ?? false,
            PageManualReviewRecommended = used?.PageManualReviewRecommended ?? false,
            OcrAttempted = used?.OcrAttempted ?? false,
            OcrApplied = used?.OcrApplied ?? false,
            OcrRecommended = used?.OcrRecommended ?? false,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(used?.ExtractionDiagnosticSummary),
            QualitySignals = used?.QualitySignals.ToList() ?? new List<string>(),
            ChunkQualitySignals = used?.ChunkQualitySignals.ToList() ?? new List<string>(),
            MatchedContentCards = used?.MatchedContentCards.ToList() ?? new List<ToolMemory.SourceContentCardRef>(),
            ProfileSignals = CloneSourceProfileSignalsRef(used?.ProfileSignals),
            SelectionHintEvidenceRole = used?.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = used?.SelectionHintActionabilityScore,
            SelectionHintSupportScore = used?.SelectionHintSupportScore,
            SelectionHintFragmentScore = used?.SelectionHintFragmentScore,
            SelectionHintNavigationScore = used?.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = used?.SelectionHintQualityPenalty,
            ContentRole = used?.ContentRole,
            NavigationReason = used?.NavigationReason,
            RetrievalNavigationScore = used?.RetrievalNavigationScore,
            ContentDensityScore = used?.ContentDensityScore
        };
    }

    private async Task<ResolvedDocRef?> ResolveDocRefAsync(string docRef, CancellationToken ct)
    {
        var s = (docRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        _mem.LastRequestedDocumentRef = s;

        if (_mem.PdfMap.TryGetValue(s, out var mapped) && mapped is not null)
        {
            var resolved = BuildResolvedDocRefFromDocumentItem(mapped);
            RememberFocusedDocument(resolved);
            return resolved;
        }

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            var key = $"PDF{nPdf:00}";
            if (_mem.PdfMap.TryGetValue(key, out var pdfDoc) && pdfDoc is not null)
            {
                var resolved = BuildResolvedDocRefFromDocumentItem(pdfDoc);
                RememberFocusedDocument(resolved);
                return resolved;
            }
        }

        if (int.TryParse(s, out var idx) && idx > 0 && _mem.LastListedDocuments is { Count: > 0 } && idx <= _mem.LastListedDocuments.Count)
        {
            var d = _mem.LastListedDocuments[idx - 1];
            var resolved = BuildResolvedDocRefFromDocumentItem(d);
            RememberFocusedDocument(resolved);
            return resolved;
        }

        foreach (var d in EnumerateKnownDocuments())
        {
            if (string.Equals(d.DocId, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocName, s, StringComparison.OrdinalIgnoreCase)
                || IsExactDocumentReferenceMatch(s, d.DocName, d.DocPath))
            {
                var resolved = BuildResolvedDocRefFromDocumentItem(d);
                RememberFocusedDocument(resolved);
                return resolved;
            }
        }

        if (LooksLikeKnownCategoryReference(s))
            return null;

        var fuzzyKnown = TryResolveKnownDocumentByFuzzyReference(s);
        if (fuzzyKnown is not null)
        {
            RememberFocusedDocument(fuzzyKnown);
            return fuzzyKnown;
        }

        if (Guid.TryParse(s, out _))
        {
            var doc = await _api.DocumentsGetAsync(s, ct).ConfigureAwait(false);
            var resolved = TryBuildResolvedDocRefFromDocumentJson(doc, s);
            if (resolved is not null)
            {
                RememberFocusedDocument(resolved);
                return resolved;
            }
        }

        var allowSearchResolution = LooksLikeSpecificDocumentReferenceQuery(s) || QueryLooksSpecificEnoughForFuzzyResolution(s);
        if (!allowSearchResolution)
            return null;

        var candidateMap = new Dictionary<string, (string docId, string docPath, string docName, string? category, string? categoryRef, string? categoryPath, int? pages, string? sourceHash, string? docLanguage, string? profileLanguage, int score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildDocumentSearchVariants(s).Take(5))
        {
            var search = await _api.DocumentsSearchAsync(query, categoryPath: null, categoryRef: null, limit: 20, offset: 0, ct: ct).ConfigureAwait(false);
            if (!search.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var it in items.EnumerateArray())
            {
                var docId = TryGetString(it, "docId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? docPath;
                if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(docPath))
                    continue;

                if (!IsExactDocumentReferenceMatch(s, docName, docPath)
                    && !CandidateMatchesDocumentIdentity(s, docName, docPath))
                {
                    continue;
                }

                var score = ScoreDocumentMatch(s, docName, docPath);
                var key = docId;
                if (!candidateMap.TryGetValue(key, out var existing) || score > existing.score)
                {
                    candidateMap[key] = (
                        docId,
                        docPath,
                        docName,
                        TryGetString(it, "category"),
                        TryGetString(it, "categoryRef"),
                        TryGetString(it, "categoryPath") ?? GuessCategoryPath(docPath),
                        TryGetInt(it, "pages"),
                        NullIfWhiteSpace(TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash")),
                        NullIfWhiteSpace(TryGetDocumentLanguage(it)),
                        NullIfWhiteSpace(TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage")),
                        score);
                }
            }
        }

        var best = candidateMap.Values
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.docName.Length)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(best.docId) && best.score >= 90)
        {
            var resolved = new ResolvedDocRef(best.docId, best.docPath, best.docName, best.category, best.categoryPath, best.pages, best.categoryRef, best.sourceHash, best.docLanguage, best.profileLanguage);
            RememberFocusedDocument(resolved);
            return resolved;
        }

        return null;
    }

    private static string GuessCategoryPath(string? docPath)
    {
        var s = (docPath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        var idx = s.LastIndexOf('/');
        return idx > 0 ? s.Substring(0, idx) : string.Empty;
    }

    private string? GetPreferredDocRef(JsonElement args)
        => GetStringArg(args, "docRef")
           ?? GetStringArg(args, "docId")
           ?? GetStringArg(args, "ref")
           ?? _mem.LastRequestedDocumentRef
           ?? _mem.LastFocusedDocument?.DocId
           ?? _mem.LastFocusedDocument?.DocPath
           ?? _mem.LastFocusedDocument?.DocName;

    private static string? GetStringArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetIntArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? GetBoolArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static List<string>? GetStringArrayArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }
            return list;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return null;
    }
}
