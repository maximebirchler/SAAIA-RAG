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

        Assert.Equal(2, changed.Count(p => p == nameof(ChatMessageItem.IsStreaming)));
        Assert.Contains(nameof(ChatMessageItem.StatusNoteVisibleWhenIdle), changed);
        Assert.Contains(nameof(ChatMessageItem.ProgressTextVisibleWhenIdle), changed);
        Assert.Contains(nameof(ChatMessageItem.StreamingShowProgressText), changed);
        Assert.Contains(nameof(ChatMessageItem.StreamingShowDefault), changed);
    }
}
