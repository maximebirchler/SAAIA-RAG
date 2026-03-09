using Microsoft.AspNetCore.Builder;

namespace SAAIA.Backend.Auth;

/// <summary>
/// Marker metadata used by <see cref="ApiKeyAuthMiddleware"/> to decide whether an endpoint must
/// be authenticated via X-Admin-Key.
/// </summary>
public sealed class RequireAdminKeyMetadata
{
}

public static class RouteHandlerBuilderAdminKeyExtensions
{
    /// <summary>
    /// Marks an endpoint as admin-only for the purpose of header selection.
    /// Note: you should still call <see cref="AdminAuth.EnsureAdmin"/> inside the handler.
    /// </summary>
    public static RouteHandlerBuilder RequireAdminKey(this RouteHandlerBuilder b)
        => b.WithMetadata(new RequireAdminKeyMetadata());
}
