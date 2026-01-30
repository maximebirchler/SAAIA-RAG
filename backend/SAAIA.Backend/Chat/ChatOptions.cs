namespace SAAIA.Backend.Chat;

public sealed class ChatOptions
{
    // LLM
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string LlmModel { get; set; } = "mistralai/Mistral-7B-Instruct-v0.3";
    public string? LlmApiKey { get; set; }

    // Ollama fallback (optionnel)
    public bool EnableOllamaFallback { get; set; } = false;
    public string? OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string? OllamaModel { get; set; } = "mistralai/Mistral-7B-Instruct-v0.3";

    // Timeouts (gérés via CancellationToken côté endpoint)
    public int LlmTimeoutSeconds { get; set; } = 120;
    public int LlmFirstTokenTimeoutSeconds { get; set; } = 25;

    // Génération
    public double LlmTemperature { get; set; } = 0.2;
    public int LlmMaxTokens { get; set; } = 256;

    // Streaming UI
    public int StreamFlushChars { get; set; } = 128;
    public int PingIntervalSeconds { get; set; } = 5;

    // Prompt sizing
    public int MaxContextChars { get; set; } = 6000;
    public int PromptMaxSources { get; set; } = 4;
    public int PromptMaxCharsPerSource { get; set; } = 450;

    // ==================
    // Retrieval (sélection des sources)
    // ==================

    /// <summary>
    /// On récupère plus que topK (candidateK = topK * multiplier) puis on déduplique
    /// par similarité. Ça réduit les doublons dus à l'overlap sans bloquer des chunks adjacents
    /// qui contiennent des infos différentes.
    /// </summary>
    public int RetrievalCandidateMultiplier { get; set; } = 4;

    /// <summary>
    /// Taille des shingles (mots) pour la similarité Jaccard.
    /// 5 mots donne un bon compromis sur des chunks techniques.
    /// </summary>
    public int RetrievalShingleWords { get; set; } = 5;

    /// <summary>
    /// Seuil de similarité Jaccard au-dessus duquel un chunk est considéré quasi-doublon.
    /// 0.85 = on ne retire que les passages très redondants.
    /// </summary>
    public double RetrievalNearDuplicateJaccard { get; set; } = 0.85;

    /// <summary>
    /// Si bestScore < Low => on considère que les sources sont probablement hors-sujet.
    /// </summary>
    public double RetrievalLowConfidenceScore { get; set; } = 0.72;

    /// <summary>
    /// Si Low <= bestScore < Ok => pertinence incertaine (warning).
    /// Si bestScore >= Ok => OK.
    /// </summary>
    public double RetrievalOkScore { get; set; } = 0.78;

    // Réponse (contraintes)
    public int ResponseMaxWords { get; set; } = 160;
    public int ResponseMaxSentences { get; set; } = 10;
    public int ResponseMaxWordsPerSentence { get; set; } = 22;

    // Citations
    public bool AppendCitationsAtEnd { get; set; } = true;

    // Fallback retrieval-only (quand LLM timeout)
    public int FallbackMaxSources { get; set; } = 3;
    public int FallbackMaxCharsPerSource { get; set; } = 900;

    // Limiteur
    public int MaxConcurrentGenerations { get; set; } = 2;
    public int MaxQueueLength { get; set; } = 10;
}
