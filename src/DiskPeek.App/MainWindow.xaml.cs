using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskPeek.Core;
using Microsoft.Win32;

namespace DiskPeek.App;

public partial class MainWindow : Window
{
    private ScanResult? result;
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? viewCancellation;
    private CancellationTokenSource? typeCancellation;
    private readonly DispatcherTimer searchTimer;
    private readonly HashSet<int> expanded = [];
    private int rootIndex;
    private int selectedIndex = -1;
    private int pendingRevealIndex = -1;
    private string? extensionFilter;
    private bool initialized;
    private bool closing;
    private string sort = "Size";
    private bool descending = true;
    private string sessionNote = "";
    private SizeMetric Metric => LogicalRadio.IsChecked == true ? SizeMetric.Logical : SizeMetric.Allocated;
    private bool IsScanning => scanCancellation != null;

    public MainWindow()
    {
        InitializeComponent();
        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshRows(); };
        InitializeTreeHover();
        ThemePicker.SelectedIndex = settingsStore.Settings.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady) TargetBox.Items.Add(drive.RootDirectory.FullName); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        TargetBox.Text = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        Treemap.NodeSelected += RevealMapSelection;
        Treemap.NodeActivated += Zoom;
        TypesList.PreviewMouseLeftButtonDown += Type_PreviewClick;
        Treemap.NodeHovered += index => { if (selectedIndex < 0) SelectedPath.Text = result?.GetPath(index) ?? ""; };
        initialized = true;
        Loaded += (_, _) =>
        {
            InitializeUpdates();
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && !args[1].StartsWith('-')) TargetBox.Text = args[1];
            int targetArgument = Array.IndexOf(args, "--target");
            if (targetArgument >= 0 && targetArgument + 1 < args.Length) TargetBox.Text = args[targetArgument + 1];
            if (args.Contains("--scan")) Scan_Click(ScanButton, new RoutedEventArgs());
            int benchmarkArgument = Array.IndexOf(args, "--ui-benchmark");
            if (benchmarkArgument >= 0 && benchmarkArgument + 1 < args.Length)
            {
                int countArgument = Array.IndexOf(args, "--synthetic-count");
                int count = countArgument >= 0 && countArgument + 1 < args.Length && int.TryParse(args[countArgument + 1], out int requested) ? Math.Clamp(requested, 1000, 2000000) : 1000000;
                _ = RunUiBenchmark(args[benchmarkArgument + 1], count);
            }
        };
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is ComboBoxItem item)
        {
            string theme = item.Content?.ToString() ?? "System";
            App.SetTheme(theme);
            if (initialized) { settingsStore.Settings.Theme = theme; SaveSettings(); }
        }
        Treemap?.InvalidateVisual();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to analyze", Multiselect = false };
        if (dialog.ShowDialog(this) == true) TargetBox.Text = dialog.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning || updateApplying) return;
        string target;
        try
        {
            string input = Environment.ExpandEnvironmentVariables(TargetBox.Text.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Choose a drive or enter a folder path.");
            target = Path.GetFullPath(input);
            if (!Path.IsPathFullyQualified(input) || !Directory.Exists(target)) throw new ArgumentException("Enter an existing drive, folder, or network path.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Choose a scan target", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cancellation = scanCancellation = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Preparing scan…";
        StatusDot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        SizeValue.Text = "Scanning…";
        SizeNote.Text = "Collecting filesystem metadata";
        TimeValue.Text = "0.0 s";
        sessionNote = "";
        var progress = new Progress<ScanProgress>(value =>
        {
            if (closing || cancellation != scanCancellation) return;
            FilesValue.Text = $"{value.Entries:N0}";
            FilesNote.Text = $"{value.Directories:N0} folders found";
            TimeValue.Text = Seconds(value.Elapsed);
            EngineNote.Text = value.Engine;
            StatusText.Text = $"{value.Phase} · {value.Entries:N0} entries · {value.Engine}";
        });
        try
        {
            ScanResult next;
            bool fast = FastCheck.IsChecked == true;
            if (fast && WorkerClient.IsSupportedTarget(target))
            {
                StatusText.Text = "Starting read-only NTFS worker · Approve the Windows administrator prompt";
                try
                {
                    next = await WorkerClient.ScanAsync(new ScanRequest(target, ScanEngineKind.Mft), progress, cancellation.Token);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    sessionNote = "Fast scan unavailable: " + ex.Message + " Directory scan used.";
                    StatusText.Text = "Fast scan unavailable · continuing with directory scan";
                    next = await ScanService.ScanAsync(new ScanRequest(target, ScanEngineKind.Directory), progress, cancellation.Token);
                }
            }
            else
            {
                next = await ScanService.ScanAsync(new ScanRequest(target, ScanEngineKind.Directory), progress, cancellation.Token);
            }
            if (closing) return;
            result = next;
            rootIndex = 0;
            selectedIndex = -1;
            extensionFilter = null;
            expanded.Clear();
            expanded.Add(0);
            SearchBox.Text = "";
            SelectedName.Text = "No item selected";
            SelectedPath.Text = "Select a row or a tile to inspect it.";
            SelectedSize.Text = "";
            RevealButton.IsEnabled = CopyButton.IsEnabled = false;
            Title = $"DiskPeek · {target}";
            UpdateSummary();
            UpdateBreadcrumbs();
            RefreshRows();
            RefreshTypes();
            Treemap.SetResult(result, rootIndex, Metric);
            ExportButton.IsEnabled = result.Count > 0 && !exportActive;
            IssuesButton.Visibility = result.Issues.Count > 0 || result.Partial || result.Cancelled || sessionNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            IssuesButton.Content = result.Issues.Count > 0 ? $"{result.Issues.Count:N0} scan issues" : "Scan details";
            bool incomplete = result.Partial || result.Cancelled;
            StatusDot.SetResourceReference(Border.BackgroundProperty, incomplete ? "WarningBrush" : "AccentBrush");
            StatusText.Text = (result.Cancelled ? "Stopped · partial results" : result.Partial ? "Completed with partial results" : "Scan complete") + $" · {result.Count:N0} entries · {Seconds(result.Elapsed)} · {result.Engine}";
            if (!string.IsNullOrEmpty(sessionNote)) StatusText.Text += " · see scan details";
        }
        catch (OperationCanceledException)
        {
            if (!closing) { StatusText.Text = "Scan stopped"; UpdateSummary(); }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                UpdateSummary();
                StatusText.Text = "Scan failed · " + ex.Message;
                StatusDot.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
                MessageBox.Show(this, ex.Message, "DiskPeek could not scan this target", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (scanCancellation == cancellation) scanCancellation = null;
            cancellation.Dispose();
            if (!closing) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = BrowseButton.IsEnabled = TargetBox.IsEnabled = FastCheck.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        ScanButton.Content = result == null ? "Scan" : "Rescan";
        ScanProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        scanCancellation?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping scan…";
    }

    private void UpdateSummary()
    {
        SizeLabel.Text = Metric == SizeMetric.Allocated ? "ALLOCATED FILE DATA" : "LOGICAL FILE SIZE";
        if (result == null)
        {
            SizeValue.Text = FilesValue.Text = FreeValue.Text = TimeValue.Text = "—";
            SizeNote.Text = "Choose a drive or folder to begin";
            FilesNote.Text = "Metadata scan; file contents stay untouched";
            EngineNote.Text = "Ready when you are";
            return;
        }
        SizeValue.Text = FormatUtil.Bytes(result.Value(rootIndex, Metric));
        SizeNote.Text = Metric == SizeMetric.Allocated && result.UnknownCounts[rootIndex] > 0
            ? $"+ {result.UnknownCounts[rootIndex]:N0} unknown allocated sizes"
            : rootIndex == 0 ? "File data; metadata and reserved space excluded" : "Within the selected folder";
        FilesValue.Text = $"{result.FileCounts[rootIndex]:N0}";
        FilesNote.Text = $"{result.DirectoryCounts[rootIndex]:N0} folders · {(result.Partial || result.Cancelled ? "partial results" : "scan complete")}";
        FreeValue.Text = result.VolumeFree.HasValue ? FormatUtil.Bytes(result.VolumeFree.Value) : "—";
        FreeNote.Text = result.VolumeCapacity.HasValue ? $"of {FormatUtil.Bytes(result.VolumeCapacity.Value)} volume capacity" : "Unavailable for this target";
        TimeValue.Text = Seconds(result.Elapsed);
        EngineNote.Text = result.Engine;
    }
    private static string Seconds(TimeSpan time) => time.TotalSeconds < 60 ? $"{time.TotalSeconds:N1} s" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private async void RefreshRows()
    {
        if (!initialized || result == null || closing) return;
        viewCancellation?.Cancel();
        var cancellation = viewCancellation = new CancellationTokenSource();
        var snapshot = result;
        int snapshotRoot = rootIndex;
        SizeMetric metric = Metric;
        bool largest = ViewTabs.SelectedIndex == 1;
        string query = SearchBox.Text.Trim(), selectedSort = sort;
        string? selectedExtension = extensionFilter;
        int contextFolder = largest ? -1 : hoverContextFolder;
        bool selectedDescending = descending;
        var expandedSnapshot = new HashSet<int>(expanded);
        RowCount.Text = "Updating view…";
        try
        {
            var entries = await Task.Run(() => ResultView.BuildRows(snapshot, snapshotRoot, metric, expandedSnapshot, query, largest, selectedSort, selectedDescending, cancellation.Token, selectedExtension, contextFolder), cancellation.Token);
            if (closing || cancellation.IsCancellationRequested || result != snapshot) return;
            FileGrid.ItemsSource = new NodeRows(snapshot, entries, metric, snapshotRoot, expandedSnapshot);
            int reveal = pendingRevealIndex;
            pendingRevealIndex = -1;
            int selectedPosition = Array.FindIndex(entries, entry => entry.Index == (reveal >= 0 ? reveal : selectedIndex));
            if (selectedPosition >= 0)
            {
                FileGrid.SelectedIndex = selectedPosition;
                if (reveal >= 0) FileGrid.ScrollIntoView(FileGrid.SelectedItem);
            }
            EmptyHint.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = "No files match this search.";
            RowCount.Text = largest ? $"{entries.Length:N0} largest matching files · maximum 1,000 · current folder" : $"{entries.Length:N0} visible rows" + (query.Length > 0 ? " · matching names and their parent folders" : " · expand folders to explore");
            if (selectedExtension == "") RowCount.Text += " · no extension";
            CompleteTreeHoverReveal();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) { RowCount.Text = "View unavailable · " + ex.Message; } }
        finally { if (viewCancellation == cancellation) viewCancellation = null; cancellation.Dispose(); }
    }

    private async void RefreshTypes()
    {
        if (result == null || closing) return;
        typeCancellation?.Cancel();
        var cancellation = typeCancellation = new CancellationTokenSource();
        var snapshot = result;
        int snapshotRoot = rootIndex;
        SizeMetric metric = Metric;
        TypesSubtitle.Text = "Calculating file types…";
        try
        {
            var types = await Task.Run(() => ResultView.BuildTypes(snapshot, snapshotRoot, metric, cancellation.Token), cancellation.Token);
            if (closing || cancellation.IsCancellationRequested || snapshot != result) return;
            TypesList.ItemsSource = types;
            TypesSubtitle.Text = types.Length == 200 ? "Largest 200 types · current folder" : "The selected folder, by extension";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) TypesSubtitle.Text = "Types unavailable: " + ex.Message; }
        finally { if (typeCancellation == cancellation) typeCancellation = null; cancellation.Dispose(); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        if (SearchBox.Text.Length > 0) extensionFilter = null;
        SearchHint.Text = extensionFilter == "" ? "Files with no extension" : "Search names or *.ext";
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        searchTimer.Stop();
        searchTimer.Start();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearFilters();
    private void ClearFilters()
    {
        extensionFilter = null;
        TypesList.SelectedItem = null;
        SearchBox.Clear();
        SearchHint.Text = "Search names or *.ext";
        RefreshRows();
    }
    private void Metric_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        UpdateSummary();
        RefreshRows();
        RefreshTypes();
        Treemap.SetResult(result, rootIndex, Metric);
        if (selectedIndex >= 0) SelectNode(selectedIndex);
    }
    private void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == ViewTabs && initialized) RefreshRows();
    }
    private void File_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string next = e.Column.SortMemberPath;
        descending = sort == next ? !descending : next != "Name";
        sort = next;
        foreach (var column in FileGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        RefreshRows();
    }
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        if (!expanded.Add(index)) expanded.Remove(index);
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        RefreshRows();
        e.Handled = true;
    }
    private void File_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileGrid.SelectedItem is NodeRow row) SelectNode(row.Index);
    }
    private void File_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is NodeRow row && e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source) != null) Zoom(row.Index);
    }
    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T result) return result;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void SelectNode(int index)
    {
        if (result == null || (uint)index >= (uint)result.Count) return;
        selectedIndex = index;
        var node = result.Nodes[index];
        SelectedName.Text = index == 0 ? result.Target : node.Name;
        SelectedPath.Text = result.GetPath(index);
        SelectedPath.ToolTip = SelectedPath.Text;
        string details = $"{ResultView.AllocatedSize(result, index)} allocated · {FormatUtil.Bytes(result.Value(index, SizeMetric.Logical))} logical";
        if (result.UnknownCounts[index] > 0) details += $"\n{result.UnknownCounts[index]:N0} unknown allocated sizes";
        if ((node.Flags & NodeFlags.SharedAllocation) != 0) details += "\nShared allocation is counted on one hard link.";
        if ((node.Flags & NodeFlags.ReparseSkipped) != 0) details += "\nLink target not traversed.";
        if ((node.Flags & NodeFlags.AdditionalStreams) != 0) details += "\nAdditional streams exist; sizes cover the unnamed stream only.";
        SelectedSize.Text = details;
        RevealButton.IsEnabled = CopyButton.IsEnabled = true;
        Treemap.SelectNode(index);
    }
    private void RevealMapSelection(int index)
    {
        if (result == null) return;
        SelectNode(index);
        if (FileGrid.ItemsSource is NodeRows current)
        {
            int position = current.IndexOfNode(index);
            if (position >= 0)
            {
                FileGrid.SelectedIndex = position;
                FileGrid.ScrollIntoView(FileGrid.SelectedItem);
                return;
            }
        }
        // A map tile can refer to a collapsed branch or a file outside the top 1,000.
        // Reveal it in the tree by walking only its ancestor chain.
        for (int parent = result.Nodes[index].ParentIndex, steps = 0; parent >= 0 && steps++ < result.Count; parent = result.Nodes[parent].ParentIndex) expanded.Add(parent);
        extensionFilter = null;
        TypesList.SelectedItem = null;
        SearchBox.Clear();
        pendingRevealIndex = index;
        ViewTabs.SelectedIndex = 0;
        RefreshRows();
    }
    private void ZoomSelected_Click(object sender, RoutedEventArgs e) { if (selectedIndex >= 0) Zoom(selectedIndex); }
    private void Zoom(int index)
    {
        if (result == null || !result.Nodes[index].IsDirectory || (result.Nodes[index].Flags & NodeFlags.ReparseSkipped) != 0) return;
        rootIndex = index;
        expanded.Add(index);
        ClearFilters();
        UpdateSummary();
        UpdateBreadcrumbs();
        RefreshRows();
        RefreshTypes();
        Treemap.SetResult(result, rootIndex, Metric);
        SelectNode(index);
    }
    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (result != null && rootIndex > 0) Zoom(result.Nodes[rootIndex].ParentIndex);
    }
    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Children.Clear();
        if (result == null) return;
        var chain = new List<int>();
        for (int i = rootIndex, steps = 0; i >= 0 && steps++ < result.Count; i = result.Nodes[i].ParentIndex) chain.Add(i);
        chain.Reverse();
        // A very deep path still has a root/home link and the nearest five parents.
        var visible = chain.Count <= 7 ? chain : new[] { chain[0] }.Concat(chain.TakeLast(5)).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            int index = visible[i];
            if (i > 0) Breadcrumbs.Children.Add(new TextBlock { Text = i == 1 && chain.Count > 7 ? " / … / " : " / ", Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = index == 0 ? result.Target : result.Nodes[index].Name, Style = (Style)FindResource("QuietButton"), FontSize = 11, MaxWidth = 180, ToolTip = result.GetPath(index) };
            button.Click += (_, _) => Zoom(index);
            Breadcrumbs.Children.Add(button);
        }
        UpButton.IsEnabled = rootIndex > 0;
    }
    private void Type_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (TypesList.SelectedItem is not TypeSummary type || type.Extension == "(other)") return;
        extensionFilter = type.Extension == "" ? "" : null;
        SearchBox.Text = type.Extension == "" ? "" : "*" + type.Extension;
        SearchHint.Text = type.Extension == "" ? "Files with no extension" : "Search names or *.ext";
        ViewTabs.SelectedIndex = 1;
        RefreshRows();
    }
    private void Type_PreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ListBoxItem>(source) is { IsSelected: true })
        {
            ClearFilters();
            e.Handled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (result == null || selectedIndex < 0) return;
        try { Clipboard.SetText(result.GetPath(selectedIndex)); StatusText.Text = "Path copied to clipboard"; }
        catch (Exception ex) { StatusText.Text = "Could not copy path · " + ex.Message; }
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (result == null || selectedIndex < 0) return;
        try
        {
            string path = result.GetPath(selectedIndex);
            if (!Directory.Exists(path) && !File.Exists(path)) { StatusText.Text = "This item has moved or is no longer available · rescan to refresh"; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.Arguments = selectedIndex == 0 ? "\"" + path + "\"" : "/select,\"" + path + "\"";
            Process.Start(start);
        }
        catch (Exception ex) { StatusText.Text = "Could not open Explorer · " + ex.Message; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (result == null || exportActive || updateApplying) return;
        var dialog = new SaveFileDialog { Title = "Export complete DiskPeek scan", Filter = "CSV report (*.csv)|*.csv", FileName = "diskpeek-scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", AddExtension = true, DefaultExt = ".csv" };
        if (dialog.ShowDialog(this) != true) return;
        exportActive = true;
        ExportButton.IsEnabled = false;
        try
        {
            await CsvExporter.ExportAsync(result, dialog.FileName);
            if (!closing) StatusText.Text = "Complete scan exported · " + dialog.FileName;
        }
        catch (Exception ex) { if (!closing) MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { exportActive = false; if (!closing) ExportButton.IsEnabled = true; }
    }

    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (result == null) return;
        var lines = new List<string> { $"Target: {result.Target}", $"Engine: {result.Engine}", $"Completed: {result.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}", $"Entries: {result.Count:N0}    Skipped: {result.SkippedEntries:N0}", $"State: {(result.Cancelled ? "stopped" : result.Partial ? "partial" : "complete")}", "" };
        if (sessionNote.Length > 0) { lines.Add(sessionNote); lines.Add(""); }
        if (!result.HardLinkAccountingAvailable) lines.Add("Some file identities were unavailable; hard-link allocation may be counted more than once.");
        lines.Add("Allocated size covers measured file data. NTFS metadata, reserved space, and unmeasured data may explain differences from volume used space. Live scans are best-effort, not atomic snapshots.");
        lines.Add("");
        lines.AddRange(result.Issues.Take(300).Select(issue => issue.Path + "\n  " + issue.Message));
        if (result.Issues.Count > 300) lines.Add($"… {result.Issues.Count - 300:N0} additional issues.");
        var text = new TextBox { Text = string.Join("\n", lines), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        var window = new Window { Title = "DiskPeek · Scan details", Owner = this, Width = 760, Height = 560, MinWidth = 480, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = text };
        window.ShowDialog();
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        MessageBox.Show(this, $"DiskPeek {version}\n\nAn independent, open-source disk usage analyzer for Windows.\nLicensed under the GNU Affero General Public License, version 3 or later.\n\nFast NTFS scanning is enabled by default for supported local NTFS drive roots. Selecting Scan requests administrator access for a read-only worker. Folders, other filesystems, and network paths automatically use directory scanning. File contents are not read for usage analysis.\n\nHover a file tile to highlight visible files in its immediate folder. Double-click a folder tile to zoom.\n\nDiskPeek is not affiliated with WizTree or WinDirStat. Source and license are included with this distribution.", "About DiskPeek", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (key == Key.F5 && !IsScanning) { Scan_Click(ScanButton, new RoutedEventArgs()); e.Handled = true; }
        else if (key == Key.Escape && IsScanning) { Stop_Click(StopButton, new RoutedEventArgs()); e.Handled = true; }
        else if (key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt) { Up_Click(UpButton, new RoutedEventArgs()); e.Handled = true; }
        else if (key == Key.Enter && FileGrid.IsKeyboardFocusWithin && FileGrid.SelectedItem is NodeRow row) { Zoom(row.Index); e.Handled = true; }
        else if (key is Key.Left or Key.Right && FileGrid.IsKeyboardFocusWithin && FileGrid.SelectedItem is NodeRow selected && selected.IsDirectory)
        {
            if (key == Key.Right) expanded.Add(selected.Index); else expanded.Remove(selected.Index);
            RefreshRows();
            e.Handled = true;
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closing = true;
        StopUpdateChecks();
        scanCancellation?.Cancel();
        viewCancellation?.Cancel();
        typeCancellation?.Cancel();
        searchTimer.Stop();
        treeHoverTimer.Stop();
    }
}
