using System.IO;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class RuntimeEventLogStoreTests
{
    [Fact]
    public async Task AppendAsync_stores_latest_events_in_reverse_chronological_order()
    {
        var root = NewTempRoot();
        RuntimeEventLogStore.RootOverride = root;

        try
        {
            await RuntimeEventLogStore.AppendAsync(new RuntimeEventLogItem(
                DateTimeOffset.Parse("2026-04-23T10:00:00Z"),
                "llama.cpp-cuda",
                "runtime_installed",
                "b8149",
                null,
                null,
                "initial"), root);
            await RuntimeEventLogStore.AppendAsync(new RuntimeEventLogItem(
                DateTimeOffset.Parse("2026-04-23T11:00:00Z"),
                "llama.cpp-cuda",
                "runtime_upgrade_activated",
                "b8901",
                "b8149",
                "gemma-4-e2b-it-q4-k-m",
                "upgrade"), root);

            var items = await RuntimeEventLogStore.ReadLatestAsync(5, root);

            Assert.Equal(2, items.Count);
            Assert.Equal("runtime_upgrade_activated", items[0].EventKind);
            Assert.Equal("runtime_installed", items[1].EventKind);
        }
        finally
        {
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-runtime-event-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
