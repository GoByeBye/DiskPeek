using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskPeek.App;

internal sealed class UpdateDialog : Window
{
    public TextBlock Heading { get; } = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    public TextBlock Message { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    public TextBlock Details { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
    public TextBlock SaveWarning { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    public CheckBox Automatic { get; } = new() { Content = "Check for updates automatically once a day", Margin = new Thickness(0, 11, 0, 4) };
    public ProgressBar Progress { get; } = new() { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
    public Button Primary { get; } = new() { MinWidth = 135, Margin = new Thickness(8, 0, 0, 0) };
    public Button Check { get; } = new() { Content = "Check now", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
    public Button Cancel { get; } = new() { Content = "Close", MinWidth = 80 };
    public Button Releases { get; } = new() { Content = "Release notes", Margin = new Thickness(0, 0, 8, 0) };
    public CancellationTokenSource Lifetime { get; } = new();
    public event Action? ClosingRequested;

    public UpdateDialog(Window owner)
    {
        Title = "DiskPeek · Updates";
        Owner = owner;
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new StackPanel { Margin = new Thickness(24) };
        var caption = new TextBlock { Text = "DISKPEEK UPDATES", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Details.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        SaveWarning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        Progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        Primary.Style = (Style)FindResource("PrimaryButton");
        root.Children.Add(caption);
        root.Children.Add(Heading);
        root.Children.Add(Message);
        root.Children.Add(Progress);
        root.Children.Add(Details);
        root.Children.Add(Automatic);
        root.Children.Add(SaveWarning);
        var buttons = new DockPanel { Margin = new Thickness(0, 21, 0, 0), LastChildFill = false };
        DockPanel.SetDock(Releases, Dock.Left);
        buttons.Children.Add(Releases);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Cancel);
        right.Children.Add(Check);
        right.Children.Add(Primary);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        Content = root;
        Closing += OnClosing;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Lifetime.Cancel();
        ClosingRequested?.Invoke();
    }
}
