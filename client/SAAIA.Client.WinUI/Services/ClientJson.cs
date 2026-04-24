using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

internal static class ClientJson
{
    internal static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
