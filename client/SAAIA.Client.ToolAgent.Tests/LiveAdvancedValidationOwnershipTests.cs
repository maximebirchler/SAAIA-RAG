using System.Text.Json;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveAdvancedValidationOwnershipTests
{
    [Fact]
    public void Different_contexts_keep_distinct_owners_recorded_before_use()
    {
        var path=Path.GetTempFileName();
        try
        {
            var first=LiveAdvancedValidationOwnership.CreateUserId(path,true);
            var second=LiveAdvancedValidationOwnership.CreateUserId(path,true);
            Assert.NotEqual(first,second);
            Assert.Equal(new[]{first,second},File.ReadAllLines(path).Select(line=>JsonSerializer.Deserialize<string>(line)));
            Assert.True(Guid.TryParse(first,out _));
            Assert.True(Guid.TryParse(second,out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-owner-file.jsonl")]
    public void Missing_or_relative_manifest_prevents_advanced_context_creation(string? path)
        => Assert.Throws<InvalidOperationException>(()=>LiveAdvancedValidationOwnership.CreateUserId(path,true));

    [Fact]
    public void Absent_destination_is_not_silently_created()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".owner.jsonl");
        Assert.Throws<FileNotFoundException>(()=>LiveAdvancedValidationOwnership.CreateUserId(path,true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Pure_local_context_does_not_require_or_write_an_owner_manifest()
        => Assert.True(Guid.TryParse(LiveAdvancedValidationOwnership.CreateUserId(null,false),out _));
}
