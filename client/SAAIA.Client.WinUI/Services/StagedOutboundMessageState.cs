namespace SAAIA.Client.WinUI.Services;

internal static class StagedOutboundMessageState
{
    internal readonly record struct Resolution(bool UsePending, string EffectiveWireText, string EffectiveDisplayText);

    internal static Resolution ResolveForSend(string? pendingWireText, string? pendingDisplayText, string? currentVisibleText)
    {
        var current = Normalize(currentVisibleText);
        var pendingWire = Normalize(pendingWireText);
        var pendingDisplay = Normalize(pendingDisplayText);

        var usePending = current.Length > 0
            && ((pendingDisplay.Length > 0 && string.Equals(current, pendingDisplay, StringComparison.Ordinal))
                || (pendingWire.Length > 0 && string.Equals(current, pendingWire, StringComparison.Ordinal)));

        if (!usePending)
            return new Resolution(false, current, current);

        var effectiveWire = pendingWire.Length > 0 ? pendingWire : current;
        var effectiveDisplay = pendingDisplay.Length > 0 ? pendingDisplay : effectiveWire;
        return new Resolution(true, effectiveWire, effectiveDisplay);
    }

    internal static bool ShouldRetainPendingAfterTextChange(string? pendingWireText, string? pendingDisplayText, string? currentVisibleText)
        => ResolveForSend(pendingWireText, pendingDisplayText, currentVisibleText).UsePending;

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
