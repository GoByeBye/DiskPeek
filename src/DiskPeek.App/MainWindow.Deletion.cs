using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskPeek.Core;

namespace DiskPeek.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource deletionLifetime = new();
    private ScanDeletionMonitor? deletionMonitor;
    private int deletionUiQueued;
    private bool selectionIsRemainder;
    private bool recycleActive;
    private CancellationTokenSource? recycleCancellation;
    private bool closeAfterRecycle;
    private bool recycleContextTargetValid = true;
    private string? deletionTrackingNote;

    private void InitializeDeletionActions()
    {
        FileGrid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source)?.Item is NodeRow item && item.BelongsTo(result))
            { selectionIsRemainder = false; RefreshRecycleControls(); }
        };
        FileGrid.GotKeyboardFocus += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source)?.Item is NodeRow item && item.BelongsTo(result))
            { selectionIsRemainder = false; RefreshRecycleControls(); }
        };
        FileGrid.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = e.OriginalSource is DependencyObject source ? FindParent<DataGridRow>(source) : null;
            recycleContextTargetValid = row?.Item is NodeRow current && current.BelongsTo(result);
            if (row?.Item is NodeRow item && item.BelongsTo(result))
            {
                selectionIsRemainder = false;
                FileGrid.SelectedItem = item;
                SelectNode(item.Index);
            }
        };
        FileGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
                recycleContextTargetValid = FileGrid.SelectedItem is NodeRow item && item.BelongsTo(result);
        };
        FileGrid.ContextMenu.Opened += (_, _) => RefreshRecycleControls();
        RefreshRecycleControls();
    }

    private void SelectMapNode(int index)
    {
        // A "More files" tile is an aggregate, never permission to recycle its owner.
        selectionIsRemainder = Treemap.SelectedIsRemainder;
        RevealMapSelection(index);
        RefreshRecycleControls();
    }

    private void AcceptDeletionMonitor(ScanDeletionMonitor? monitor)
    {
        var previous = deletionMonitor;
        deletionMonitor = monitor;
        if (monitor != null) monitor.Changed += DeletionMonitorChanged;
        if (previous != null) { previous.Changed -= DeletionMonitorChanged; previous.Dispose(); }
    }

    private void DeletionMonitorChanged(object? sender, EventArgs e)
    {
        if (closing || !ReferenceEquals(sender, deletionMonitor) || Interlocked.Exchange(ref deletionUiQueued, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref deletionUiQueued, 0);
                if (!closing) RefreshDeletionUi();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref deletionUiQueued, 0); }
    }

    private void RefreshDeletionUi()
    {
        var state = deletionMonitor?.State;
        Treemap.SetDeletionState(state);
        foreach (var row in realizedTreeRows)
            if (row.Item is NodeRow item) item.RefreshDeletion();
        int count = state?.DeletedCount ?? 0;
        DeletedStatus.Text = result == null ? "Scan sizes are a snapshot. Red marks entries deleted or moved afterwards."
            : $"{count:N0} entries deleted/moved since scan · original sizes and tile areas retained";
        if (state?.TrackingIncomplete == true) DeletedStatus.Text += " · tracking incomplete; Rescan";
        if (deletionTrackingNote != null) DeletedStatus.Text += " · live tracking unavailable";
        DeletedStatus.ToolTip = deletionTrackingNote ?? state?.Status ?? "Rescan to refresh snapshot totals. Recycle Bin contents still use disk space.";
        RefreshSelectedDeletionStatus();
        RefreshRecycleControls();
    }

    private void RefreshSelectedDeletionStatus()
    {
        if (result == null || selectedIndex < 0) { SelectedChangeStatus.Text = ""; return; }
        var state = deletionMonitor?.State;
        bool deleted = state?.IsDeleted(selectedIndex) == true;
        bool changed = state?.HasDeletedDescendants(selectedIndex) == true;
        SelectedChangeStatus.Text = deleted ? "Deleted or moved since scan · snapshot sizes retained"
            : changed ? $"{state!.GetDeletedCount(selectedIndex):N0} entries deleted/moved since scan" : "";
        SelectedPath.SetResourceReference(TextBlock.ForegroundProperty, deleted ? "DeletionBrush" : "MutedBrush");
    }

    private string? RecycleUnavailableReason()
    {
        if (result == null || selectedIndex < 0) return "Select one actual file or folder in the table or map.";
        if (viewCancellation != null || FileGrid.ItemsSource is not NodeRows rows || !ReferenceEquals(rows.Snapshot, result)) return "Wait for the current scan view to finish updating before recycling.";
        if (result.Engine == "Synthetic UI benchmark") return "Synthetic benchmark records cannot be recycled.";
        if (IsScanning || exportActive || updateApplying || recycleActive) return "Finish or stop the scan, and wait for exports or updates before recycling.";
        if (selectionIsRemainder) return "This tile represents additional items, not one deletion target. Select the actual file or folder in the table.";
        if (deletionMonitor?.State.IsDeleted(selectedIndex) == true) return "This snapshot entry was deleted or moved. Rescan before operating on its path again.";
        var validation = RecycleBinService.ValidateSelection(result, selectedIndex);
        return validation.Success ? null : validation.Message;
    }

    private void RefreshRecycleControls()
    {
        string? reason;
        try { reason = RecycleUnavailableReason(); }
        catch { reason = "This entry cannot be recycled safely. Rescan to refresh its metadata."; }
        RecycleButton.IsEnabled = reason == null;
        RecycleContextItem.IsEnabled = reason == null && recycleContextTargetValid;
        RecycleButton.ToolTip = RecycleContextItem.ToolTip = reason ?? "Move the selected file or folder to the Windows Recycle Bin (Delete).";
    }

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        string? unavailable = RecycleUnavailableReason();
        if (unavailable != null)
        {
            MessageBox.Show(this, unavailable, "Move to Recycle Bin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var snapshot = result!;
        int index = selectedIndex;
        string path = snapshot.GetPath(index);
        bool folder = snapshot.Nodes[index].IsDirectory;
        string contents = folder ? "This moves the folder and ALL of its CURRENT contents, including files added or changed since this scan." : "This moves the selected file.";
        string prompt = $"Move this {(folder ? "folder" : "file")} to the Windows Recycle Bin?\n\n{path}\n\n{contents}\n\nRecycle Bin contents still use disk space until the bin is emptied. Snapshot sizes will remain unchanged; Rescan to refresh them.";
        if (MessageBox.Show(this, prompt, "Move to Recycle Bin", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        // Re-check after confirmation. The core service validates live identity again.
        if (!ReferenceEquals(result, snapshot) || selectedIndex != index || RecycleUnavailableReason() != null) return;
        recycleActive = true;
        var cancellation = recycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(deletionLifetime.Token);
        ScanButton.IsEnabled = BrowseButton.IsEnabled = TargetBox.IsEnabled = FastCheck.IsEnabled = ExportButton.IsEnabled = false;
        RefreshRecycleControls();
        RefreshUpdateUi();
        try
        {
            StatusText.Text = "Moving to Recycle Bin…";
            var outcome = await RecycleBinService.MoveAsync(snapshot, index, cancellation.Token);
            if (closing) return;
            // Watcher state, not the command result, decides whether red is justified.
            deletionMonitor?.NotifyPath(path);
            StatusText.Text = outcome.Message;
            if (!outcome.Success && !outcome.Cancelled && !closeAfterRecycle)
                MessageBox.Show(this, outcome.Message, "Item was not recycled", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { if (!closing) StatusText.Text = "Recycle operation cancelled."; }
        catch (Exception ex)
        {
            if (!closing && !closeAfterRecycle) MessageBox.Show(this, ex.Message, "Item was not recycled", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            recycleActive = false;
            if (recycleCancellation == cancellation) recycleCancellation = null;
            cancellation.Dispose();
            if (!closing)
            {
                SetBusy(IsScanning);
                ExportButton.IsEnabled = result != null && !exportActive;
                RefreshRecycleControls();
                RefreshUpdateUi();
            }
            if (closeAfterRecycle && !closing) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void StopDeletionTracking()
    {
        deletionLifetime.Cancel();
        AcceptDeletionMonitor(null);
    }
}
