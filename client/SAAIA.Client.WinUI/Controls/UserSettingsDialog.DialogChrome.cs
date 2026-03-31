using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SAAIA.Client.WinUI.Controls;

internal sealed partial class UserSettingsDialog
{
    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Opened -= OnDialogOpened;

        FlattenDialogHostChrome();
        await Task.Yield();
        FlattenDialogHostChrome();
        await Task.Delay(1);
        FlattenDialogHostChrome();
        await Task.Delay(16);
        FlattenDialogHostChrome();
    }

    private void FlattenDialogHostChrome()
    {
        try
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ApplyGlobalDialogThemeOverrides(transparent);

            FlattenDialogElement(FindDescendantByName<Border>(this, "BackgroundElement"), transparent);
            FlattenDialogElement(FindDescendantByName<Border>(this, "Container"), transparent);
            FlattenDialogElement(FindDescendantByName<Border>(this, "DialogSpace"), transparent);
            FlattenDialogGrid(FindDescendantByName<Grid>(this, "LayoutRoot"), transparent);
            FlattenDialogGrid(FindDescendantByName<Grid>(this, "DialogSpace"), transparent);
            FlattenDialogGrid(FindDescendantByName<Grid>(this, "CommandSpace"), transparent);
            FlattenDialogScrollViewer(FindDescendantByName<ScrollViewer>(this, "ContentScrollViewer"), transparent);

            if (Content is FrameworkElement contentRoot)
            {
                var parent = VisualTreeHelper.GetParent(contentRoot);
                while (parent is not null && !ReferenceEquals(parent, this))
                {
                    if (parent is Border border)
                        FlattenDialogElement(border, transparent);
                    else if (parent is Grid grid)
                        FlattenDialogGrid(grid, transparent);
                    else if (parent is Panel panel)
                        FlattenDialogPanel(panel, transparent);
                    else if (parent is ScrollViewer scrollViewer)
                        FlattenDialogScrollViewer(scrollViewer, transparent);

                    if (parent is UIElement uiElement)
                    {
                        uiElement.Shadow = null;
                        uiElement.Translation = Vector3.Zero;
                    }

                    if (parent is FrameworkElement framework)
                        framework.Margin = new Thickness(0);

                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
        }
        catch
        {
            // non bloquant
        }
    }

    private static void FlattenDialogElement(Border? border, Brush transparent)
    {
        if (border is null)
            return;

        border.Background = transparent;
        border.BorderBrush = transparent;
        border.BorderThickness = new Thickness(0);
        border.Padding = new Thickness(0);
        border.Margin = new Thickness(0);
        border.Shadow = null;
        border.Translation = Vector3.Zero;
    }

    private static void FlattenDialogGrid(Grid? grid, Brush transparent)
    {
        if (grid is null)
            return;

        grid.Background = transparent;
        grid.Margin = new Thickness(0);
        grid.Shadow = null;
        grid.Translation = Vector3.Zero;
    }

    private static void FlattenDialogPanel(Panel? panel, Brush transparent)
    {
        if (panel is null)
            return;

        panel.Background = transparent;
        panel.Margin = new Thickness(0);
        panel.Shadow = null;
        panel.Translation = Vector3.Zero;
    }

    private static void FlattenDialogScrollViewer(ScrollViewer? scrollViewer, Brush transparent)
    {
        if (scrollViewer is null)
            return;

        scrollViewer.Background = transparent;
        scrollViewer.BorderBrush = transparent;
        scrollViewer.BorderThickness = new Thickness(0);
        scrollViewer.Padding = new Thickness(0);
        scrollViewer.Margin = new Thickness(0);
        scrollViewer.Shadow = null;
        scrollViewer.Translation = Vector3.Zero;
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
                return typed;

            var nested = FindDescendantByName<T>(child, name);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    internal UIElement DetachContentForOverlay(Action closeAction)
    {
        _overlayCloseAction = closeAction ?? throw new ArgumentNullException(nameof(closeAction));

        if (Content is UIElement existingContent)
        {
            Content = null;
            return existingContent;
        }

        return BuildUi();
    }

    private void RequestClose()
    {
        if (_overlayCloseAction is not null)
        {
            _overlayCloseAction();
            return;
        }

        Hide();
    }

    public void ApplyResponsiveLayout(double maxWidth, double maxHeight)
    {
        _shellHost.MaxWidth = maxWidth;
        _shellHost.MaxHeight = maxHeight;
        _layoutScroller.MaxWidth = Math.Max(280, maxWidth - 8);
        _layoutScroller.MaxHeight = Math.Max(180, maxHeight - 112);
    }
}
