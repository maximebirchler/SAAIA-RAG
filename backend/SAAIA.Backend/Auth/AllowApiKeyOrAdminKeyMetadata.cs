using Microsoft.AspNetCore.Builder;

namespace SAAIA.Backend.Auth;

/// <summary>
/// Marker metadata used by <see cref="ApiKeyAuthMiddleware"/> to accept either X-Api-Key or X-Admin-Key
/// on selected non-admin endpoints.
/// </summary>
public sealed class AllowApiKeyOrAdminKeyMetadata
{
}

public static class RouteHandlerBuilderApiKeyOrAdminKeyExtensions
{
    public static RouteHandlerBuilder AllowApiKeyOrAdminKey(this RouteHandlerBuilder b)
        => b.WithMetadata(new AllowApiKeyOrAdminKeyMetadata());
}
