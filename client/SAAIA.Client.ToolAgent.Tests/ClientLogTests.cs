using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClientLogTests
{
    [Fact]
    public void RotateIfNeeded_bounds_and_orders_retained_logs()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "saaia-client-log-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "client_startup.log");

        try
        {
            File.WriteAllText(logPath, "current");
            File.WriteAllText(logPath + ".1", "previous-1");
            File.WriteAllText(logPath + ".2", "previous-2");
            File.WriteAllText(logPath + ".3", "expired");

            ClientLog.RotateIfNeeded(logPath, maximumBytes: 1, retainedLogCount: 3);

            Assert.False(File.Exists(logPath));
            Assert.Equal("current", File.ReadAllText(logPath + ".1"));
            Assert.Equal("previous-1", File.ReadAllText(logPath + ".2"));
            Assert.Equal("previous-2", File.ReadAllText(logPath + ".3"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RotateIfNeeded_leaves_a_log_below_the_limit_untouched()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "saaia-client-log-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "client_startup.log");

        try
        {
            File.WriteAllText(logPath, "small");

            ClientLog.RotateIfNeeded(logPath, maximumBytes: 1024, retainedLogCount: 3);

            Assert.Equal("small", File.ReadAllText(logPath));
            Assert.False(File.Exists(logPath + ".1"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
