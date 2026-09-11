using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int DefaultEvidenceOverviewPointCount = 7;
    private const int MaximumEvidenceOverviewPointCount = 10;
    private const int EvidenceOverviewContextLimit = 12;

    private async Task<JsonElement> ExecRagEvidenceOverviewSummaryAsync(
        ResolvedDocRef resolved,
        ToolMemory.SourceRef? fallbackSource,
        JsonElement args,
        string responseLanguage,
        string docLanguage,
        CancellationToken ct)
    {
        var userRequest = GetStringArg(args, "userRequest")?.Trim();
        if (string.IsNullOrWhiteSpace(userRequest))
            userRequest = "Summarize the named document into useful decision points.";
        var overviewFacets = NormalizeEvidenceOverviewFacets(args);

        var requestedPointCount = Math.Clamp(
            GetIntArg(args, "requestedPointCount")
            ?? TryExtractEvidenceOverviewPointCount(userRequest)
            ?? DefaultEvidenceOverviewPointCount,
            2,
            MaximumEvidenceOverviewPointCount);
        var requestedSampleCount = Math.Clamp(
            GetIntArg(args, "sampleCount") ?? requestedPointCount + 1,
            requestedPointCount,
            MaximumEvidenceOverviewPointCount + 1);

        var pageCount = resolved.Pages ?? 0;
        if (pageCount <= 0)
        {
            try
            {
                var navigation = await _api.DocumentsNavigationAsync(
                        path: null,
                        categoryRef: null,
                        resolved.DocId,
                        resolved.DocPath,
                        q: null,
                        kind: null,
                        limit: 1,
                        offset: 0,
                        ct)
                    .ConfigureAwait(false);
                pageCount = ReadEvidenceOverviewPageCount(navigation);
            }
            catch
            {
                pageCount = 0;
            }
        }

        if (pageCount <= 0)
        {
            return JsonSerializer.SerializeToElement(new
            {
                error = "document_page_count_unavailable",
                strategy = "evidence_overview"
            });
        }

        var facetAcquisition = overviewFacets.Length >= 2
            ? await TryAcquireEvidenceOverviewFacetsAsync(
                    resolved,
                    userRequest,
                    overviewFacets,
                    ct)
                .ConfigureAwait(false)
            : null;
        int[] targetPages;
        EvidenceOverviewContextSelection[] selections;
        long materializationMs;
        if (facetAcquisition is not null)
        {
            selections = facetAcquisition.Selections;
            targetPages = selections
                .Select(static selection => selection.TargetPage)
                .Distinct()
                .Order()
                .ToArray();
            materializationMs = facetAcquisition.MaterializationMs;
        }
        else
        {
            targetPages = SelectEvidenceOverviewTargetPages(
                pageCount,
                requestedSampleCount);
            var materializationWatch = Stopwatch.StartNew();
            var contextResults = await Task.WhenAll(targetPages.Select(
                async targetPage =>
                {
                    var watch = Stopwatch.StartNew();
                    var context = await _api.DocumentsContextAsync(
                            resolved.DocId,
                            resolved.DocPath,
                            chunkId: null,
                            pageStart: targetPage,
                            pageEnd: targetPage,
                            before: 0,
                            after: 0,
                            limit: EvidenceOverviewContextLimit,
                            offset: 0,
                            ct)
                        .ConfigureAwait(false);
                    watch.Stop();
                    return BuildEvidenceOverviewContextSelection(
                        targetPage,
                        context,
                        watch.ElapsedMilliseconds);
                }));
            materializationWatch.Stop();
            materializationMs = materializationWatch.ElapsedMilliseconds;
            selections = contextResults
                .Where(static selection => selection is not null)
                .Cast<EvidenceOverviewContextSelection>()
                .OrderBy(static selection => selection.TargetPage)
                .ToArray();
        }

        var requiredCandidateCount = facetAcquisition is null
            ? requestedPointCount
            : selections.Length;
        if (selections.Length < requiredCandidateCount)
        {
            return JsonSerializer.SerializeToElement(new
            {
                error = "insufficient_page_stratified_evidence",
                strategy = "evidence_overview",
                requestedPointCount,
                materializedChunkCount = selections.Length,
                targetPages
            });
        }

        var toolResults = new ToolResults();
        foreach (var selection in selections)
        {
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "documents.context",
                DurationMs = selection.ElapsedMs,
                Result = JsonSerializer.SerializeToElement(new
                {
                    document = selection.Document,
                    requestedPage = selection.TargetPage,
                    items = new[] { selection.Item }
                })
            });
        }

        var bundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            userRequest,
            traceId: "document-overview-" + Guid.NewGuid().ToString("N")[..8]);
        var evidence = bundle.Items
            .Where(HasCompleteEvidenceOverviewIdentity)
            .Take(selections.Length)
            .ToArray();
        if (evidence.Length < requiredCandidateCount)
        {
            return JsonSerializer.SerializeToElement(new
            {
                error = "incomplete_page_stratified_identity",
                strategy = "evidence_overview",
                requestedPointCount,
                evidenceCount = evidence.Length,
                bundleId = bundle.BundleId
            });
        }

        var evidenceByFacet = facetAcquisition is null
            ? null
            : TryMapEvidenceOverviewFacetsToEvidence(
                facetAcquisition.SelectedChunkIdsByFacet,
                evidence);
        if (facetAcquisition is not null && evidenceByFacet is null)
        {
            EmitRagTrace(
                "document_overview.facet_evidence_mapping.rejected",
                ("facet_count", overviewFacets.Length),
                ("evidence_count", evidence.Length));
        }

        var selectedEvidence = evidence;
        var selectionWatch = new Stopwatch();

        var writerPrompt = BuildEvidenceOverviewWriterPrompt(
            userRequest,
            resolved,
            responseLanguage,
            requiredCandidateCount,
            selectedEvidence);
        var writerWatch = Stopwatch.StartNew();
        var rawAnswer = await CompleteEvidenceOverviewWriterAsync(
                new[]
                {
                    (
                        "system",
                        BuildEvidenceOverviewWriterSystemPrompt(
                            responseLanguage)),
                    ("user", writerPrompt)
                },
                maximumOutputTokens: 320,
                ct)
            .ConfigureAwait(false);
        writerWatch.Stop();

        var detectedLanguage = LocalizedStrings.DetectLanguage(
            rawAnswer,
            responseLanguage);
        if (!string.Equals(
                NormalizeLanguageCode(detectedLanguage),
                NormalizeLanguageCode(responseLanguage),
                StringComparison.OrdinalIgnoreCase))
        {
            var fallback = TryBuildEvidenceOverviewExtractiveFallbackPayload(
                resolved,
                fallbackSource,
                responseLanguage,
                docLanguage,
                overviewFacets,
                evidenceByFacet,
                bundle,
                pageCount,
                targetPages,
                selections.Length,
                materializationMs,
                facetAcquisition,
                "document_overview_language_mismatch",
                writerWatch.ElapsedMilliseconds);
            if (fallback.HasValue)
                return fallback.Value;
            return JsonSerializer.SerializeToElement(new
            {
                error = "document_overview_language_mismatch",
                strategy = "evidence_overview",
                expectedLanguage = responseLanguage,
                detectedLanguage,
                writerMs = writerWatch.ElapsedMilliseconds,
                bundleId = bundle.BundleId
            });
        }

        var candidateValidation = ValidateAndRenderEvidenceOverviewCandidates(
            rawAnswer,
            selectedEvidence,
            selectedEvidence.Length,
            responseLanguage);
        long candidateRepairMs = 0;
        var writerRepairUsed = false;
        if (candidateValidation.ValidCandidateCount < requiredCandidateCount
            && TryReadSingleEvidenceOverviewObligationRejection(
                candidateValidation.Rejections,
                out var rejectedEvidenceId,
                out var rejectedCandidateText))
        {
            writerRepairUsed = true;
            var rejectedEvidence = selectedEvidence.First(item => string.Equals(
                item.EvidenceId,
                rejectedEvidenceId,
                StringComparison.OrdinalIgnoreCase));
            var repairWatch = Stopwatch.StartNew();
            var repairedCandidate = await CompleteEvidenceOverviewWriterAsync(
                    new[]
                    {
                        (
                            "system",
                            BuildEvidenceOverviewWriterSystemPrompt(
                                responseLanguage)),
                        (
                            "user",
                            BuildEvidenceOverviewCandidateRepairPrompt(
                                rejectedEvidence,
                                rejectedCandidateText,
                                responseLanguage))
                    },
                    maximumOutputTokens: 96,
                    ct)
                .ConfigureAwait(false);
            repairWatch.Stop();
            candidateRepairMs = repairWatch.ElapsedMilliseconds;
            var repairedRawAnswer = ReplaceEvidenceOverviewCandidateLine(
                rawAnswer,
                rejectedEvidenceId,
                repairedCandidate);
            var repairedValidation = ValidateAndRenderEvidenceOverviewCandidates(
                repairedRawAnswer,
                selectedEvidence,
                selectedEvidence.Length,
                responseLanguage);
            EmitRagTrace(
                repairedValidation.ValidCandidateCount >= requiredCandidateCount
                    ? "document_overview.candidate_repair.accepted"
                    : "document_overview.candidate_repair.rejected",
                ("evidence_id", rejectedEvidenceId),
                ("valid_count", repairedValidation.ValidCandidateCount),
                ("repair_ms", candidateRepairMs));
            if (repairedValidation.ValidCandidateCount
                > candidateValidation.ValidCandidateCount)
            {
                rawAnswer = repairedRawAnswer;
                candidateValidation = repairedValidation;
            }
        }

        if (candidateValidation.ValidCandidateCount > requiredCandidateCount)
        {
            var validEvidenceIds = candidateValidation.CitedEvidenceIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var validEvidence = evidence
                .Where(item => validEvidenceIds.Contains(item.EvidenceId))
                .ToArray();
            selectionWatch.Start();
            var rawSelection = await _llm.CompleteAsync(
                    new[]
                    {
                        (
                            "system",
                            "You are the semantic evidence selector. Return only the requested evidence IDs separated by commas. Never write claims or commentary."),
                        (
                            "user",
                            BuildEvidenceOverviewSelectionPrompt(
                                userRequest,
                                requiredCandidateCount,
                                validEvidence))
                    },
                    forceJson: false,
                    ct)
                .ConfigureAwait(false);
            selectionWatch.Stop();
            var selectedIds = TryParseEvidenceOverviewSelection(
                rawSelection,
                validEvidence,
                requiredCandidateCount);
            if (selectedIds is null)
            {
                EmitRagTrace(
                    "document_overview.selector.rejected",
                    ("requested_count", requestedPointCount),
                    ("evidence_count", validEvidence.Length),
                    ("selector_ms", selectionWatch.ElapsedMilliseconds));
                return JsonSerializer.SerializeToElement(new
                {
                    error = "document_overview_selection_contract_failed",
                    strategy = "evidence_overview",
                    requestedPointCount,
                    evidenceCount = validEvidence.Length,
                    selectorMs = selectionWatch.ElapsedMilliseconds,
                    rawSelection = CompactEvidenceOverviewText(
                        rawSelection,
                        500),
                    bundleId = bundle.BundleId
                });
            }

            candidateValidation = SelectEvidenceOverviewCandidates(
                candidateValidation,
                selectedIds,
                responseLanguage);
            var selectedSet = selectedIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            selectedEvidence = validEvidence
                .Where(item => selectedSet.Contains(item.EvidenceId))
                .ToArray();
            EmitRagTrace(
                "document_overview.selector.accepted",
                ("selected_ids", selectedEvidence.Select(
                    static item => item.EvidenceId).ToArray()),
                ("selector_ms", selectionWatch.ElapsedMilliseconds));
        }
        else if (candidateValidation.ValidCandidateCount == requiredCandidateCount)
        {
            var validIds = candidateValidation.CitedEvidenceIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            selectedEvidence = evidence
                .Where(item => validIds.Contains(item.EvidenceId))
                .ToArray();
            EmitRagTrace(
                "document_overview.selector.skipped",
                ("reason", "verified_candidate_count_matches_request"),
                ("selected_ids", selectedEvidence.Select(
                    static item => item.EvidenceId).ToArray()));
        }

        if (candidateValidation.ValidCandidateCount < requiredCandidateCount)
        {
            EmitRagTrace(
                "document_overview.writer.rejected",
                ("candidate_count", candidateValidation.CandidatePointCount),
                ("valid_count", candidateValidation.ValidCandidateCount),
                ("rejection_codes", candidateValidation.Rejections
                    .Select(static rejection => rejection.Split(':')[0])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()),
                ("writer_ms", writerWatch.ElapsedMilliseconds));
            var fallback = TryBuildEvidenceOverviewExtractiveFallbackPayload(
                resolved,
                fallbackSource,
                responseLanguage,
                docLanguage,
                overviewFacets,
                evidenceByFacet,
                bundle,
                pageCount,
                targetPages,
                selections.Length,
                materializationMs,
                facetAcquisition,
                "document_overview_candidate_contract_failed",
                writerWatch.ElapsedMilliseconds);
            if (fallback.HasValue)
                return fallback.Value;
            return JsonSerializer.SerializeToElement(new
            {
                error = "document_overview_candidate_contract_failed",
                strategy = "evidence_overview",
                requestedPointCount,
                candidateValidation.CandidatePointCount,
                candidateValidation.ValidCandidateCount,
                candidateValidation.Rejections,
                writerMs = writerWatch.ElapsedMilliseconds,
                rawWriterAnswer = CompactEvidenceOverviewText(
                    rawAnswer,
                    3000),
                bundleId = bundle.BundleId
            });
        }

        EmitRagTrace(
            "document_overview.writer.accepted",
            ("candidate_count", candidateValidation.CandidatePointCount),
            ("valid_count", candidateValidation.ValidCandidateCount),
            ("cited_ids", candidateValidation.CitedEvidenceIds),
            ("writer_ms", writerWatch.ElapsedMilliseconds));

        var allowedEvidenceIds = candidateValidation.CitedEvidenceIds;
        var draft = new WriterDraft(
            candidateValidation.RenderedAnswer,
            allowedEvidenceIds);
        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake: null,
            allowedEvidenceIds: allowedEvidenceIds,
            enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true,
            allowMultipleEvidencePerVisibleSource: false,
            requireSeparateAtomicClaims: true);
        if (!verification.IsValid)
        {
            var fallback = TryBuildEvidenceOverviewExtractiveFallbackPayload(
                resolved,
                fallbackSource,
                responseLanguage,
                docLanguage,
                overviewFacets,
                evidenceByFacet,
                bundle,
                pageCount,
                targetPages,
                selections.Length,
                materializationMs,
                facetAcquisition,
                "document_overview_source_contract_failed",
                writerWatch.ElapsedMilliseconds);
            if (fallback.HasValue)
                return fallback.Value;
            return JsonSerializer.SerializeToElement(new
            {
                error = "document_overview_source_contract_failed",
                strategy = "evidence_overview",
                verificationErrors = verification.Errors.Select(
                    static error => new
                    {
                        error.Code,
                        error.Message,
                        error.EvidenceId
                    }),
                writerMs = writerWatch.ElapsedMilliseconds,
                bundleId = bundle.BundleId
            });
        }

        var semanticReview = await ReviewEvidenceOverviewFinalDraftAsync(
            userRequest, responseLanguage, resolved.DocPath, draft, bundle, ct).ConfigureAwait(false);
        if (!semanticReview.IsAccepted
            && string.Equals(semanticReview.Decision, "revise", StringComparison.Ordinal)
            && !writerRepairUsed)
        {
            // Share the existing single writer repair with the contract-repair path.
            writerRepairUsed = true;
            var revision = await TryReviseEvidenceOverviewDraftAsync(
                userRequest, responseLanguage, resolved, draft, semanticReview.Reasons,
                selectedEvidence, bundle, requiredCandidateCount, ct).ConfigureAwait(false);
            if (revision is not null)
            {
                candidateValidation = revision.Validation;
                candidateRepairMs = revision.ElapsedMilliseconds;
                allowedEvidenceIds = candidateValidation.CitedEvidenceIds;
                draft = new WriterDraft(candidateValidation.RenderedAnswer, allowedEvidenceIds);
                verification = revision.Verification;
                semanticReview = await ReviewEvidenceOverviewFinalDraftAsync(
                    userRequest, responseLanguage, resolved.DocPath, draft, bundle, ct).ConfigureAwait(false);
            }
        }
        if (!semanticReview.IsAccepted)
            return JsonSerializer.SerializeToElement(new
            {
                error = "document_overview_semantic_review_failed",
                strategy = "evidence_overview",
                decision = semanticReview.Decision,
                reasons = semanticReview.Reasons,
                bundleId = bundle.BundleId
            });

        var citedEvidence = verification.CitedEvidence
            .OrderBy(item => Array.IndexOf(
                allowedEvidenceIds.ToArray(),
                item.EvidenceId))
            .ToArray();
        var sourcePayloads = citedEvidence
            .Select(BuildEvidenceOverviewSourcePayload)
            .ToArray();
        var primarySource = citedEvidence.First();
        var payload = new
        {
            docId = resolved.DocId,
            docPath = resolved.DocPath,
            docName = resolved.DocName,
            mode = "live_evidence_overview",
            level = "medium",
            strategy = "evidence_overview",
            language = responseLanguage,
            responseLanguage,
            docLanguage,
            profileLanguage = primarySource.ProfileLanguage
                              ?? fallbackSource?.ProfileLanguage,
            sourceHash = primarySource.SourceHash
                         ?? fallbackSource?.SourceHash,
            category = fallbackSource?.Category ?? resolved.Category,
            categoryRef = fallbackSource?.CategoryRef ?? resolved.CategoryRef,
            categoryPath = primarySource.CategoryPath
                           ?? fallbackSource?.CategoryPath
                           ?? resolved.CategoryPath,
            sourceMetadata = sourcePayloads,
            sourceMetadataTotal = sourcePayloads.Length,
            sourceMetadataTruncated = false,
            sourceMetadataSample = sourcePayloads,
            sampling = new
            {
                method = "page_bucket_midpoint_canonical_context",
                pageCount,
                targetPages,
                materializedChunkCount = selections.Length,
                evidenceCount = evidence.Length,
                selectedEvidenceCount = selectedEvidence.Length,
                selectedEvidenceIds = selectedEvidence.Select(
                    static item => item.EvidenceId),
                requestedPointCount,
                candidateCount = candidateValidation.CandidatePointCount,
                validCandidateCount = candidateValidation.ValidCandidateCount,
                rejectedCandidateCount = candidateValidation.Rejections.Count,
                materializationMs,
                selectorMs = selectionWatch.ElapsedMilliseconds,
                candidateRepairMs,
                writerMs = writerWatch.ElapsedMilliseconds
            },
            evidenceBundle = new
            {
                bundle.BundleId,
                itemCount = bundle.Items.Count,
                citedEvidenceIds = allowedEvidenceIds,
                sourceVerified = verification.IsValid,
                traceEvents = bundle.TraceEvents
            },
            summaryText = candidateValidation.RenderedAnswer,
            anchors = sourcePayloads
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static EvidenceOverviewContextSelection?
        BuildEvidenceOverviewContextSelection(
            int targetPage,
            JsonElement context,
            long elapsedMs)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || !context.TryGetProperty("document", out var document)
            || document.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var candidates = items.EnumerateArray()
            .Where(item => EvidenceOverviewReadInt(item, "pageStart") <= targetPage
                           && EvidenceOverviewReadInt(item, "pageEnd") >= targetPage)
            .Where(item => !string.IsNullOrWhiteSpace(
                EvidenceOverviewReadString(item, "text")))
            .Where(item => !string.Equals(
                EvidenceOverviewReadString(item, "contentRole"),
                "navigation",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(EvidenceOverviewChunkPreference)
            .ThenByDescending(item => EvidenceOverviewReadInt(
                item,
                "tokenCount"))
            .ThenBy(item => EvidenceOverviewReadInt(item, "chunkIndex"))
            .ToArray();
        return candidates.Length == 0
            ? null
            : new EvidenceOverviewContextSelection(
                targetPage,
                document.Clone(),
                candidates[0].Clone(),
                elapsedMs);
    }

    private static int EvidenceOverviewChunkPreference(JsonElement item)
        => EvidenceOverviewReadInt(item, "tokenCount") < 60
            ? 3
            : EvidenceOverviewReadString(item, "chunkType") switch
            {
                "unit_exact_v1" => 0,
                "section_window_v1" => 1,
                _ => 2
            };

    private static int ReadEvidenceOverviewPageCount(JsonElement navigation)
    {
        if (navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var item in items.EnumerateArray())
        {
            var pageCount = EvidenceOverviewReadInt(item, "pageCount");
            if (pageCount > 0)
                return pageCount;
        }

        return 0;
    }

    private static int[] SelectEvidenceOverviewTargetPages(
        int pageCount,
        int requestedCount)
    {
        var count = Math.Min(Math.Max(1, requestedCount), pageCount);
        return Enumerable.Range(0, count)
            .Select(bucket => Math.Clamp(
                (int)Math.Floor(
                    (bucket + 0.5d) * pageCount / count) + 1,
                1,
                pageCount))
            .Distinct()
            .ToArray();
    }

    internal static int[] SelectEvidenceOverviewTargetPagesForTests(
        int pageCount,
        int requestedCount)
        => SelectEvidenceOverviewTargetPages(pageCount, requestedCount);

    private static bool HasCompleteEvidenceOverviewIdentity(EvidenceItem item)
        => !string.IsNullOrWhiteSpace(item.DocId)
           && !string.IsNullOrWhiteSpace(item.DocPath)
           && !string.IsNullOrWhiteSpace(item.SourceHash)
           && !string.IsNullOrWhiteSpace(item.RevisionId)
           && !string.IsNullOrWhiteSpace(item.ChunkId)
           && item.PageStart is > 0
           && item.PageEnd >= item.PageStart
           && !string.IsNullOrWhiteSpace(item.Excerpt);

    private static string BuildEvidenceOverviewWriterSystemPrompt(
        string responseLanguage)
    {
        var language = NormalizeLanguageCode(responseLanguage);
        if (language == "fr")
        {
            return "Tu es le redacteur semantique final. Reponds uniquement en francais, meme lorsque les extraits sont dans une autre langue. Utilise uniquement les extraits canoniques fournis. N'invente rien. Produis des enonces factuels autonomes et concis qui conservent exactement la relation exprimee par la source; le verificateur ajoute sa modalite.";
        }

        var languageName = language switch
        {
            "en" => "English",
            "es" => "Spanish",
            "pt" => "Portuguese",
            "de" => "German",
            "it" => "Italian",
            _ => "French"
        };
        return "You are the final semantic writer. Answer only in "
               + languageName
               + ", even when the evidence is in another language. Use only the canonical evidence below. Never invent facts or strengthen informative material into a requirement.";
    }

    private static string BuildEvidenceOverviewSelectionPrompt(
        string userRequest,
        int requestedPointCount,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_SELECTOR");
        prompt.AppendLine("USER_REQUEST:");
        prompt.AppendLine(userRequest);
        prompt.Append("Select exactly ").Append(requestedPointCount)
            .Append(" distinct evidence IDs from the ").Append(evidence.Count)
            .AppendLine(" items below.");
        prompt.AppendLine("Choose coherent self-contained passages that are most useful to answer the request faithfully across the document.");
        prompt.AppendLine("Prefer complete prose, explicit definitions, requirements or conclusions whose actor, action, scope and conditions are readable in one item.");
        prompt.AppendLine("Reject navigation lists, bibliographies, OCR-interleaved table columns, sentence fragments and passages whose meaning depends on missing neighboring content.");
        prompt.AppendLine("After clarity and source fidelity, maximize useful coverage and avoid redundant topics.");
        prompt.AppendLine("Return only comma-separated IDs, for example: E1,E3,E4. No words, JSON, brackets or claims.");
        prompt.AppendLine("CANONICAL_EVIDENCE:");
        foreach (var item in evidence)
        {
            prompt.Append(item.EvidenceId).Append(" page=")
                .Append(item.PageStart).Append(" source=")
                .AppendLine(CompactEvidenceOverviewText(
                    item.Excerpt,
                    650));
        }

        return prompt.ToString();
    }

    private static string[]? TryParseEvidenceOverviewSelection(
        string? rawSelection,
        IReadOnlyList<EvidenceItem> evidence,
        int requestedPointCount)
    {
        var raw = rawSelection?.Trim() ?? string.Empty;
        if (raw.Length == 0
            || Regex.Replace(
                    raw,
                    @"E\d{1,3}|[\s,;|]",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Length != 0)
        {
            return null;
        }

        var ids = Regex.Matches(
                raw,
                @"E\d{1,3}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToUpperInvariant())
            .ToArray();
        if (ids.Length != requestedPointCount
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != requestedPointCount)
        {
            return null;
        }

        var allowed = evidence.Select(static item => item.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.All(allowed.Contains) ? ids : null;
    }

    internal static string BuildEvidenceOverviewSelectionPromptForTests(
        string userRequest,
        int requestedPointCount,
        IReadOnlyList<EvidenceItem> evidence)
        => BuildEvidenceOverviewSelectionPrompt(
            userRequest,
            requestedPointCount,
            evidence);

    internal static string[]? TryParseEvidenceOverviewSelectionForTests(
        string? rawSelection,
        IReadOnlyList<EvidenceItem> evidence,
        int requestedPointCount)
        => TryParseEvidenceOverviewSelection(
            rawSelection,
            evidence,
            requestedPointCount);

    private static string BuildEvidenceOverviewWriterPrompt(
        string userRequest,
        ResolvedDocRef document,
        string responseLanguage,
        int requestedPointCount,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_WRITER");
        if (NormalizeLanguageCode(responseLanguage) == "fr")
        {
            prompt.AppendLine("DEMANDE:");
            prompt.AppendLine(userRequest);
            prompt.AppendLine();
            prompt.AppendLine("CONTRAT DE SORTIE:");
            prompt.Append("- Reponds a la demande en exactement ")
                .Append(evidence.Count)
                .AppendLine(" phrases, une seule phrase par ligne.");
            for (var index = 0; index < evidence.Count; index++)
                prompt.Append("- La phrase ").Append(index + 1)
                    .Append(" synthetise uniquement ").Append(evidence[index].EvidenceId)
                    .Append(" et finit par [").Append(evidence[index].EvidenceId).AppendLine("].");
            prompt.AppendLine("- Choisis les informations qui repondent a la demande, sans repeter les informations secondaires des extraits.");
            prompt.AppendLine("- Conserve l'acteur, l'action, l'objet, la relation, la portee et les conditions utiles ; ne transforme pas une observation en obligation et n'ajoute aucun qualificatif non documente.");
            prompt.AppendLine("- Francais uniquement. Aucun titre, numero, label de modalite ou commentaire hors des phrases citees.");
        }
        else
        {
            prompt.AppendLine("FINAL_USER_REQUEST:");
            prompt.AppendLine(userRequest);
            prompt.AppendLine();
            prompt.AppendLine("OUTPUT CONTRACT:");
            prompt.Append("- Answer the request in exactly ").Append(evidence.Count)
                .AppendLine(" sentences, one sentence per line.");
            for (var index = 0; index < evidence.Count; index++)
                prompt.Append("- Sentence ").Append(index + 1)
                    .Append(" summarizes only ").Append(evidence[index].EvidenceId)
                    .Append(" and ends with [").Append(evidence[index].EvidenceId).AppendLine("].");
            prompt.AppendLine("- Select information that answers the request without repeating secondary details from the excerpts.");
            prompt.AppendLine("- Preserve actor, action, object, relation, scope and relevant conditions; never turn an observation into an obligation or add an unsupported qualifier.");
            prompt.AppendLine(NormalizeLanguageCode(responseLanguage) == "en"
                ? "- English only. No title, numbering, modality label or commentary outside the cited sentences."
                : "- Use only target_language. No title, numbering, modality label or commentary outside the cited sentences.");
        }
        prompt.AppendLine();
        prompt.AppendLine("target_language=" + NormalizeLanguageCode(responseLanguage));
        prompt.AppendLine("document=" + document.DocPath);
        prompt.AppendLine("CANONICAL_EVIDENCE:");
        foreach (var item in evidence)
        {
            var sourceStrength = ResolveEvidenceOverviewSourceStrength(
                item.Excerpt);
            prompt.Append('[').Append(item.EvidenceId).Append("] ")
                .Append("source_strength=")
                .Append(sourceStrength)
                .Append("; page=").Append(item.PageStart)
                .Append("; text=")
                .AppendLine(item.Excerpt);
        }

        return prompt.ToString();
    }

    internal static string BuildEvidenceOverviewWriterPromptForTests(
        string userRequest,
        string responseLanguage,
        IReadOnlyList<EvidenceItem> evidence)
        => BuildEvidenceOverviewWriterPrompt(
            userRequest,
            new ResolvedDocRef(
                "doc-1",
                "Normes/Named document.pdf",
                "Named document.pdf",
                null,
                "Normes",
                10,
                "cat_024"),
            responseLanguage,
            Math.Min(DefaultEvidenceOverviewPointCount, evidence.Count),
            evidence);

    private static string BuildEvidenceOverviewCandidateRepairPrompt(
        EvidenceItem evidence,
        string rejectedCandidateText,
        string responseLanguage)
    {
        var sourceStrength = ResolveEvidenceOverviewSourceStrength(
            evidence.Excerpt);
        var french = NormalizeLanguageCode(responseLanguage) == "fr";
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_CANDIDATE_REPAIR");
        prompt.AppendLine(french
            ? "Repare uniquement le candidat rejete ci-dessous."
            : "Repair only the rejected candidate below.");
        prompt.AppendLine("evidence_id=" + evidence.EvidenceId);
        prompt.AppendLine("source_strength=" + sourceStrength);
        prompt.AppendLine("rejected_candidate=" + CompactEvidenceOverviewText(
            rejectedCandidateText,
            500));
        prompt.AppendLine("canonical_source=" + CompactEvidenceOverviewText(
            evidence.Excerpt,
            1400));
        prompt.AppendLine(french
            ? "Retourne un seul enonce factuel autonome directement soutenu, puis le marqueur exact ["
              + evidence.EvidenceId
              + "]. N'utilise doit, obligatoire, exige, requis ou faut que si source_strength=requirement. Preserve le sujet grammatical, l'action, l'objet et les conditions de la source. Aucun commentaire."
            : "Return one self-contained directly supported factual statement followed by the exact marker ["
              + evidence.EvidenceId
              + "]. Use must, required or mandatory only when source_strength=requirement. Preserve the source grammatical subject, action, object and conditions. No commentary.");
        return prompt.ToString();
    }

    private static bool TryReadSingleEvidenceOverviewObligationRejection(
        IReadOnlyList<string> rejections,
        out string evidenceId,
        out string rejectedCandidateText)
    {
        evidenceId = string.Empty;
        rejectedCandidateText = string.Empty;
        if (rejections.Count != 1)
            return false;

        var parts = rejections[0].Split(':', 3);
        if (parts.Length != 3
            || !string.Equals(
                parts[0],
                "unsupported_obligation",
                StringComparison.Ordinal))
        {
            return false;
        }

        evidenceId = parts[1];
        rejectedCandidateText = parts[2];
        return evidenceId.Length > 0 && rejectedCandidateText.Length > 0;
    }

    private static string ReplaceEvidenceOverviewCandidateLine(
        string rawAnswer,
        string evidenceId,
        string repairedCandidate)
    {
        var lines = (rawAnswer ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        var marker = new Regex(
            @"\[?" + Regex.Escape(evidenceId) + @"\]?[\.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var replacement = (repairedCandidate ?? string.Empty).Trim();
        for (var index = 0; index < lines.Length; index++)
        {
            if (marker.IsMatch(lines[index]))
            {
                lines[index] = replacement;
                break;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string BuildEvidenceOverviewCandidateRepairPromptForTests(
        EvidenceItem evidence,
        string rejectedCandidateText,
        string responseLanguage)
        => BuildEvidenceOverviewCandidateRepairPrompt(
            evidence,
            rejectedCandidateText,
            responseLanguage);

    internal static string ReplaceEvidenceOverviewCandidateLineForTests(
        string rawAnswer,
        string evidenceId,
        string repairedCandidate)
        => ReplaceEvidenceOverviewCandidateLine(
            rawAnswer,
            evidenceId,
            repairedCandidate);

    private static EvidenceOverviewCandidateValidation
        ValidateAndRenderEvidenceOverviewCandidates(
            string rawAnswer,
            IReadOnlyList<EvidenceItem> evidence,
            int requestedPointCount,
            string responseLanguage)
    {
        var rejections = new List<string>();
        var valid = new List<EvidenceOverviewValidatedCandidate>();
        var usedEvidenceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var byId = evidence.ToDictionary(
            static item => item.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var lines = (rawAnswer ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        var linePattern = new Regex(
            @"^\s*(?:\d{1,2}[\.)]\s*)?(?<text>.*?)(?:\s*\[(?<evidence>E\d+)\]?|\s+(?<evidence>E\d+)\]?)[\.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = linePattern.Match(line);
            if (match.Success && !byId.ContainsKey(match.Groups["evidence"].Value))
            {
                rejections.Add("unknown_evidence:" + match.Groups["evidence"].Value.ToUpperInvariant());
                continue;
            }
            string evidenceId;
            string text;
            if (match.Success)
            {
                evidenceId = match.Groups["evidence"].Value.ToUpperInvariant();
                text = match.Groups["text"].Value.Trim();
                if (lineIndex >= evidence.Count
                    || !string.Equals(
                        evidenceId,
                        evidence[lineIndex].EvidenceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    rejections.Add(
                        "source_order:line=" + (lineIndex + 1)
                        + ":marker=" + evidenceId);
                    continue;
                }
            }
            else if (lines.Length == evidence.Count)
            {
                evidenceId = evidence[lineIndex].EvidenceId;
                text = Regex.Replace(
                    line,
                    @"^\s*\d{1,2}[\.)]\s*",
                    string.Empty,
                    RegexOptions.CultureInvariant).Trim();
            }
            else
            {
                rejections.Add("format:" + line);
                continue;
            }

            if (text.Length == 0)
            {
                rejections.Add("empty_candidate:" + evidenceId);
                continue;
            }

            if (Regex.IsMatch(
                    text,
                    @"\[?E#\]?",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                rejections.Add(
                    "placeholder_candidate:" + evidenceId + ":" + text);
                continue;
            }

            if (!usedEvidenceIds.Add(evidenceId))
            {
                rejections.Add("duplicate_evidence:" + evidenceId);
                continue;
            }

            var source = byId[evidenceId];
            var strength = ResolveEvidenceOverviewSourceStrength(
                source.Excerpt);
            if (!string.Equals(
                    strength,
                    "requirement",
                    StringComparison.Ordinal)
                && ContainsEvidenceOverviewObligation(text))
            {
                rejections.Add(
                    "unsupported_obligation:" + evidenceId + ":" + text);
                continue;
            }

            valid.Add(new EvidenceOverviewValidatedCandidate(
                evidenceId,
                strength,
                text));
        }

        var selected = valid.Take(requestedPointCount).ToArray();
        var rendered = string.Join(
            Environment.NewLine,
            selected.Select((candidate, index) =>
                $"{index + 1}. [{RenderEvidenceOverviewStrengthLabel(candidate.Strength, responseLanguage)}] {candidate.Text} [{candidate.EvidenceId}]"));
        return new EvidenceOverviewCandidateValidation(
            lines.Length,
            valid.Count,
            selected.Select(static candidate => candidate.EvidenceId)
                .ToArray(),
            rejections,
            rendered,
            valid.ToArray());
    }

    private static EvidenceOverviewCandidateValidation
        SelectEvidenceOverviewCandidates(
            EvidenceOverviewCandidateValidation validation,
            IReadOnlyList<string> selectedEvidenceIds,
            string responseLanguage)
    {
        var selectedSet = selectedEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var selected = validation.ValidCandidates
            .Where(candidate => selectedSet.Contains(candidate.EvidenceId))
            .ToArray();
        var rendered = string.Join(
            Environment.NewLine,
            selected.Select((candidate, index) =>
                $"{index + 1}. [{RenderEvidenceOverviewStrengthLabel(candidate.Strength, responseLanguage)}] {candidate.Text} [{candidate.EvidenceId}]"));
        return new EvidenceOverviewCandidateValidation(
            validation.CandidatePointCount,
            selected.Length,
            selected.Select(static candidate => candidate.EvidenceId)
                .ToArray(),
            validation.Rejections,
            rendered,
            selected);
    }

    private static string ResolveEvidenceOverviewSourceStrength(
        string? sourceText)
    {
        var text = sourceText ?? string.Empty;
        if (Regex.IsMatch(
                text,
                @"\b(?:shall|must|doit|doivent|obligatoire|exig[ée]e?|requis(?:e|es|s)?|debe(?:n|r[aá]n)?|obligatorio|obligatoria|deve|devono|obbligatorio|devem|obrigat[oó]rio|muss|m[uü]ssen|verpflichtet)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "requirement";
        }

        if (Regex.IsMatch(
                text,
                @"\b(?:should|recommended|recommendation|encouraged|devrait|devraient|recommand[ée]e?s?|deber[ií]a(?:n)?|recomendad[oa]s?|dovrebbe|raccomandat[oa]|deveria|recomendad[oa]|sollte|empfohlen)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "recommendation";
        }

        return "context";
    }

    internal static string ResolveEvidenceOverviewSourceStrengthForTests(
        string? sourceText)
        => ResolveEvidenceOverviewSourceStrength(sourceText);

    internal static (
        int ValidCount,
        string[] CitedEvidenceIds,
        string[] Rejections,
        string RenderedAnswer)
        ValidateAndRenderEvidenceOverviewCandidatesForTests(
            string rawAnswer,
            IReadOnlyList<EvidenceItem> evidence,
            int requestedPointCount,
            string responseLanguage)
    {
        var result = ValidateAndRenderEvidenceOverviewCandidates(
            rawAnswer,
            evidence,
            requestedPointCount,
            responseLanguage);
        return (
            result.ValidCandidateCount,
            result.CitedEvidenceIds,
            result.Rejections.ToArray(),
            result.RenderedAnswer);
    }

    private static bool ContainsEvidenceOverviewObligation(string text)
        => Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:shall|must|required|mandatory|doit|doivent|faut|obligatoire|exig[ée]e?|requis(?:e|es|s)?|debe(?:n|r[aá]n)?|obligatorio|obligatoria|deve|devono|obbligatorio|devem|obrigat[oó]rio|muss|m[uü]ssen|verpflichtet)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RenderEvidenceOverviewStrengthLabel(
        string strength,
        string responseLanguage)
        => NormalizeLanguageCode(responseLanguage) switch
        {
            "en" => strength switch
            {
                "requirement" => "Requirement",
                "recommendation" => "Recommendation",
                _ => "Context"
            },
            "es" => strength switch
            {
                "requirement" => "Exigencia",
                "recommendation" => "Recomendación",
                _ => "Contexto"
            },
            "pt" => strength switch
            {
                "requirement" => "Exigência",
                "recommendation" => "Recomendação",
                _ => "Contexto"
            },
            "de" => strength switch
            {
                "requirement" => "Anforderung",
                "recommendation" => "Empfehlung",
                _ => "Kontext"
            },
            "it" => strength switch
            {
                "requirement" => "Requisito",
                "recommendation" => "Raccomandazione",
                _ => "Contesto"
            },
            _ => strength switch
            {
                "requirement" => "Exigence",
                "recommendation" => "Recommandation",
                _ => "Contexte"
            }
        };

    private static object BuildEvidenceOverviewSourcePayload(
        EvidenceItem item)
        => new
        {
            evidenceId = item.EvidenceId,
            docId = item.DocId,
            docPath = item.DocPath,
            docName = item.DocName,
            pageStart = item.PageStart,
            pageEnd = item.PageEnd,
            label = item.DocName ?? item.DocPath ?? item.DocId ?? item.EvidenceId,
            sourceHash = item.SourceHash,
            revisionId = item.RevisionId,
            docLanguage = item.DocLanguage,
            profileLanguage = item.ProfileLanguage,
            categoryPath = item.CategoryPath,
            chunkId = item.ChunkId,
            anchorId = item.AnchorId,
            contentCardId = item.ContentCardId,
            contentRole = item.SourceKind,
            sourceKind = item.SourceKind
        };

    private static int? TryExtractEvidenceOverviewPointCount(
        string? userRequest)
    {
        var match = Regex.Match(
            userRequest ?? string.Empty,
            @"\b(?<count>\d{1,2})\s+(?:points?|items?|elements?|[ée]l[ée]ments?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : null;
    }

    private static string CompactEvidenceOverviewText(
        string? value,
        int maxCharacters)
    {
        var compact = Regex.Replace(
                value ?? string.Empty,
                @"\s+",
                " ")
            .Trim();
        return compact.Length <= maxCharacters
            ? compact
            : compact[..maxCharacters].TrimEnd() + "...";
    }

    private static string EvidenceOverviewReadString(
        JsonElement value,
        string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int EvidenceOverviewReadInt(
        JsonElement value,
        string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private sealed record EvidenceOverviewContextSelection(
        int TargetPage,
        JsonElement Document,
        JsonElement Item,
        long ElapsedMs);

    private sealed record EvidenceOverviewValidatedCandidate(
        string EvidenceId,
        string Strength,
        string Text);

    private sealed record EvidenceOverviewCandidateValidation(
        int CandidatePointCount,
        int ValidCandidateCount,
        string[] CitedEvidenceIds,
        IReadOnlyList<string> Rejections,
        string RenderedAnswer,
        IReadOnlyList<EvidenceOverviewValidatedCandidate> ValidCandidates);
}
