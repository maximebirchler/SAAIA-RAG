namespace SAAIA.Backend.Auth;

public static class AdminAuth
{
    public static void EnsureAdmin(HttpContext ctx)
    {
        if (!ctx.Items.TryGetValue(ApiKeyAuth.IsAdminItemKey, out var v) || v is not bool b || !b)
            throw new UnauthorizedAccessException("Admin API key required.");
    }
}
