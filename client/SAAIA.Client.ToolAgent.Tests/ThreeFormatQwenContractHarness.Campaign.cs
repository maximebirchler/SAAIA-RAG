using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.ToolAgent.Tests;

public static partial class ThreeFormatQwenContractHarness
{
    public sealed record RunOutcome(ParsedAction Controller, ParsedAction? Reviewer, string? PublishedText,
        bool SourceContractValid, bool ProtocolValid, bool ProvenanceValid, int ControllerInputTokens,
        int? ReviewerInputTokens, long Milliseconds, int ModelCalls, bool Fallback = false,
        bool ForbiddenEndpoint = false, bool EnvironmentDrift = false, bool RuntimeHealthy = true);
    public sealed record ProtocolRejection(string Stage, string Code, string ModelPayload,
        int InputTokens, long Milliseconds, int ModelCalls, ParsedAction? PartialController = null);
    public sealed class ProtocolRejectedException(ProtocolRejection rejection) : Exception(rejection.Code)
    {
        public ProtocolRejection Rejection { get; } = rejection;
    }
    public sealed record RunRecord(ScheduleItem Scheduled, RunOutcome? Outcome, bool Strict,
        IReadOnlyList<string> FatalReasons, ProtocolRejection? ProtocolFailure = null);
    public sealed record CampaignResult(IReadOnlyList<RunRecord> Runs, IReadOnlyList<string> DisqualifiedFamilies, bool RuntimeHealthy);
    public sealed record BlindPacket(string PublicJson, string PrivateKeyJson, string PacketSha256, string KeySha256);

    public static void WriteCheckpointAtomically(string path, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void AppendJsonLine(string path, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value) + "\n");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            Require(stream.ReadByte() == '\n', "jsonl_existing_tail_incomplete");
        }
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static async Task<CampaignResult> RunFailFast(FrozenInputs inputs, IReadOnlySet<string> eligibleFamilies,
        string officialDirectory, Func<ScheduleItem, ModelState, CancellationToken, Task<RunOutcome>> execute, CancellationToken ct = default)
    {
        Require(eligibleFamilies.Count > 0 && eligibleFamilies.All(Families.Contains), "eligible_families");
        Require(!Directory.Exists(officialDirectory) && !File.Exists(officialDirectory), "official_directory_already_exists");
        Directory.CreateDirectory(officialDirectory);
        using (var claim = new FileStream(Path.Combine(officialDirectory, ".official-run"), FileMode.CreateNew)) { }
        var disqualified = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<RunRecord>();
        var healthy = true;
        WriteCheckpointAtomically(Path.Combine(officialDirectory, "checkpoint.json"), new CampaignResult(records, [], healthy));
        foreach (var item in inputs.Schedule.Where(s => eligibleFamilies.Contains(s.Variant)))
        {
            if (disqualified.Contains(item.Variant)) continue;
            ct.ThrowIfCancellationRequested();
            var state = inputs.States.Single(s => s.CasePosition == item.CasePosition);
            RunOutcome? outcome = null;
            var fatal = new List<string>();
            var strict = false;
            ProtocolRejection? protocolFailure = null;
            try
            {
                outcome = await execute(item, state, ct).ConfigureAwait(false);
                CheckParsed(inputs, state, outcome.Controller, "controller");
                if (outcome.Reviewer is not null) CheckParsed(inputs, state, outcome.Reviewer, "reviewer");
                var published = !string.IsNullOrEmpty(outcome.PublishedText);
                var answer = outcome.Controller.Action == "answer";
                if (outcome.Fallback) fatal.Add("interface_fallback");
                if (outcome.ForbiddenEndpoint) fatal.Add("forbidden_endpoint");
                if (outcome.EnvironmentDrift) fatal.Add("environment_drift");
                if (!outcome.ProvenanceValid) fatal.Add("invalid_provenance");
                if (published && (!answer || outcome.Reviewer?.Action != "accept" || !outcome.SourceContractValid)) fatal.Add("publication_without_valid_review");
                if (!answer && outcome.Reviewer is not null) fatal.Add("reviewer_without_draft");
                var expectedCalls = (item.Variant == Routed ? 2 : 1) * (outcome.Reviewer is null ? 1 : 2);
                if (outcome.ModelCalls != expectedCalls) fatal.Add("call_count_or_retry");
                if (outcome.ControllerInputTokens <= 0 || outcome.ControllerInputTokens > 3392
                    || outcome.ReviewerInputTokens is <= 0 or > 3648
                    || outcome.Reviewer is not null && outcome.ReviewerInputTokens is null) fatal.Add("context_budget");
                if (outcome.Milliseconds < 0 || outcome.ModelCalls <= 0) fatal.Add("missing_measurements");
                if (!outcome.RuntimeHealthy || outcome.EnvironmentDrift) { fatal.Add("runtime_unhealthy_or_drifted"); healthy = false; }
                strict = IsStrict(inputs.ScoringCases[item.CasePosition], outcome);
                if (answer && !strict && inputs.ScoringCases[item.CasePosition].GetProperty("isDangerous").GetBoolean()) fatal.Add("dangerous_false_answer");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ProtocolRejectedException ex)
            {
                protocolFailure = ex.Rejection;
                // Invalid model syntax/schema is an ordinary non-strict sample.
                // Identity, transport and runtime failures must never use this path.
                if (!IsOrdinaryProtocolRejection(protocolFailure.Code)) fatal.Add("misclassified_protocol_rejection");
                if (protocolFailure.InputTokens <= 0 || protocolFailure.InputTokens >
                    (protocolFailure.Stage.StartsWith("reviewer", StringComparison.Ordinal) ? 3648 : 3392)) fatal.Add("context_budget");
                if (protocolFailure.ModelCalls <= 0 || protocolFailure.Milliseconds < 0) fatal.Add("missing_measurements");
                if (protocolFailure.PartialController is { } partial)
                {
                    CheckParsed(inputs, state, partial, "controller");
                    if (partial.Action == "answer" && Text(inputs.ScoringCases[item.CasePosition], "expectedDecision") != "answer"
                        && inputs.ScoringCases[item.CasePosition].GetProperty("isDangerous").GetBoolean()) fatal.Add("dangerous_false_answer");
                }
            }
            catch (Exception ex)
            {
                // Preserve incident text in the private ledger, never in the blind packet.
                fatal.Add("execution_exception:" + ex.GetType().Name + ":" + ex.Message);
            }
            if (fatal.Count > 0) { disqualified.Add(item.Variant); strict = false; }
            var record = new RunRecord(item, outcome, strict, fatal, protocolFailure);
            records.Add(record);
            AppendJsonLine(Path.Combine(officialDirectory, "runs.jsonl"), record);
            WriteCheckpointAtomically(Path.Combine(officialDirectory, "checkpoint.json"), new CampaignResult(records, disqualified.Order().ToArray(), healthy));
            if (!healthy) break;
        }
        return new(records, disqualified.Order().ToArray(), healthy);
    }

    public static bool IsOrdinaryProtocolRejection(string code)
        => code.StartsWith("A657_schema_", StringComparison.Ordinal)
           || code is "invalid_json" or "missing_response_property" or "invalid_response_type"
               or "A657_native_finish_reason" or "A657_native_content_fallback"
               or "A657_native_exactly_one_call" or "A657_native_call_identity"
               or "A657_unknown_or_wrong_role_tool" or "A657_route_tool"
               or "A657_route_payload_mismatch" or "A657_exactly_one_choice"
               or "A657_assistant_role" or "A657_duplicate_json_property";

    private static void CheckParsed(FrozenInputs inputs, ModelState state, ParsedAction parsed, string role)
    {
        Require(parsed.Role == role, "parsed_role_mismatch");
        var expected = ValidateAction(inputs, state, role, parsed.Action, parsed.Arguments);
        Require(expected.Tool == parsed.Tool && SameJson(expected.Arguments, parsed.Arguments), "parsed_action_drift");
    }
    public static bool ScoreStrictCase(JsonElement oracle, RunOutcome outcome) => IsStrict(oracle, outcome);

    private static bool IsStrict(JsonElement oracle, RunOutcome outcome)
    {
        if (!outcome.ProtocolValid || !outcome.ProvenanceValid || outcome.Fallback) return false;
        var action = outcome.Controller.Action;
        var normalized = action is "search" or "navigation" or "content_cards" ? "research" : action;
        if (Text(oracle, "expectedDecision") != normalized) return false;
        if (action != "answer") return string.IsNullOrEmpty(outcome.PublishedText) && outcome.Reviewer is null;
        if (outcome.Reviewer?.Action != "accept" || !outcome.SourceContractValid || string.IsNullOrWhiteSpace(outcome.PublishedText)) return false;
        var ids = outcome.Controller.Arguments.GetProperty("claims").EnumerateArray()
            .SelectMany(c => c.GetProperty("evidenceIds").EnumerateArray().Select(e => e.GetString()!)).ToHashSet(StringComparer.Ordinal);
        var expected = oracle.GetProperty("expectedEvidenceIds").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        return ids.SetEquals(expected) && oracle.GetProperty("requiredPatterns").EnumerateArray()
            .All(p => Regex.IsMatch(outcome.PublishedText, p.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    public static BlindPacket BuildBlindPacket(FrozenInputs inputs, IReadOnlyList<RunRecord> records, string privateSalt, bool includeAll = false)
    {
        Require(privateSalt.Length >= 16, "blind_salt_too_short");
        var disagreementCases = records.GroupBy(r => r.Scheduled.CasePosition)
            .Where(group => group.Select(r => Serialize(new { r.Strict, r.Outcome?.ProtocolValid, r.Outcome?.ProvenanceValid,
                r.Outcome?.SourceContractValid, result = PublicResult(r) })).Distinct(StringComparer.Ordinal).Count() > 1).Select(g => g.Key).ToHashSet();
        var selected = records.Where(r => includeAll || !r.Strict || disagreementCases.Contains(r.Scheduled.CasePosition)).ToArray();
        var packets = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var key = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var record in selected)
        {
            var position = record.Scheduled.CasePosition;
            var state = inputs.States.Single(s => s.CasePosition == position);
            var blindId = Hash(privateSalt + "|" + record.Scheduled.Variant + "|" + position);
            Require(!packets.ContainsKey(blindId), "duplicate_blind_run");
            var oracle = inputs.ScoringCases[position];
            packets.Add(blindId, new
            {
                blindId, question = Text(ParseJson(state.Json), "question"),
                evidence = state.Evidence.Select(e => new { e.Id, e.Excerpt, e.PageStart, e.PageEnd }),
                result = ParseJson(PublicResult(record)),
                oracle = Project(oracle, ["expectedDecision", "expectedEvidenceIds", "requiredPatterns"])
            });
            key.Add(blindId, new { record.Scheduled.Variant, record.Scheduled.CasePosition, record.Scheduled.Sequence });
        }
        var publicJson = Serialize(packets.Values.ToArray());
        var keyJson = Serialize(key);
        return new(publicJson, keyJson, Hash(publicJson), Hash(keyJson));
    }
    private static string PublicResult(RunRecord record)
    {
        if (record.ProtocolFailure is { } rejected) return Serialize(new
        {
            decision = "protocol_invalid", publishedText = (string?)null,
            modelPayload = rejected.ModelPayload,
            draft = rejected.PartialController?.Arguments,
            claims = rejected.PartialController is { Action: "answer" } partial
                ? partial.Arguments.GetProperty("claims") : ParseJson("[]")
        });
        if (record.Outcome is not { } outcome) return Serialize(new { decision = "execution_failed", publishedText = (string?)null, claims = Array.Empty<object>() });
        return Serialize(new
        {
            decision = outcome.Controller.Action, publishedText = outcome.PublishedText,
            // Retain the actual search/context payload for the auditor, stripping path-bearing scope fields.
            arguments = outcome.Controller.Arguments.EnumerateObject()
                .Where(p => p.Name is not ("docPath" or "path" or "categoryPath" or "docRef" or "categoryRef"))
                .ToDictionary(p => p.Name, p => p.Value),
            claims = outcome.Controller.Arguments.TryGetProperty("claims", out var claims) ? claims : ParseJson("[]"),
            clarification = outcome.Controller.Action == "clarify" ? Text(outcome.Controller.Arguments, "message") : null,
            missingFacts = outcome.Controller.Action == "insufficient" ? outcome.Controller.Arguments.GetProperty("missingFacts") : ParseJson("[]")
        });
    }
}
