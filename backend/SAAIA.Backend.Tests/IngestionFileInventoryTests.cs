using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionFileInventoryTests
{
    [Fact]
    public void Bounded_cursor_visits_later_sources_and_restarts_only_after_complete_cycle()
    {
        using var inventory = new IngestionFileInventory();
        var root = Path.GetTempPath();
        IEnumerable<string> Enumerate(string _) => new[] { "a.pdf", "b.pdf", "c.pdf" };
        var first = inventory.Read(root, 1, Enumerate);
        var second = inventory.Read(root, 1, Enumerate);
        var third = inventory.Read(root, 1, Enumerate);
        Assert.Equal(new[] { "a.pdf", "b.pdf", "c.pdf" }, first.Files.Concat(second.Files).Concat(third.Files));
        Assert.False(first.Complete);
        Assert.True(first.ContinuesNextScan);
        Assert.False(second.Complete);
        Assert.True(third.Complete);
        Assert.Equal(new[] { "a.pdf" }, inventory.Read(root, 1, Enumerate).Files);
    }

    [Fact]
    public void Enumeration_error_never_counts_as_complete_absence()
    {
        using var inventory = new IngestionFileInventory();
        static IEnumerable<string> Broken(string _)
        {
            yield return "still-present.pdf";
            throw new IOException("SYNTHETIC_ERROR_WITH_NO_LOGGED_CONTENT");
        }
        var batch = inventory.Read(Path.GetTempPath(), 2, Broken);
        Assert.Equal(new[] { "still-present.pdf" }, batch.Files);
        Assert.False(batch.Complete);
        Assert.False(batch.ContinuesNextScan);
        Assert.Equal("inventory_io_error", batch.FailureCode);
        Assert.DoesNotContain("SYNTHETIC", batch.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Access_or_io_failure_is_unknown_not_absent(bool denied)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var calls = 0;
        FileAttributes Attributes(string _)
        {
            if (calls++ == 0) return FileAttributes.Directory;
            if (denied) throw new UnauthorizedAccessException("synthetic");
            throw new IOException("synthetic");
        }
        Assert.Equal(IngestionSourcePresence.Unknown, IngestionFilePresence.Probe(root, "source.pdf", Attributes));
    }

    [Fact]
    public void Missing_root_does_not_assert_absence_of_all_sources()
    {
        Assert.Equal(IngestionSourcePresence.Unknown, IngestionFilePresence.Probe(Path.GetTempPath(), "source.pdf",
            _ => throw new DirectoryNotFoundException()));
    }

    [Fact]
    public void Only_explicit_not_found_under_accessible_root_is_absence()
    {
        var calls = 0;
        Assert.Equal(IngestionSourcePresence.Absent, IngestionFilePresence.Probe(Path.GetTempPath(), "source.pdf",
            _ => calls++ == 0 ? FileAttributes.Directory : throw new FileNotFoundException()));
    }

    [Fact]
    public void Traversal_is_unknown_without_probing_outside_root()
    {
        var calls = 0;
        Assert.Equal(IngestionSourcePresence.Unknown, IngestionFilePresence.Probe(Path.GetTempPath(), "../outside.pdf",
            _ => { calls++; return FileAttributes.Normal; }));
        Assert.Equal(0, calls);
    }
}
