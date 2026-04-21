namespace SAAIA.Backend;

sealed class ChatOptions
{
    public string? LlmBaseUrl { get; set; }
    public string LlmModel { get; set; } = "local";
    public double LlmTemperature { get; set; } = 0.2;
    public int LlmMaxTokens { get; set; } = 800;
    public int LlmTimeoutSeconds { get; set; } = 120;
    public int LlmFirstTokenTimeoutSeconds { get; set; } = 25;
    public string SystemPrompt { get; set; } = "Tu es un assistant technique. Cite toujours la source avec doc et page.";
}
