namespace SAAIA.Client.WinUI.Services.ToolAgent;

/// <summary>Created only at the local model transport boundary, never from backend failures.</summary>
internal sealed class LocalLlmUnavailableException(HttpRequestException innerException)
    : HttpRequestException("The configured local model service is unavailable.", innerException, innerException.StatusCode)
{
    internal const string ReasonCode = "local_model_unavailable";
}
