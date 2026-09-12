namespace SAAIA.Backend;

sealed class AdvancedAnalysisOptions
{
    public string Provider { get; set; } = "disabled";

    public string ProviderKey { get; set; } = string.Empty;

    public string LlmLocation { get; set; } = "internal";

    public string LlmBaseUrl { get; set; } = string.Empty;

    public string LlmModel { get; set; } = string.Empty;

    public string? LlmApiKeyRef { get; set; }

    public string ReasoningEffort { get; set; } = "low";

    public int LlmTimeoutSeconds { get; set; } = 600;

    public int LlmMaximumHttpAttempts { get; set; } = 3;

    public int LlmRetryBaseDelayMilliseconds { get; set; } = 15_000;

    public int LlmMaximumRetryDelayMilliseconds { get; set; } = 60_000;

    public int PlannerMaxTokens { get; set; } = 512;

    public int WriterMaxTokens { get; set; } = 4_096;

    public bool SemanticCriticEnabled { get; set; } = true;

    public int CriticMaxTokens { get; set; } = 4_096;

    public int MaximumPlanQueries { get; set; } = 8;

    public bool AdaptiveResearchEnabled { get; set; } = true;

    public int MaximumEvidencePromptCharacters { get; set; } = 14_000;

    public decimal ExternalBudgetAuthorizedUsd { get; set; } = 25m;

    public decimal ExternalBudgetSoftLimitUsd { get; set; } = 20m;

    public decimal ExternalBudgetHardLimitUsd { get; set; } = 24m;

    public decimal ExternalMaximumCostPerJobUsd { get; set; } = 0.50m;

    public int ExternalMaximumCallsPerJob { get; set; } = 4;

    public decimal ExternalInputUsdPerMillionTokens { get; set; } = 2m;

    public decimal ExternalCachedInputUsdPerMillionTokens { get; set; } = 0.20m;

    public decimal ExternalOutputUsdPerMillionTokens { get; set; } = 12m;

    public string ExternalUsageLedgerPath { get; set; } =
        "data/advanced-analysis/openai-terra-usage.jsonl";

    public int RetentionDays { get; set; } = 30;

    public int RetentionSweepMilliseconds { get; set; } = 300_000;

    public int RetentionDeleteBatchSize { get; set; } = 1_000;

    public int MaximumQueuedJobsPerUser { get; set; } = 20;

    public bool WorkerEnabled { get; set; }

    public int PollDelayMilliseconds { get; set; } = 1_000;

    public int LeaseSeconds { get; set; } = 120;

    public int HeartbeatMilliseconds { get; set; } = 5_000;

    public int RetryDelayMilliseconds { get; set; } = 5_000;

    public int MaximumJobRetryDelayMilliseconds { get; set; } = 86_400_000;

    public int MaximumAttempts { get; set; } = 3;

    public int MaximumEvidenceCharactersPerItem { get; set; } = 24_000;

    public int MaximumEvidenceCharactersTotal { get; set; } = 256_000;

    public bool AllowExternalProviderContent { get; set; }

    public bool AllowExternalProviderMetadata { get; set; }

    public int MaximumToolCalls { get; set; } = 32;

    public int MaximumSearchTopK { get; set; } = 60;

    public int MaximumAccumulatedEvidenceItems { get; set; } = 512;

    public long MaximumToolElapsedMilliseconds { get; set; } = 300_000;
}
