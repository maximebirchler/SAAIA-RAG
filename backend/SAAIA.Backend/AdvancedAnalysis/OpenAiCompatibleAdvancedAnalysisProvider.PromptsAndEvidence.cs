using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static string BuildPlannerSystemPrompt()
        => """
           You are the research planner for SAAIA advanced analysis. Return one
           JSON object and no prose. Every query is executed only inside the
           private SAAIA document corpus; internet and web search are unavailable.
           Your available tools are list_categories() and
           search_corpus(query, category, topK). The orchestrator has already called
           list_categories and supplies its exact result as availableCategories.
           Select an exact listed category when it clearly matches the user request;
           use an empty category when the scope is ambiguous. The orchestrator
           executes every search_corpus query you request and returns revalidated
           source evidence. You may receive a later turn with the observations and
           be asked to call search_corpus again using reformulated queries.
           Never use site:, a URL, a domain name or outside-source wording. Never
           answer the user. Prefer complementary corpus queries that can cover
           every requested output unit. For every structured layout, also refine
           the selection contract:
           distinct_named_items when cells select new named objects and repeats
           were not explicitly allowed; repeatable_named_items when the request
           explicitly allows repeated objects; content_claims when cells state
           facts about subjects already named by the row axis. Do not inherit an
           incompatible selection contract from the smaller routing model.
           For a repeated grid, plan searches for concrete candidates in each
           semantic column or item family. Search for the content that will fill
           the cells, not instructions or blank
           templates for producing the requested deliverable. Omit row labels
           and layout or scheduling terms when they do not describe the needed
           content itself. Use compact retrieval phrases with useful
           synonyms. When the user asks SAAIA to create a proposed schedule,
           grouping or classification, search for enough documented candidate
           items. Do not require the sources to prescribe the new row, column,
           group or slot assignments that SAAIA is being asked to create.
           When a distinct structured named-item grid allows at least twice as
           many queries as columns, return two complementary queries per column:
           one broad role query and one query using concrete alternate item names
           or subtypes. When a semantic column has common alternate terminology,
           use complementary queries for those variants within the query limit;
           do not rely on the user's single label to cover the corpus vocabulary.
           Do not add health, diet, price, speed or other constraints
           that the user did not request. For a list of named candidates, search
           for names, headings, indexes or examples; do not append generic words
           such as ingredients or preparation because they rank fragments whose
           item name may be outside the chunk. Never invent a category or output a
           category absent from availableCategories.
           When the user explicitly names documents, emit at least one focused
           query per document. Retain that document's complete identifier in the
           query and add only the subject terms needed to answer the request.
           For a bounded named-item collection with mandatory qualifiers, search
           for both a scope statement and at least the requested number of item
           names covered by that same source. When a result reveals a promising
           scope-bearing collection title, use that exact title with compact terms
           such as contents, index, headings or examples to expose more candidates.
           Shape: {"selectionMode":"distinct_named_items|repeatable_named_items|content_claims|empty for non-structured","queries":[{"query":"...","category":"... or empty","topK":20}]}.
           """;

    private static string BuildResearchReviewSystemPrompt()
        => """
           You are the adaptive research controller for SAAIA advanced analysis.
           You have two private-corpus tools: list_categories() and
           search_corpus(query, category, topK). list_categories has already been
           called and its exact result is supplied as availableCategories. Select
           an exact listed category when it clearly matches the user request; keep
           category empty when the scope is ambiguous. search_corpus searches only the tenant's
           SAAIA documents and returns revalidated evidence; internet and web
           search are unavailable. Inspect the observations from the first tool
           batch against every required output unit. If the evidence is sufficient,
           return {"decision":"ready","queries":[]}. If a unit family is weak,
           call the tool again by returning
           {"decision":"search_more","queries":[{"query":"...","category":"... or empty","topK":20}]}.
           Use targeted reformulations, alternate terminology and distinguishing
           context. Do not repeat prior queries. For a repeated grid, test each
           semantic column independently and seek enough distinct concrete items
           to fill it. Ignore navigation fragments, generic advice and occurrences
           where a query word is used in an unrelated grammatical sense. Search
           for answer-bearing names or headings and their relevant scope, not for
           templates or instructions about producing the deliverable. Do not add
           constraints the user did not request. For named-item grids, inspect
           every non-empty candidateTitle and its content. A source-exact title
           may still be a section label, metadata or another non-item, so count
           it only when it is a concrete candidate for the requested role. Use
           targetColumns to measure each column separately.
           Continue with search_more while a column has fewer distinct usable
           candidates than load.rowCount. Reformulate the weak column using
           concrete alternate item names or subtypes absent from priorQueries;
           do not merely reorder the same generic words. You may be called for
           more than one review round, and each round must use the newly supplied
           observations and priorQueries.
           For every bounded named-item collection, count only candidates whose
           own evidence or same-source scope evidence supports every mandatory
           qualifier in the user request. If that count is below load.answerUnitCount,
           return search_more. When observations reveal a qualifying collection
           but too few of its items, reformulate with its exact source or collection
           title plus contents, index, headings or examples. Do not fill the gap
           from another source that lacks the mandatory qualifier.
           For a user-requested proposed
           schedule, grouping or classification, count documented candidate items;
           do not demand documentary proof of the new row, column, group or slot
           assignment that SAAIA must create.
           Never invent a category or output
           one absent from availableCategories. Never answer the user. Return
           one JSON object and no prose.
           """;

    private string BuildPlannerUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            priorQueries = request.Handoff.ResearchState.ExecutedQueries
                .Concat(request.PreviousToolEvents.Select(static item =>
                    item.Request.Query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            revalidatedEvidenceCount = request.Evidence.Count,
            maximumQueries = ResolveMaximumPlanQueries(request),
            availableCategories
        }, JsonOptions);

    private string BuildResearchReviewUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<PromptEvidenceItem> evidence,
        IReadOnlySet<string> previouslyExecuted,
        IReadOnlyList<string> availableCategories)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            tool = new
            {
                name = "search_corpus",
                parameters = new
                {
                    query = "required corpus retrieval phrase",
                    category = "exact allowed category or empty",
                    topK = "integer from 1 to 60"
                }
            },
            priorQueries = previouslyExecuted
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            maximumFollowUpQueries = ResolveMaximumPlanQueries(request),
            availableCategories,
            observations = evidence
        }, JsonOptions);

    private string BuildWriterSystemPrompt(
        AdvancedAnalysisProviderRequest request)
        => """
           You are the SAAIA advanced-analysis Writer. Use only the supplied
           revalidated evidence. Return one JSON object and no prose with shape
           {"outcome":"answered|insufficient_documentation|clarification_required",
           "answerText":"...","claims":[{"claimId":"C1",
           "selectedItem":"exact selected item or empty","text":"...",
           "evidenceIds":["E1"]}]}. Every factual answer unit must have a claim
           backed by one or more supplied evidenceIds. Do not invent a value,
           title, procedure or source. Keep the user's requested language and
           format. When the evidence gives a named title, preserve the exact source
           words, spelling and diacritics; only normalize capitalization when needed
           for readability. Do not split or reinterpret a composite title.
           A non-empty candidateTitle is the exact source title of that evidence.
           Prefer these explicit candidates for named-item selections and copy the
           chosen candidateTitle verbatim into selectedItem and answerText. A
           source_chunk may also support a named item when that exact name is
           explicitly present in its content, including a source index; copy the
           exact displayed name and cite that chunk.
           In answerText, append [claimId] directly to the factual unit
           it supports and use every claimId exactly once. For a synthesis or
           grid, separate documentary facts from the synthesis you create. You may
           propose an ordering, grouping, classification, schedule placement or
           recommendation when that is necessary to produce the requested
           deliverable. Make the synthesized nature clear in the answer and cite
           the evidence that documents every selected item. Do not claim that the
           source prescribed your synthesis, and do not contradict an explicit
           source role or constraint. If the user asks what a source itself assigns,
           classifies or prescribes, that relationship remains a documentary fact
           and must be explicit in the evidence. Every mandatory factual qualifier
           in the request must remain explicit and supported. For a schedule,
           table, grouping or classification that the user asks SAAIA to create,
           the new placement is not a documentary fact. Require evidence for each
           selected item and each mandatory factual qualifier, but never require
           the source to name the row, column, group or slot chosen by SAAIA. If
           enough distinct documented candidates exist, complete the requested
           deliverable instead of reporting insufficiency because those new
           placements are absent from the sources.
           Every synthesized placement must still be semantically plausible for
           its target row or column. Infer ordinary compatibility from the exact
           item title, supplied content and retrievedFor queries; do not demand
           that the source prescribe the slot. When claimCoordinates is non-empty,
           use exactly those claimIds and coordinates in the supplied row-major
           order. Put each [claimId] in that exact answer-table cell. Cite only
           evidence whose exact title/content makes the placement ordinarily
           suitable. targetColumns records which requested column caused the
           search to retrieve that evidence; treat it as a strong relevance hint,
            not as a source-prescribed classification.
            candidateTitleIsSourceExact confirms provenance, not semantic
            suitability. A source-exact title can still be metadata, a generic
            heading or another non-item and must not fill a named-item cell. A
            source_chunk whose candidateTitle is empty remains usable when its
            content explicitly lists a concrete item name. Prefer individual candidateTitle
           evidence over a broad index, and never distribute index entries across
           incompatible slots merely to fill the grid.
           Never
           silently drop qualifiers such as audience, simplicity, compatibility or
           intended use. Each evidence item includes an opaque sourceKey. It is
           internal reasoning metadata and must never appear in answerText or a
           claim. Equal sourceKeys mean that the items come from the same canonical document
           revision. A document-level scope statement may support a qualifier for
           a named item listed elsewhere in that same source only when its wording
           clearly applies to the document's item collection; cite both evidence
           items in that claim. Never carry a scope statement across different
           sourceKeys. A source index or heading can support the existence and
           spelling of a named item. It cannot support absent factual details about
           that item. It may serve as the documented basis for a clearly labeled
           synthesis decision, subject to the rules above.
           A mandatory qualifier in the user request applies to every requested
           output unit unless the request explicitly limits its scope. Each claim's
           own evidenceIds must support that qualifier. If one selected item lacks
           the required audience, simplicity, compatibility, intended-use or other
           qualifier, replace it with a supported candidate or report the precise
           insufficiency; never silently keep the item by relying on evidence tied
           to another sourceKey. Evidence IDs and sourceKeys are internal protocol
           values. Never print an evidenceId, an advanced-evidence-* token or an
           internal-source-* token in answerText, claim text or selectedItem.
           User-visible traceability uses only [claimId] markers and the separate
           source cards.
           Distinct
           cells may cite the same evidence when it documents several distinct
           candidates. When the request requires distinct units, every claim must
           describe a distinct concrete unit and selectedItem must contain its
           exact source-backed identity. Never repeat the same selectedItem, add
           an accompaniment to disguise a duplicate, or use a generic category
           as an item. Do not repeat generic guidance to fill a grid. If the
           evidence cannot support all mandatory units and
           partial answers are not allowed, choose insufficient_documentation and
           identify the exact rows, columns or item types that remain unsupported.
           Describe the limits of the supplied evidence, not an exhaustive absence
           from the document corpus. Never claim that the documents contain only a
           listed set, or that no other candidate exists, unless supplied evidence
           explicitly proves that exhaustive statement. Prefer the smallest
           decisive unsupported relation or unit family; supported examples may be
           reported as examples, but never as an exhaustive corpus inventory.
           If supplied evidence supports some candidates for a semantic role,
           never label every neutral coordinate or every requested unit as
           unsupported merely because the complete deliverable cannot be filled.
           Report the minimum remaining deficit for the decisive role or relation,
           while preserving the supported candidates as supported examples.
           Do not turn an optional synthesis choice into an insufficiency. Reserve
           insufficient_documentation for a missing documentary fact, item or
           mandatory source-defined relationship that prevents the deliverable.
           Keep claim text concise. When selectedItem already identifies the
           choice, do not repeat the table row, column or placement wording in
           that claim.
           """
           + "\nRequested output shape: "
           + JsonSerializer.Serialize(
               BuildPromptLoad(request.Handoff.Load),
               JsonOptions);

    private static string BuildWriterRepairSystemPrompt()
        => """
           Repair one SAAIA Writer JSON object and return only the repaired JSON.
           Preserve the original answer facts, outcome and evidence mappings.
           Remove any internal sourceKey or evidenceId label from answerText, claim
           text and selectedItem, including internal-source-* and advanced-evidence-*;
           do not replace it with an invented source name. Do not add a fact or
           evidence id. For an answered outcome, append each
           [claimId] directly to its factual unit in answerText and use every
           claimId exactly once. Preserve selectedItem for every structured claim.
           If the original object is malformed or truncated,
           recover only information that is explicitly present. Keep the same JSON
           schema.
           """;

    private static string BuildSynthesisRecoverySystemPrompt()
        => """
           You are the second-pass SAAIA synthesis completer. The first Writer
           returned insufficient_documentation and may have confused missing
           documentary evidence with a placement that the user asked SAAIA to
           create. Re-evaluate the whole supplied evidence set independently.
           Return one JSON object and no prose, using exactly the Writer schema:
           {"outcome":"answered|insufficient_documentation|clarification_required",
           "answerText":"...","claims":[{"claimId":"C1",
           "selectedItem":"exact selected item or empty","text":"...",
           "evidenceIds":["E1"]}]}.

           For a proposed schedule, table, grouping or classification, sources
           must document each selected concrete item and every mandatory factual
           qualifier. Sources do not need to prescribe the new day, row, column,
            group or slot that SAAIA is asked to create. Do not demand explicit
            source labels matching the new slots merely to make that user-requested
            arrangement. If the evidence contains enough
           distinct documented candidate items, complete every requested cell.
           A non-empty candidateTitle is an exact source title. Prefer those
           candidates and copy the chosen candidateTitle verbatim into selectedItem
           and answerText. A source_chunk may also support a named item when its
           exact name is explicitly present in the chunk content, including an
           index; copy that exact displayed name and cite the chunk.
           Every placement must be semantically plausible for its target label.
           Use the exact title, supplied content and retrievedFor queries to make
           that synthesis judgment. Prefer individual candidateTitle evidence over
           a broad index and do not assign index entries to incompatible slots.
           When claimCoordinates is non-empty, use exactly its row-major claimIds,
           put each [claimId] in that exact answer-table cell. targetColumns is a
           retrieval-relevance hint rather than a source-prescribed classification;
            an item may move to another column only when its exact title/content
            makes that placement ordinarily suitable.
            candidateTitleIsSourceExact confirms provenance, not semantic
            suitability. Reject section labels, metadata and other non-items after
            inspecting the title and content. A source_chunk with an empty
            candidateTitle remains usable when it explicitly lists the selected
            concrete item.
           For a distinct structured selection, selectedItem is mandatory in
           every claim and must contain the exact identity of that claim's chosen
           item. Every normalized selectedItem must be unique. Replacing or
           extending an accompaniment does not make a repeated item distinct.
           Preserve exact source title words, spelling and diacritics, and label
           the arrangement as a SAAIA synthesis. Never invent an item, value,
           title, qualifier or evidence id. If there are truly fewer documented
           candidates than required, keep insufficient_documentation and state
           the exact smallest remaining deficit. Every factual answer unit must
           have one claim, every claim must cite supplied evidenceIds, and every
           [claimId] must appear exactly once in answerText.
           Keep each claim text concise. When selectedItem already identifies the
           choice, do not repeat row, column or placement wording in that claim.
           """;

    private static string BuildCriticSystemPrompt()
        => """
           You are the SAAIA advanced-analysis Critic. Independently audit the
           proposed Writer result against the user request and every supplied
           evidence item. Return one final JSON object and no commentary, using
           exactly the Writer schema: {"outcome":"answered|insufficient_documentation|clarification_required",
           "answerText":"...","claims":[{"claimId":"C1",
           "selectedItem":"exact selected item or empty","text":"...",
           "evidenceIds":["E1"]}]}. Treat the candidate as an untrusted proposal.
           If it is fully supported, reproduce it. Otherwise, correct or remove
           every unsupported factual unit before returning the final object. Never
           preserve a factual contradiction merely to keep the candidate wording.
           Every factual unit must be backed by its declared evidenceIds and every
           [claimId] must appear exactly once in answerText.

           A mandatory qualifier applies to every requested output unit unless the
           request explicitly limits its scope. Verify it separately for every
           claim using that claim's own evidenceIds. Do not transfer audience,
           simplicity, compatibility or intended-use scope across different
           sourceKeys. Replace a selected item that lacks its mandatory qualifier
           with a fully supported candidate, or return the smallest precise
           insufficiency.

           Audit positive statements, negative statements, counts and factual
           qualifiers. A clearly labeled ordering, grouping, classification,
           schedule placement or recommendation created by the Writer is a synthesis
           decision: require evidence for every selected item and reject any
           contradiction, but do not require the source to prescribe that synthesis.
           An absent source-defined row, column, group or slot is not a deficit when
           the user asked SAAIA to create that arrangement. If enough distinct
           documented candidates exist, preserve the complete synthesized
           deliverable.
           For a distinct structured selection, require one non-empty unique
           selectedItem per claim; an accompaniment does not make a repeated
           selectedItem distinct. When claimCoordinates is non-empty, use exactly
           its row-major claimIds and place every [claimId] in that exact table
            cell. Copy each chosen candidateTitle exactly into selectedItem and
            answerText. candidateTitleIsSourceExact confirms provenance, not
            semantic suitability. Reject section labels, metadata and other
            non-items after inspecting the title and content. A source_chunk with
            an empty candidateTitle remains usable when its content explicitly
            lists the selected concrete item.
           Audit every
           placement for ordinary semantic suitability using the title, content,
           targetColumns and requested column. targetColumns is a retrieval hint,
           so a clearly suitable item may be reassigned, but generic headings,
           metadata labels and obviously incompatible items must be replaced from
           the supplied evidence set.
           When the user asks what a source itself assigns, classifies or prescribes,
           the relationship is factual and must be explicit in the evidence. For an incomplete required
           deliverable, use insufficient_documentation, preserve supported examples,
           and state the smallest decisive remaining deficit. Scope absence claims
           to the supplied evidence unless that evidence explicitly proves an
           exhaustive corpus statement. Compare deficit and absence claims against
           the whole supplied evidence set, not only the evidenceIds already chosen
           by the candidate. Evidence IDs and sourceKeys are internal protocol
           values: never print an evidenceId, advanced-evidence-* or
           internal-source-* token in answerText, claim text or selectedItem. Do
           not add facts, preferences or evidence identifiers.
           Keep the requested language and format.
           """;

    private string BuildWriterRepairUserPrompt(
        AdvancedAnalysisProviderRequest request,
        string originalWriterJson,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
        => JsonSerializer.Serialize(new
        {
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            allowedEvidenceIds = evidence
                .Select(static item => item.Reference.EvidenceId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToArray(),
            originalWriterJson
        }, JsonOptions);

    private sealed record PromptEvidenceItem(
        string? EvidenceId,
        string SourceKey,
        IReadOnlyList<string> RetrievedFor,
        IReadOnlyList<string> TargetColumns,
        string EvidenceKind,
        string? CandidateTitle,
        bool CandidateTitleIsSourceExact,
        string Content);

    private sealed record StructuredClaimCoordinate(
        string ClaimId,
        string RowLabel,
        string ColumnLabel);

    private string BuildWriterUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<PromptEvidenceItem> evidence)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            allowsPartialAnswer = false,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(
                request.Handoff.Load),
            evidence
        }, JsonOptions);

    private string BuildSynthesisRecoveryUserPrompt(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult candidate,
        IReadOnlyList<PromptEvidenceItem> evidence)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            allowsPartialAnswer = false,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(
                request.Handoff.Load),
            firstWriterCandidate = new
            {
                outcome = candidate.Outcome,
                answerText = candidate.AnswerText,
                claims = candidate.Claims.Select(static claim => new
                {
                    claimId = claim.ClaimId,
                    selectedItem = claim.SelectedItem,
                    text = claim.Text,
                    evidenceIds = claim.EvidenceIds
                }).ToArray()
            },
            evidence
        }, JsonOptions);

    private string BuildCriticUserPrompt(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult candidate,
        IReadOnlyList<PromptEvidenceItem> evidence)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            allowsPartialAnswer = false,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(
                request.Handoff.Load),
            candidate = new
            {
                outcome = candidate.Outcome,
                answerText = candidate.AnswerText,
                claims = candidate.Claims.Select(static claim => new
                {
                    claimId = claim.ClaimId,
                    selectedItem = claim.SelectedItem,
                    text = claim.Text,
                    evidenceIds = claim.EvidenceIds
                }).ToArray()
            },
            evidence
        }, JsonOptions);

    private static IReadOnlyList<StructuredClaimCoordinate>
        BuildStructuredClaimCoordinates(AdvancedAnalysisLoadDescriptor load)
    {
        if (!load.StructuredLayout
            || load.RowCount <= 0
            || load.ColumnCount <= 0
            || load.RowLabels.Count < load.RowCount
            || load.Columns.Count < load.ColumnCount)
        {
            return [];
        }

        var coordinates = new List<StructuredClaimCoordinate>(
            checked(load.RowCount * load.ColumnCount));
        var claimNumber = 1;
        for (var rowIndex = 0; rowIndex < load.RowCount; rowIndex++)
        {
            for (var columnIndex = 0;
                 columnIndex < load.ColumnCount;
                 columnIndex++)
            {
                coordinates.Add(new StructuredClaimCoordinate(
                    $"C{claimNumber++}",
                    load.RowLabels[rowIndex],
                    load.Columns[columnIndex]));
            }
        }
        return coordinates;
    }

    internal static IReadOnlyList<string> ResolveTargetColumnsForPrompt(
        AdvancedAnalysisLoadDescriptor load,
        IReadOnlyList<string> retrievalQueries)
    {
        if (!load.StructuredLayout || load.Columns.Count == 0)
            return [];

        var targets = new List<string>();
        foreach (var query in retrievalQueries)
        {
            var normalizedQuery = NormalizeClaimText(query);
            if (normalizedQuery.Length == 0)
                continue;
            var bestMatch = load.Columns
                .Select(column => new
                {
                    Column = column,
                    Normalized = NormalizeClaimText(column)
                })
                .Where(item => item.Normalized.Length > 0
                               && Regex.IsMatch(
                                   normalizedQuery,
                                   $@"(?:^|\s){Regex.Escape(item.Normalized)}(?:\s|$)",
                                   RegexOptions.CultureInvariant))
                .OrderByDescending(static item => item.Normalized.Length)
                .ThenBy(static item => item.Column, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (bestMatch is not null
                && !targets.Contains(
                    bestMatch.Column,
                    StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(bestMatch.Column);
            }
        }
        return targets;
    }

    private static string FoldDiacritics(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private IReadOnlyList<PromptEvidenceItem> BuildPromptEvidence(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId)
    {
        var configuredMaximum = Math.Clamp(
            _options.MaximumEvidencePromptCharacters,
            8_000,
            1_000_000);
        const int maximumCharactersPerPromptEvidence = 700;
        var distinctRetrievalQueryCount = retrievalQueriesByEvidenceId
            .Values
            .SelectMany(static queries => queries)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var structuredMinimum = ResolveStructuredEvidencePromptMinimumCharacters(
            request.Handoff.Load,
            distinctRetrievalQueryCount,
            maximumCharactersPerPromptEvidence);
        var remaining = (int)Math.Max(configuredMaximum, structuredMinimum);
        var promptEvidence = new List<PromptEvidenceItem>();
        var sourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> prioritizedEvidence =
            PrioritizeCollectionEvidenceForPrompt(
            request.Handoff.Load,
            evidence,
            retrievalQueriesByEvidenceId);
        foreach (var item in prioritizedEvidence)
        {
            if (remaining <= 0)
                break;
            var content = item.Content ?? string.Empty;
            if (content.Length > maximumCharactersPerPromptEvidence)
                content = content[..maximumCharactersPerPromptEvidence];
            if (content.Length > remaining)
                content = content[..remaining];
            remaining -= content.Length;
            var evidenceId = item.Reference.EvidenceId;
            var retrievedFor = evidenceId is not null
                               && retrievalQueriesByEvidenceId.TryGetValue(
                                   evidenceId,
                                   out var queries)
                ? queries.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            var sourceIdentity = BuildCanonicalSourceIdentity(
                item,
                evidenceId ?? sourceKeys.Count.ToString());
            if (!sourceKeys.TryGetValue(sourceIdentity, out var sourceKey))
            {
                sourceKey = $"internal-source-{sourceKeys.Count + 1}";
                sourceKeys.Add(sourceIdentity, sourceKey);
            }
            promptEvidence.Add(new PromptEvidenceItem(
                evidenceId,
                sourceKey,
                retrievedFor,
                ResolveTargetColumnsForPrompt(
                    request.Handoff.Load,
                    retrievedFor),
                !string.IsNullOrWhiteSpace(item.Reference.ContentCardId)
                    ? "content_card"
                    : !string.IsNullOrWhiteSpace(item.Reference.ChunkId)
                        ? "source_chunk"
                        : "source_span",
                string.IsNullOrWhiteSpace(item.ExactTitle)
                    ? null
                    : item.ExactTitle.Trim(),
                !string.IsNullOrWhiteSpace(item.ExactTitle),
                content));
        }
        return promptEvidence;
    }

    internal static int ResolveStructuredEvidencePromptMinimumCharacters(
        AdvancedAnalysisLoadDescriptor load,
        int distinctRetrievalQueryCount,
        int maximumCharactersPerPromptEvidence = 700)
    {
        if (!load.StructuredLayout)
            return 0;
        var rowCount = Math.Max(1, load.RowCount);
        var columnCount = Math.Max(1, load.ColumnCount);
        var gridCoverageSlots = checked(rowCount * columnCount + columnCount);
        var researchCoverageSlots = checked(
            Math.Max(0, distinctRetrievalQueryCount) * rowCount);
        var evidenceSlots = Math.Max(
            gridCoverageSlots,
            researchCoverageSlots);
        return (int)Math.Clamp(
            (long)evidenceSlots
            * Math.Clamp(maximumCharactersPerPromptEvidence, 1, 10_000),
            8_000,
            64_000);
    }

    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        PrioritizeCollectionEvidenceForPrompt(
            AdvancedAnalysisLoadDescriptor load,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
            IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId)
    {
        if (RequiresDistinctStructuredSelection(load))
        {
            var distinctCandidates = evidence
                .Select((item, index) => new
                {
                    Item = item,
                    Index = index,
                    HasExactTitle = !string.IsNullOrWhiteSpace(item.ExactTitle),
                    Queries = item.Reference.EvidenceId is not null
                              && retrievalQueriesByEvidenceId.TryGetValue(
                                  item.Reference.EvidenceId,
                                  out var queries)
                        ? queries
                            .Where(static query => !string.IsNullOrWhiteSpace(query))
                            .Select(static query => query.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(static query => query, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : []
                })
                .ToArray();
            var exactTitleCandidates = distinctCandidates
                .Where(static item => item.HasExactTitle)
                .ToArray();
            var sourceTextCandidates = distinctCandidates
                .Where(static item => !item.HasExactTitle)
                .ToArray();
            var queryOrder = distinctCandidates
                .SelectMany(static item => item.Queries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static query => query, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedIndexes = new HashSet<int>();
            var prioritized = new List<AdvancedAnalysisResolvedEvidence>(evidence.Count);

            while (true)
            {
                var selectedInRound = false;
                foreach (var query in queryOrder)
                {
                    foreach (var candidatePool in new[]
                             {
                                 exactTitleCandidates,
                                 sourceTextCandidates
                             })
                    {
                        var next = candidatePool
                            .Where(item => !selectedIndexes.Contains(item.Index)
                                           && item.Queries.Contains(
                                               query,
                                               StringComparer.OrdinalIgnoreCase))
                            .OrderBy(static item => item.Queries.Length)
                            .ThenBy(static item => item.Index)
                            .FirstOrDefault();
                        if (next is null)
                            continue;

                        selectedIndexes.Add(next.Index);
                        prioritized.Add(next.Item);
                        selectedInRound = true;
                    }
                }

                if (!selectedInRound)
                    break;
            }

            foreach (var candidate in exactTitleCandidates
                         .Where(item => !selectedIndexes.Contains(item.Index))
                         .OrderBy(static item => item.Queries.Length)
                         .ThenBy(static item => item.Index))
            {
                selectedIndexes.Add(candidate.Index);
                prioritized.Add(candidate.Item);
            }

            prioritized.AddRange(distinctCandidates
                .Where(item => !item.HasExactTitle
                               && !selectedIndexes.Contains(item.Index))
                .OrderBy(static item => item.Index)
                .Select(static item => item.Item));
            return prioritized;
        }
        if (evidence.Count < 2
            || load.AnswerUnitCount < 2
            || load.StructuredLayout
            || load.BoundedNamedDocumentExtraction
            || (!load.PlanKind.Contains("multi_item", StringComparison.OrdinalIgnoreCase)
                && !load.AtomicEvidenceMode.Contains(
                    "one_per_item",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return evidence;
        }

        var candidates = evidence
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                SourceIdentity = BuildCanonicalSourceIdentity(
                    item,
                    item.Reference.EvidenceId ?? index.ToString()),
                QueryCoverage = item.Reference.EvidenceId is not null
                                && retrievalQueriesByEvidenceId.TryGetValue(
                                    item.Reference.EvidenceId,
                                    out var queries)
                    ? queries.Count
                    : 0,
                PageIdentity = $"{item.Reference.PageStart}:{item.Reference.PageEnd}"
            })
            .ToArray();
        var promotionLimit = Math.Clamp(load.AnswerUnitCount + 1, 2, 16);
        var leadingSource = candidates
            .GroupBy(static item => item.SourceIdentity, StringComparer.Ordinal)
            .Select(group => new
            {
                SourceIdentity = group.Key,
                Score = group
                    .OrderByDescending(static item => item.QueryCoverage)
                    .ThenBy(static item => item.Index)
                    .Take(promotionLimit)
                    .Sum(static item => item.QueryCoverage),
                FirstIndex = group.Min(static item => item.Index)
            })
            .OrderByDescending(static group => group.Score)
            .ThenBy(static group => group.FirstIndex)
            .First();
        if (leadingSource.Score <= 0)
            return evidence;

        var sourceCandidates = candidates
            .Where(item => item.SourceIdentity == leadingSource.SourceIdentity)
            .ToArray();
        var promoted = sourceCandidates
            .GroupBy(static item => item.PageIdentity, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static item => item.QueryCoverage)
                .ThenBy(static item => item.Index)
                .First())
            .OrderByDescending(static item => item.QueryCoverage)
            .ThenBy(static item => item.Index)
            .Concat(sourceCandidates
                .GroupBy(static item => item.PageIdentity, StringComparer.Ordinal)
                .SelectMany(static group => group
                    .OrderByDescending(static item => item.QueryCoverage)
                    .ThenBy(static item => item.Index)
                    .Skip(1)))
            .Take(promotionLimit)
            .Select(static item => item.Item)
            .ToArray();
        var promotedIds = promoted
            .Select(static item => item.Reference.EvidenceId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return promoted
            .Concat(evidence.Where(item =>
                string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                || !promotedIds.Contains(item.Reference.EvidenceId)))
            .ToArray();
    }

    private static string BuildCanonicalSourceIdentity(
        AdvancedAnalysisResolvedEvidence item,
        string fallback)
    {
        var identity = string.Join(
            "|",
            item.Reference.DocId ?? string.Empty,
            item.Reference.RevisionId ?? string.Empty,
            item.Reference.SourceHash ?? string.Empty);
        return identity == "||" ? "evidence:" + fallback : identity;
    }

    private object BuildPromptLoad(AdvancedAnalysisLoadDescriptor load)
        => new
        {
            load.PlanKind,
            load.Deliverable,
            load.AnswerUnitCount,
            load.AtomicEvidenceCount,
            load.RowCount,
            load.ColumnCount,
            load.StructuredLayout,
            load.AtomicEvidenceType,
            load.AtomicEvidenceMode,
            load.SelectionPolicy,
            load.QuestionFocus,
            load.RequestedDocumentName,
            load.BoundedNamedDocumentExtraction,
            synthesisRelationshipPolicy =
                "Evidence proves each selected item and mandatory factual qualifier; SAAIA may create row, column, group and schedule placement unless the user asks for a source-prescribed relationship.",
            candidateScopePaths = IsExternalProvider
                ? Array.Empty<string>()
                : load.CandidateScopePaths.ToArray(),
            load.RowLabels,
            load.Columns
        };
}
