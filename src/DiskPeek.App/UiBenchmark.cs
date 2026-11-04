using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiskPeek.Core;

namespace DiskPeek.App;

public partial class MainWindow
{
    // Explicit developer-only mode. No synthetic records are present in normal app flow.
    // DiskPeek.exe --ui-benchmark <report.json> [--synthetic-count 1000000] [--benchmark-exit]
    private async Task RunUiBenchmark(string outputPath, int count)
    {
        var report = new Dictionary<string, object?> { ["synthetic_file_count"] = count, ["started_utc"] = DateTime.UtcNow };
        var interval = TimeSpan.FromMilliseconds(30);
        var pulse = Stopwatch.StartNew();
        double maxDelay = 0;
        int pulses = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = interval };
        timer.Tick += (_, _) => { maxDelay = Math.Max(maxDelay, Math.Max(0, pulse.Elapsed.TotalMilliseconds - interval.TotalMilliseconds)); pulses++; pulse.Restart(); };
        timer.Start();
        try
        {
            StatusText.Text = $"UI benchmark · generating {count:N0} synthetic files";
            var clock = Stopwatch.StartNew();
            result = await Task.Run(() =>
            {
                var nodes = new ScanNode[count + 1];
                nodes[0] = new ScanNode { Name = "Synthetic benchmark", ParentIndex = -1, Attributes = FileAttributes.Directory };
                string[] extensions = [".bin", ".mp4", ".jpg", ".zip", ".dll", ".pdf", ".ts", ".log"];
                for (int i = 1; i <= count; i++)
                {
                    long length = 64L + (long)(i % 32749) * 2048;
                    nodes[i] = new ScanNode { Name = "file-" + i.ToString("D7") + extensions[i % extensions.Length], ParentIndex = 0, Identity = new FileIdentity((ulong)i), LogicalBytes = length, AllocatedBytes = (length + 4095) / 4096 * 4096, Attributes = FileAttributes.Normal };
                }
                return ScanAggregation.Complete(new ScanResult { Target = @"C:\DiskPeek synthetic benchmark", Nodes = nodes, Engine = "Synthetic UI benchmark", Elapsed = TimeSpan.Zero });
            });
            report["seed_and_aggregate_ms"] = clock.Elapsed.TotalMilliseconds;
            rootIndex = 0;
            expanded.Clear();
            expanded.Add(0);
            UpdateSummary();
            UpdateBreadcrumbs();
            clock.Restart();
            RefreshRows();
            RefreshTypes();
            Treemap.SetResult(result, 0, Metric);
            await WaitForView();
            FileGrid.UpdateLayout();
            report["initial_projection_and_map_ms"] = clock.Elapsed.TotalMilliseconds;
            report["visible_projection_count"] = FileGrid.Items.Count;
            report["initial_realized_rows"] = CountVisualRows(FileGrid);
            report["treemap_tiles"] = Treemap.TileCount;
            report["treemap_bounds_valid"] = Treemap.LayoutBoundsValid;
            if (FileGrid.Items.Count > 0)
            {
                clock.Restart();
                FileGrid.ScrollIntoView(FileGrid.Items[FileGrid.Items.Count - 1]);
                await Dispatcher.InvokeAsync(() => FileGrid.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                report["scroll_to_last_ms"] = clock.Elapsed.TotalMilliseconds;
                report["scrolled_realized_rows"] = CountVisualRows(FileGrid);
            }
            clock.Restart();
            SearchBox.Text = "*9999*.bin";
            searchTimer.Stop();
            RefreshRows();
            await WaitForView();
            report["wildcard_search_ms"] = clock.Elapsed.TotalMilliseconds;
            report["wildcard_visible_rows"] = FileGrid.Items.Count;
            SearchBox.Clear();
            searchTimer.Stop();
            ViewTabs.SelectedIndex = 1;
            clock.Restart();
            RefreshRows();
            await WaitForView();
            report["largest_files_ms"] = clock.Elapsed.TotalMilliseconds;
            report["largest_files_count"] = FileGrid.Items.Count;
            report["dispatcher_pulses"] = pulses;
            report["max_dispatcher_delay_ms"] = maxDelay;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            report["working_set_bytes"] = process.WorkingSet64;
            report["peak_working_set_bytes"] = process.PeakWorkingSet64;
            report["managed_heap_bytes"] = GC.GetTotalMemory(false);
            report["passed"] = (int)report["initial_realized_rows"]! is > 0 and < 250 && (int)report["scrolled_realized_rows"]! is > 0 and < 250 && Treemap.LayoutBoundsValid && FileGrid.Items.Count == 1000;
            StatusText.Text = $"UI benchmark complete · {count:N0} files · report: {outputPath}";
        }
        catch (Exception ex) { report["passed"] = false; report["error"] = ex.ToString(); StatusText.Text = "UI benchmark failed · " + ex.Message; }
        finally
        {
            timer.Stop();
            report["completed_utc"] = DateTime.UtcNow;
            try
            {
                string absolute = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { StatusText.Text = "Could not write UI benchmark report · " + ex.Message; }
            if (Environment.GetCommandLineArgs().Contains("--benchmark-exit")) Close();
        }
    }

    private async Task WaitForView()
    {
        var timeout = Stopwatch.StartNew();
        while (viewCancellation != null || typeCancellation != null || Treemap.IsLayoutPending)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("UI benchmark projections did not complete in 90 seconds.");
            await Task.Delay(30);
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static int CountVisualRows(DependencyObject parent)
    {
        int count = parent is DataGridRow ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) count += CountVisualRows(VisualTreeHelper.GetChild(parent, i));
        return count;
    }
}
