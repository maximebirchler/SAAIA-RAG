using System.Globalization;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedAgentV2Options(
    int MaximumTurns,
    int MaximumToolCalls,
    int MaximumObservationItems,
    int MaximumObservationExcerptCharacters,
    int MaximumOutputTokens,
    int MaximumActionTokens = 480,
    int MaximumWorkingEvidenceItems = 40,
    int MaximumWorkingExcerptCharacters = 240,
    int MaximumPlanningTokens = 320,
    double PlanningTemperature = 0.2,
    int MaximumSemanticReviewTokens = 640,
    double SemanticReviewTemperature = 0.0,
    bool SeparateActionAndWriter = true,
    int MaximumSemanticCorrectionTurns = 2,
    bool SemanticCandidateAuditEnabled = true,
    int MaximumContextTokens = 4096,
    bool SemanticColumnRoleReviewEnabled = true,
    int MaximumSemanticColumnRoleTokens = 320,
    int MaximumSelectionProtocolRepairTurns = 2,
    bool StructuredSemanticPlanningEnabled = true,
    bool RequireEvidenceSelectionBeforeWriter = false,
    int MaximumSemanticCandidatesPerAuditTurn = 40,
    int MaximumSemanticCandidateAuditConcurrency = 1,
    int MaximumFlatStructuredSelectionItems = int.MaxValue,
    bool SemanticCandidateStrategyEnabled = true,
    bool SemanticCandidateDefinitionEnabled = false,
    bool SemanticCandidateLabelResolutionEnabled = false,
    int MaximumSemanticCandidatesPerAuditBatch = 6,
    bool StructuredFlatWriterEnabled = false,
    bool SemanticAnswerTransactionEnabled = false,
    bool SemanticResolutionWriterReviewEnabled = false,
    int MaximumCumulativeLlmTokens = 12_000,
    int MaximumCumulativeLlmElapsedMilliseconds = 240_000,
    int CumulativeLlmTerminalReserveTokens = 4_800,
    int CumulativeLlmTerminalReserveMilliseconds = 60_000)
{
    public static SourceBackedAgentV2Options ResolveFromEnvironment(
        int? providerContextTokens = null,
        bool advancedCapacity = false)
    {
        var defaultContextTokens = advancedCapacity && providerContextTokens is > 0
            ? Math.Clamp(providerContextTokens.Value, 2_048, 262_144)
            : 4_096;
        var defaultCumulativeTokens = advancedCapacity ? 48_000 : 12_000;
        var defaultCumulativeMilliseconds = advancedCapacity ? 600_000 : 240_000;
        var defaultTerminalReserveTokens = advancedCapacity ? 8_000 : 4_800;
        var defaultTerminalReserveMilliseconds = advancedCapacity ? 120_000 : 60_000;

        return new(
            MaximumTurns: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_TURNS", 10, 2, 20),
            MaximumToolCalls: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_TOOL_CALLS", 24, 1, 64),
            MaximumObservationItems: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OBSERVATION_ITEMS", 10, 1, 24),
            MaximumObservationExcerptCharacters: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_EXCERPT_CHARS", 180, 80, 1200),
            MaximumOutputTokens: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_OUTPUT_TOKENS", 900, 256, 4096),
            MaximumActionTokens: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_ACTION_TOKENS", 480, 96, 800),
            MaximumWorkingEvidenceItems: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_WORKING_EVIDENCE", 40, 8, 80),
            MaximumWorkingExcerptCharacters: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_WORKING_EXCERPT_CHARS",
                240,
                40,
                1200),
            MaximumPlanningTokens: ReadInt("SAAIA_SOURCE_BACKED_AGENT_V2_MAX_PLANNING_TOKENS", 320, 128, 1200),
            PlanningTemperature: ReadDouble(
                "SAAIA_SOURCE_BACKED_AGENT_V2_PLANNING_TEMPERATURE",
                0.2,
                0,
                2),
            MaximumSemanticReviewTokens: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_SEMANTIC_REVIEW_TOKENS",
                640,
                128,
                1200),
            SemanticReviewTemperature: ReadDouble(
                "SAAIA_SOURCE_BACKED_AGENT_V2_SEMANTIC_REVIEW_TEMPERATURE",
                0,
                0,
                2),
            SeparateActionAndWriter: true,
            MaximumSemanticCorrectionTurns: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_SEMANTIC_CORRECTION_TURNS",
                6,
                0,
                6),
            SemanticCandidateAuditEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT",
                defaultValue: true),
            MaximumContextTokens: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CONTEXT_TOKENS",
                defaultContextTokens,
                2048,
                262144),
            SemanticColumnRoleReviewEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_COLUMN_ROLE_REVIEW",
                defaultValue: true),
            MaximumSemanticColumnRoleTokens: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_COLUMN_ROLE_TOKENS",
                320,
                128,
                800),
            MaximumSelectionProtocolRepairTurns: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_SELECTION_PROTOCOL_REPAIR_TURNS",
                2,
                0,
                4),
            StructuredSemanticPlanningEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_STRUCTURED_PLANNING",
                defaultValue: true),
            RequireEvidenceSelectionBeforeWriter: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_REQUIRE_EVIDENCE_SELECTION",
                defaultValue: true),
            MaximumSemanticCandidatesPerAuditTurn: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATES_PER_AUDIT_TURN",
                40,
                8,
                80),
            MaximumSemanticCandidateAuditConcurrency: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_AUDIT_CONCURRENCY",
                1,
                1,
                4),
            MaximumFlatStructuredSelectionItems: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_FLAT_SELECTION_ITEMS",
                8,
                1,
                80),
            SemanticCandidateStrategyEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_STRATEGY",
                defaultValue: false),
            SemanticCandidateDefinitionEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_DEFINITION",
                defaultValue: true),
            SemanticCandidateLabelResolutionEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATE_LABEL_RESOLUTION",
                defaultValue: true),
            MaximumSemanticCandidatesPerAuditBatch: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_CANDIDATES_PER_AUDIT_BATCH",
                6,
                1,
                80),
            StructuredFlatWriterEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_STRUCTURED_FLAT_WRITER",
                defaultValue: true),
            SemanticAnswerTransactionEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_SEMANTIC_ANSWER_TRANSACTION",
                defaultValue: false),
            SemanticResolutionWriterReviewEnabled: ReadBool(
                "SAAIA_SOURCE_BACKED_AGENT_V2_SEMANTIC_RESOLUTION_WRITER_REVIEW",
                defaultValue: false),
            MaximumCumulativeLlmTokens: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_TOKENS",
                defaultCumulativeTokens,
                8_000,
                48_000),
            MaximumCumulativeLlmElapsedMilliseconds: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_ELAPSED_MS",
                defaultCumulativeMilliseconds,
                60_000,
                1_800_000),
            CumulativeLlmTerminalReserveTokens: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_TERMINAL_RESERVE_TOKENS",
                defaultTerminalReserveTokens,
                512,
                16_000),
            CumulativeLlmTerminalReserveMilliseconds: ReadInt(
                "SAAIA_SOURCE_BACKED_AGENT_V2_TERMINAL_RESERVE_MS",
                defaultTerminalReserveMilliseconds,
                5_000,
                300_000));
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return raw.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "enabled" => true,
            "0" or "false" or "no" or "off" or "disabled" => false,
            _ => throw new InvalidOperationException($"{name} must be a boolean value.")
        };
    }

    private static int ReadInt(string name, int defaultValue, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException($"{name} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    private static double ReadDouble(string name, double defaultValue, double minimum, double maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException($"{name} must be a number between {minimum} and {maximum}.");
        }

        return value;
    }
}
