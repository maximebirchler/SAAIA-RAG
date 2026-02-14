using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Controls;

public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is ChatMessageItem m)
        {
            var role = (m.Role ?? "").Trim().ToLowerInvariant();
            if (role == "user")
                return UserTemplate ?? base.SelectTemplateCore(item);
        }

        return AssistantTemplate ?? base.SelectTemplateCore(item);
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
