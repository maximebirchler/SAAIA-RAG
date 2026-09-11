using System;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int NativeRouterFallbackTimeoutMs = 90_000;
    private const int NativeRouterMinimumTimeoutMs = 45_000;
    private const int NativeRouterMaximumTimeoutMs = 180_000;
    private const int NativeRouterFixedLatencyAllowanceMs = 10_000;
    private const int NativeRouterMillisecondsPerInputToken = 15;
    private const int NativeRouterMillisecondsPerOutputToken = 225;

    private static int ResolveNativeRouterTimeoutMs(
        int? inputTokens,
        int maximumOutputTokens)
    {
        if (inputTokens is not > 0)
            return NativeRouterFallbackTimeoutMs;

        var estimatedTimeoutMs =
            NativeRouterFixedLatencyAllowanceMs
            + ((long)inputTokens.Value
               * NativeRouterMillisecondsPerInputToken)
            + ((long)Math.Max(0, maximumOutputTokens)
               * NativeRouterMillisecondsPerOutputToken);
        return (int)Math.Clamp(
            estimatedTimeoutMs,
            NativeRouterMinimumTimeoutMs,
            NativeRouterMaximumTimeoutMs);
    }

    internal static int ResolveNativeRouterTimeoutMsForTests(
        int? inputTokens,
        int maximumOutputTokens)
        => ResolveNativeRouterTimeoutMs(
            inputTokens,
            maximumOutputTokens);
}
