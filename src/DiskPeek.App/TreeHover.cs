using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DiskPeek.App;

public static class TreemapFolderPreview
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(TreemapFolderPreview), new FrameworkPropertyMetadata(false));
    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}

public partial class MainWindow
{
    private readonly HashSet<DataGridRow> realizedTreeRows = [];
    private DispatcherTimer treeHoverTimer = null!;
    private int hoveredTreeFolder = -1;
    private int hoverContextFolder = -1;
    private bool hoverRevealPending;

    private void InitializeTreeHover()
    {
        treeHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        treeHoverTimer.Tick += (_, _) => { treeHoverTimer.Stop(); RevealHoveredFolder(); };
        Treemap.FolderHovered += PreviewTreeFolder;
        FileGrid.LoadingRow += (_, e) =>
        {
            realizedTreeRows.Add(e.Row);
            UpdateTreeRowPreview(e.Row);
        };
        FileGrid.UnloadingRow += (_, e) =>
        {
            TreemapFolderPreview.SetIsActive(e.Row, false);
            realizedTreeRows.Remove(e.Row);
        };
    }

    private void PreviewTreeFolder(int folder)
    {
        if (hoveredTreeFolder == folder || closing) return;
        hoveredTreeFolder = folder;
        hoverRevealPending = false;
        treeHoverTimer.Stop();
        foreach (var row in realizedTreeRows) UpdateTreeRowPreview(row);
        if (folder >= 0 && ViewTabs.SelectedIndex == 0) treeHoverTimer.Start();
        else if (hoverContextFolder >= 0)
        {
            hoverContextFolder = -1;
            RefreshRows();
        }
    }

    private void UpdateTreeRowPreview(DataGridRow row) => TreemapFolderPreview.SetIsActive(row,
        ViewTabs.SelectedIndex == 0 && row.Item is NodeRow item && item.Index == hoveredTreeFolder);

    private void RevealHoveredFolder()
    {
        int folder = hoveredTreeFolder;
        if (closing || result == null || (uint)folder >= (uint)result.Count || ViewTabs.SelectedIndex != 0 || !result.IsDescendantOf(folder, rootIndex)) return;
        hoverRevealPending = true;
        if (FileGrid.ItemsSource is NodeRows rows && rows.IndexOfNode(folder) >= 0 && hoverContextFolder < 0)
        {
            CompleteTreeHoverReveal();
            return;
        }
        // Expand ancestors, not the folder's own files. The committed file selection is untouched.
        for (int parent = result.Nodes[folder].ParentIndex, steps = 0; parent >= 0 && steps++ < result.Count; parent = result.Nodes[parent].ParentIndex)
            expanded.Add(parent);
        hoverContextFolder = folder;
        RefreshRows();
    }

    private void CompleteTreeHoverReveal()
    {
        foreach (var row in realizedTreeRows) UpdateTreeRowPreview(row);
        if (!hoverRevealPending || hoveredTreeFolder < 0 || ViewTabs.SelectedIndex != 0 || FileGrid.ItemsSource is not NodeRows rows) return;
        int position = rows.IndexOfNode(hoveredTreeFolder);
        if (position < 0) return;
        hoverRevealPending = false;
        // ScrollIntoView realizes only the needed row; LoadingRow supplies the transient accent.
        FileGrid.ScrollIntoView(rows[position]);
    }
}
