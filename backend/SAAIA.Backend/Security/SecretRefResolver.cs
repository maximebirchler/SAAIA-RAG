namespace SAAIA.Backend.Security;

/// <summary>
/// Resolves secrets referenced by a signed policy/config.
/// 
/// Goal: keep POLICIES (licence/security flags) in the signed config,
/// while allowing OPERATIONS to inject secrets via env/files without modifying the signed file.
///
/// Supported references:
/// - "ENV:NAME"  -> Environment.GetEnvironmentVariable("NAME")
/// - "FILE:/path/to/secret" -> reads file content (UTF-8), trimmed
///   (relative file paths are resolved from contentRoot)
/// </summary>
public static class SecretRefResolver
{
    public static string? Resolve(string? explicitValue, string? reference, string contentRoot, out string? source)
    {
        source = null;

        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            source = "config";
            return explicitValue.Trim();
        }

        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var r = reference.Trim();

        if (r.StartsWith("ENV:", StringComparison.OrdinalIgnoreCase))
        {
            var name = r[4..].Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;
            source = $"env:{name}";
            return Environment.GetEnvironmentVariable(name)?.Trim();
        }

        if (r.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
        {
            var path = r[5..].Trim();
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Resolve relative FILE paths from contentRoot.
            var full = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(contentRoot, path));

            source = $"file:{path}";
            try
            {
                return File.ReadAllText(full).Trim();
            }
            catch
            {
                return null;
            }
        }

        // Unknown ref format
        source = "ref:unknown";
        return null;
    }
}
