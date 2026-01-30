using Microsoft.AspNetCore.Builder;

namespace SAAIA.Backend.Chat;

/// <summary>
/// Compat shim: Program.cs appelle app.MapChatEndpoints().
/// </summary>
public static class ChatEndpointsExtensions
{
    public static void MapChatEndpoints(this WebApplication app)
        => ChatStreamEndpoint.Map(app);
}
