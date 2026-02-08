namespace SAAIA.Backend.Models;

/// <summary>
/// Réponse d'erreur uniforme pour tous les endpoints.
/// Format : { error, requestId }
/// </summary>
public sealed class ErrorResponse
{
    public string Error { get; set; }
    public string RequestId { get; set; }

    public ErrorResponse(string error, string requestId)
    {
        Error = error;
        RequestId = requestId;
    }
}
