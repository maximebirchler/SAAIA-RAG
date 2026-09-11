using System.Diagnostics;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>
/// Resolves exact document identities, unique extensionless identity prefixes,
/// and unique formal numeric designators from complete current-catalog
/// observations. It deliberately performs no fuzzy positive selection.
/// </summary>
public sealed partial class SourceBackedNamedDocumentResolver
    : ISourceBackedNamedDocumentResolver
{
    internal const int CatalogPageSize = 100;
    internal const int MaximumCatalogPages = 60;
    internal const int MaximumReturnedCandidates = 20;

    private readonly ISourceBackedNamedDocumentCatalogClient _catalog;
    private readonly ISourceBackedNamedDocumentIdentityHydrator?
        _identityHydrator;

    public SourceBackedNamedDocumentResolver(
        ISourceBackedNamedDocumentCatalogClient catalog)
        : this(
            catalog,
            catalog as ISourceBackedNamedDocumentIdentityHydrator)
    {
    }

    public SourceBackedNamedDocumentResolver(
        ISourceBackedNamedDocumentCatalogClient catalog,
        ISourceBackedNamedDocumentIdentityHydrator? identityHydrator)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _identityHydrator = identityHydrator;
    }

    public async Task<SourceBackedDocumentResolutionObservation> ResolveAsync(
        string requestedReference,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var requested = CleanReference(requestedReference);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Inconclusive(
                requestedReference,
                "invalid_requested_reference",
                pagesObserved: 0,
                itemsObserved: 0,
                reachedSafetyLimit: false,
                timer.ElapsedMilliseconds);
        }

        var exactMatches = new Dictionary<
            string,
            SourceBackedDocumentResolutionCandidate>(StringComparer.OrdinalIgnoreCase);
        var abbreviatedMatches = new Dictionary<
            string,
            SourceBackedDocumentResolutionCandidate>(StringComparer.OrdinalIgnoreCase);
        var formalDesignatorMatches = new Dictionary<
            string,
            SourceBackedDocumentResolutionCandidate>(StringComparer.OrdinalIgnoreCase);
        var pagesObserved = 0;
        var itemsObserved = 0;
        try
        {
            foreach (var query in BuildSearchQueries(requested))
            {
                var offset = 0;
                while (true)
                {
                    if (pagesObserved >= MaximumCatalogPages)
                    {
                        return Inconclusive(
                            requested,
                            "catalog_page_safety_limit_reached",
                            pagesObserved,
                            itemsObserved,
                            reachedSafetyLimit: true,
                            timer.ElapsedMilliseconds,
                            ObservedMatches(exactMatches, abbreviatedMatches,
                                formalDesignatorMatches));
                    }

                    var page = await _catalog.SearchAsync(
                            query,
                            CatalogPageSize,
                            offset,
                            ct)
                        .ConfigureAwait(false);
                    pagesObserved++;
                    itemsObserved += page.Items.Count;
                    if (!IsPaginationConsistent(page, offset))
                    {
                        return Inconclusive(
                            requested,
                            "catalog_pagination_incomplete",
                            pagesObserved,
                            itemsObserved,
                            reachedSafetyLimit: false,
                            timer.ElapsedMilliseconds,
                            ObservedMatches(exactMatches, abbreviatedMatches,
                                formalDesignatorMatches));
                    }

                    foreach (var candidate in page.Items)
                    {
                        var key = CanonicalIdentityKey(candidate);
                        if (IsExactMatch(requested, candidate))
                            exactMatches.TryAdd(key, candidate);
                        else if (IsAbbreviatedIdentityMatch(requested, candidate))
                            abbreviatedMatches.TryAdd(key, candidate);
                        else if (IsUniqueFormalDesignatorCandidate(
                                     requested,
                                     candidate))
                            formalDesignatorMatches.TryAdd(key, candidate);
                    }

                    if (page.EndOfList)
                        break;
                    if (page.Items.Count == 0)
                    {
                        return Inconclusive(
                            requested,
                            "catalog_pagination_did_not_advance",
                            pagesObserved,
                            itemsObserved,
                            reachedSafetyLimit: false,
                            timer.ElapsedMilliseconds,
                            ObservedMatches(exactMatches, abbreviatedMatches,
                                formalDesignatorMatches));
                    }

                    var nextOffset = checked(page.Offset + page.Items.Count);
                    if (nextOffset <= offset)
                    {
                        return Inconclusive(
                            requested,
                            "catalog_pagination_did_not_advance",
                            pagesObserved,
                            itemsObserved,
                            reachedSafetyLimit: false,
                            timer.ElapsedMilliseconds,
                            ObservedMatches(exactMatches, abbreviatedMatches,
                                formalDesignatorMatches));
                    }
                    offset = nextOffset;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceBackedCatalogObservationDisagreementException ex)
        {
            return Inconclusive(
                requested,
                "catalog_observations_disagree",
                pagesObserved + ex.PagesObserved,
                itemsObserved + ex.ItemsObserved,
                reachedSafetyLimit: false,
                timer.ElapsedMilliseconds,
                ex.Candidates,
                ex.GetType().Name);
        }
        catch (Exception ex)
        {
            return Inconclusive(
                requested,
                "catalog_observation_failed",
                pagesObserved,
                itemsObserved,
                reachedSafetyLimit: false,
                timer.ElapsedMilliseconds,
                ObservedMatches(exactMatches, abbreviatedMatches,
                    formalDesignatorMatches),
                ex.GetType().Name);
        }

        var matchedByExactIdentity = exactMatches.Count > 0;
        var matchedByAbbreviatedIdentity = !matchedByExactIdentity
                                           && abbreviatedMatches.Count > 0;
        var selectedMatches = matchedByExactIdentity
            ? exactMatches.Values
            : matchedByAbbreviatedIdentity
                ? abbreviatedMatches.Values
                : formalDesignatorMatches.Values;
        var allCandidates = selectedMatches
            .OrderBy(static candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.DocId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allCandidates.Length == 1 && _identityHydrator is not null)
        {
            try
            {
                var hydrated = await _identityHydrator.HydrateAsync(
                        allCandidates[0],
                        ct)
                    .ConfigureAwait(false);
                allCandidates = [hydrated];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SourceBackedCanonicalIdentityHydrationException ex)
            {
                return Inconclusive(
                    requested,
                    ex.ReasonCode,
                    pagesObserved,
                    itemsObserved,
                    reachedSafetyLimit: false,
                    timer.ElapsedMilliseconds,
                    candidates: null,
                    ex.GetType().Name);
            }
            catch (Exception ex)
            {
                return Inconclusive(
                    requested,
                    "canonical_identity_observation_failed",
                    pagesObserved,
                    itemsObserved,
                    reachedSafetyLimit: false,
                    timer.ElapsedMilliseconds,
                    candidates: null,
                    ex.GetType().Name);
            }
        }
        var status = allCandidates.Length switch
        {
            0 => SourceBackedDocumentResolutionStatus.NotFound,
            1 => SourceBackedDocumentResolutionStatus.Resolved,
            _ => SourceBackedDocumentResolutionStatus.Ambiguous
        };
        var reason = status switch
        {
            SourceBackedDocumentResolutionStatus.Resolved when matchedByExactIdentity
                => "resolved_exact_current_catalog",
            SourceBackedDocumentResolutionStatus.Resolved when matchedByAbbreviatedIdentity
                => "resolved_unique_identity_prefix_current_catalog",
            SourceBackedDocumentResolutionStatus.Resolved
                => "resolved_unique_formal_designator_current_catalog",
            SourceBackedDocumentResolutionStatus.NotFound
                => "no_exact_current_catalog_match",
            _ when matchedByExactIdentity
                => "multiple_exact_current_catalog_matches",
            _ when matchedByAbbreviatedIdentity
                => "multiple_identity_prefix_current_catalog_matches",
            _ => "multiple_formal_designator_current_catalog_matches"
        };
        return new SourceBackedDocumentResolutionObservation(
            requested,
            status,
            CatalogObservationComplete: true,
            allCandidates.Take(MaximumReturnedCandidates).ToArray(),
            reason,
            pagesObserved,
            itemsObserved,
            ReachedSafetyLimit: false,
            timer.ElapsedMilliseconds,
            ExactMatchCount: allCandidates.Length);
    }

    private static bool IsPaginationConsistent(
        SourceBackedDocumentCatalogPage page,
        int requestedOffset)
    {
        if (page.Offset != requestedOffset || page.Limit <= 0)
            return false;
        var observedEnd = page.Offset + page.Items.Count;
        if (page.Total is { } total)
        {
            if (observedEnd > total)
                return false;
            if (page.EndOfList != (observedEnd >= total))
                return false;
        }
        return true;
    }

    private static IReadOnlyList<string> BuildSearchQueries(string requested)
    {
        var fileName = ReadFileName(requested);
        var stem = RemoveExtension(fileName);
        return new[] { requested, fileName, stem }
            .Concat(ExtractFormalDesignators(requested))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsExactMatch(
        string requestedReference,
        SourceBackedDocumentResolutionCandidate candidate)
    {
        var requested = NormalizePath(CleanReference(requestedReference));
        if (string.IsNullOrWhiteSpace(requested))
            return false;
        if (string.Equals(
                requested,
                NormalizeIdentity(candidate.DocId),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidatePath = NormalizePath(candidate.DocPath);
        if (requested.Contains('/', StringComparison.Ordinal))
        {
            return string.Equals(
                requested,
                candidatePath,
                StringComparison.OrdinalIgnoreCase);
        }

        var requestedName = ReadFileName(requested);
        var candidateNames = new[]
        {
            ReadFileName(candidatePath),
            ReadFileName(NormalizePath(candidate.DocName))
        }.Where(static value => !string.IsNullOrWhiteSpace(value));
        if (HasExtension(requestedName))
        {
            return candidateNames.Any(candidateName => string.Equals(
                requestedName,
                candidateName,
                StringComparison.OrdinalIgnoreCase));
        }

        var requestedStem = RemoveExtension(requestedName);
        return candidateNames.Any(candidateName => string.Equals(
            requestedStem,
            RemoveExtension(candidateName),
            StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsAbbreviatedIdentityMatch(
        string requestedReference,
        SourceBackedDocumentResolutionCandidate candidate)
    {
        var requested = NormalizePath(CleanReference(requestedReference));
        if (string.IsNullOrWhiteSpace(requested)
            || requested.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var requestedName = ReadFileName(requested);
        if (HasExtension(requestedName))
            return false;

        var requestedStem = RemoveExtension(requestedName).Trim();
        if (requestedStem.Length == 0)
            return false;

        return new[]
            {
                ReadFileName(NormalizePath(candidate.DocPath)),
                ReadFileName(NormalizePath(candidate.DocName))
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(RemoveExtension)
            .Any(candidateStem => StartsWithIdentityBoundary(
                candidateStem,
                requestedStem));
    }

    private static bool StartsWithIdentityBoundary(
        string candidateStem,
        string requestedStem)
        => candidateStem.Length > requestedStem.Length
           && candidateStem.StartsWith(
               requestedStem,
               StringComparison.OrdinalIgnoreCase)
           && !char.IsLetterOrDigit(candidateStem[requestedStem.Length]);

    private static IEnumerable<SourceBackedDocumentResolutionCandidate>
        ObservedMatches(
            IReadOnlyDictionary<string, SourceBackedDocumentResolutionCandidate>
                exactMatches,
            IReadOnlyDictionary<string, SourceBackedDocumentResolutionCandidate>
                abbreviatedMatches,
            IReadOnlyDictionary<string, SourceBackedDocumentResolutionCandidate>
                formalDesignatorMatches)
        => exactMatches.Values
            .Concat(abbreviatedMatches.Values)
            .Concat(formalDesignatorMatches.Values);

    private static string CanonicalIdentityKey(
        SourceBackedDocumentResolutionCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.DocId)
            ? "id:" + NormalizeIdentity(candidate.DocId)
            : "path:" + NormalizePath(candidate.DocPath);

    private static SourceBackedDocumentResolutionObservation Inconclusive(
        string requestedReference,
        string reason,
        int pagesObserved,
        int itemsObserved,
        bool reachedSafetyLimit,
        long elapsedMilliseconds,
        IEnumerable<SourceBackedDocumentResolutionCandidate>? candidates = null,
        string? technicalError = null)
        => new(
            requestedReference,
            SourceBackedDocumentResolutionStatus.Inconclusive,
            CatalogObservationComplete: false,
            candidates?.ToArray()
            ?? Array.Empty<SourceBackedDocumentResolutionCandidate>(),
            reason,
            pagesObserved,
            itemsObserved,
            reachedSafetyLimit,
            elapsedMilliseconds,
            technicalError,
            ExactMatchCount: candidates?.Count());

    private static string CleanReference(string? value)
        => (value ?? string.Empty)
            .Trim()
            .Trim('"', '\'', '`')
            .Normalize(NormalizationForm.FormKC);

    private static string NormalizeIdentity(string? value)
        => CleanReference(value).Trim();

    private static string NormalizePath(string? value)
        => CleanReference(value)
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .TrimEnd('/');

    private static string ReadFileName(string? value)
    {
        var normalized = NormalizePath(value);
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static bool HasExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot > 0 && dot < value.Length - 1;
    }

    private static string RemoveExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot > 0 ? value[..dot] : value;
    }
}
