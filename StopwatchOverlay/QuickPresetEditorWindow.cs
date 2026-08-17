using System;
using System.Windows;
using System.Windows.Controls;

namespace StopwatchOverlay;

public sealed class QuickPresetEditorWindow : Window
{
    private readonly TextBox _minutesTextBox;

    public int Minutes { get; private set; }

    public QuickPresetEditorWindow(string currentMinutes)
    {
        Title = "Edit quick duration";
        Width = 300;
        Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Duration in minutes", Margin = new Thickness(0, 0, 0, 6) });

        _minutesTextBox = new TextBox
        {
            Text = currentMinutes,
            FontSize = 16,
            Padding = new Thickness(8, 4, 8, 4),
            TextAlignment = TextAlignment.Center
        };
        panel.Children.Add(_minutesTextBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Save", MinWidth = 70, IsDefault = true };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) =>
        {
            _minutesTextBox.Focus();
            _minutesTextBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_minutesTextBox.Text, out var minutes) || minutes < 0)
        {
            MessageBox.Show(this, "Enter a whole number of minutes (zero or greater).", "Invalid duration", MessageBoxButton.OK, MessageBoxImage.Warning);
            _minutesTextBox.Focus();
            _minutesTextBox.SelectAll();
            return;
        }

        Minutes = minutes;
        DialogResult = true;
    }
}
