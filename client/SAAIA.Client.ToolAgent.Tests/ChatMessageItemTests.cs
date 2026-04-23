using SAAIA.Client.WinUI.Models;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ChatMessageItemTests
{
    [Fact]
    public void IsStreaming_notifies_ui_when_generation_state_changes()
    {
        var item = new ChatMessageItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.IsStreaming = true;
        item.IsStreaming = false;

        Assert.Equal(new[] { nameof(ChatMessageItem.IsStreaming), nameof(ChatMessageItem.IsStreaming) }, changed);
    }
}
