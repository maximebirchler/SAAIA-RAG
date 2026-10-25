namespace SAAIA.Client.WinUI.Services;

internal static class ClientConnectionPolicy
{
    internal static bool ShouldReconnectAfterConfiguration(bool wasConnected, bool autoConnect, bool requiresSetup)
        => !requiresSetup && (wasConnected || autoConnect);
}
