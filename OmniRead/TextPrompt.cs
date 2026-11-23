using System.Windows;
using System.Windows.Controls;

using OmniRead.Localization;

namespace OmniRead;

/// <summary>A small dialog that asks for one line of text.</summary>
public static class TextPrompt {
    /// <summary>Returns what was typed, or null when the dialog was cancelled.</summary>
    public static string? Show(Window owner, string title, string label, string initial) {
        TextBox box = new() { Text = initial, MinWidth = 320, Margin = new Thickness(0, 8, 0, 16) };
        Button accept = new() { Content = Localizer.Get("Dialog_OK"), IsDefault = true, MinWidth = 88 };
        Button cancel = new() { Content = Localizer.Get("Dialog_Cancel"), IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(box);
        content.Children.Add(buttons);
        HandyControl.Controls.Window dialog = new() {
            Owner = owner,
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize
        };
        dialog.SetResourceReference(HandyControl.Controls.Window.NonClientAreaBackgroundProperty, "TitleBarBrush");
        dialog.SetResourceReference(HandyControl.Controls.Window.NonClientAreaForegroundProperty, "TextBrush");
        dialog.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => {
            box.Focus();
            box.SelectAll();
        };
        return (dialog.ShowDialog() == true) ? box.Text : null;
    }
}