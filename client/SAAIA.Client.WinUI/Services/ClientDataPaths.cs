using System;
using System.IO;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// One per-process client profile root. An explicit absolute root also isolates
/// packaged settings and provisioning; it never falls back to the Windows profile.
/// Set before launching the client, not while a client process is running.
/// </summary>
internal static class ClientDataPaths
{
    internal const string RootEnvironmentVariable = "SAAIA_CLIENT_DATA_ROOT";

    internal static bool IsIsolated => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(RootEnvironmentVariable));

    internal static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA");
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException($"{RootEnvironmentVariable} must be an absolute path.");
            return Path.GetFullPath(configured);
        }
    }

    // An explicit profile must not read or migrate another packaged user's values.
    internal static bool UsePackagedSettings => !IsIsolated;
}
