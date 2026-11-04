using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskPeek.Core;

namespace DiskPeek.App;

internal readonly record struct MapTile(int Index, Rect Bounds, int Color, bool Grouped, int FolderIndex, bool Remainder);
internal sealed record MapFolderGroup(Rect Bounds, Rect[] FileTiles);
internal sealed record MapLayout(MapTile[] Tiles, Size Size, IReadOnlyDictionary<int, MapFolderGroup> FolderGroups);

public sealed class TreemapControl : FrameworkElement
{
    private ScanResult? result;
    private SizeMetric metric;
    private int root;
    private int selected = -1;
    private int hover = -1;
    private int hoverFolder = -1;
    private Geometry? hoverGroupGeometry;
    private Rect hoverGroupBounds = Rect.Empty;
    private MapLayout? layout;
    private CancellationTokenSource? layoutCancellation;
    private readonly DispatcherTimer resizeTimer;
    private static readonly Brush TextBrush = FrozenBrush(Color.FromRgb(247, 251, 255));
    private static readonly Pen TilePen = FrozenPen(Color.FromArgb(110, 10, 20, 32), .7);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(224, 255, 250), 2.5);
    private static readonly Brush GroupFill = FrozenBrush(Color.FromArgb(46, 215, 255, 245));
    private static readonly Pen GroupShadowPen = FrozenPen(Color.FromArgb(220, 12, 28, 36), 4);
    private static readonly Pen GroupPen = FrozenPen(Color.FromRgb(148, 255, 226), 2);
    public event Action<int>? NodeSelected;
    public event Action<int>? NodeActivated;
    public event Action<int>? NodeHovered;
    public event Action<int>? FolderHovered;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        ToolTip = "Hover a file to highlight its folder group. Select a tile to inspect it; double-click any tile to zoom into its folder group.";
        AutomationProperties.SetName(this, "Disk usage treemap. A keyboard-accessible representation is available in the file table.");
        resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); Rebuild(); };
        SizeChanged += (_, _) => { resizeTimer.Stop(); resizeTimer.Start(); };
        Unloaded += (_, _) => { resizeTimer.Stop(); layoutCancellation?.Cancel(); };
    }

    public void SetResult(ScanResult? value, int selectedRoot, SizeMetric sizeMetric)
    {
        result = value;
        root = selectedRoot;
        metric = sizeMetric;
        hover = -1;
        ClearHoverGroup();
        selected = -1;
        layout = null;
        InvalidateVisual();
        Rebuild();
    }

    public void SelectNode(int index) { selected = index; InvalidateVisual(); }

    private async void Rebuild()
    {
        layoutCancellation?.Cancel();
        layoutCancellation = null;
        var snapshot = result;
        var size = new Size(ActualWidth, ActualHeight);
        if (snapshot == null || size.Width < 1 || size.Height < 1) return;
        var cancellation = layoutCancellation = new CancellationTokenSource();
        int snapshotRoot = root;
        SizeMetric snapshotMetric = metric;
        try
        {
            var next = await Task.Run(() => BuildLayout(snapshot, snapshotRoot, snapshotMetric, size, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || result != snapshot) return;
            layout = next;
            ClearHoverGroup();
            UpdateHoverGroup(hover);
            InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ToolTip = "Treemap could not be drawn: " + ex.Message; }
        finally { if (layoutCancellation == cancellation) layoutCancellation = null; cancellation.Dispose(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle((Brush)FindResource("WindowBrush"), null, new Rect(RenderSize));
        if (layout == null || layout.Tiles.Length == 0)
        {
            string text = result == null ? "Your disk, at a glance" : "No measured bytes in this folder";
            var caption = Text(text, 15, (Brush)FindResource("MutedBrush"));
            dc.DrawText(caption, new Point(Math.Max(12, (ActualWidth - caption.Width) / 2), Math.Max(12, (ActualHeight - caption.Height) / 2)));
            return;
        }
        dc.PushTransform(new ScaleTransform(ActualWidth / layout.Size.Width, ActualHeight / layout.Size.Height));
        foreach (var tile in layout.Tiles)
        {
            dc.DrawRectangle(TreemapPalette.BrushAt(tile.Color), TilePen, tile.Bounds);
            if (result != null && tile.Bounds.Width > 84 && tile.Bounds.Height > 24)
            {
                string label = tile.Remainder ? "More files / …" : tile.Grouped ? result.Nodes[tile.Index].Name + " / …" : result.Nodes[tile.Index].Name;
                var caption = Text(label, 10.5, TextBrush);
                caption.MaxTextWidth = Math.Max(1, tile.Bounds.Width - 10);
                caption.MaxTextHeight = Math.Max(1, tile.Bounds.Height - 7);
                caption.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(caption, new Point(tile.Bounds.X + 5, tile.Bounds.Y + 3));
            }
        }
        if (hoverGroupGeometry != null)
        {
            dc.DrawGeometry(GroupFill, null, hoverGroupGeometry);
            var groupBounds = hoverGroupBounds;
            groupBounds.Inflate(-2, -2);
            if (!groupBounds.IsEmpty && groupBounds.Width > 0 && groupBounds.Height > 0)
            {
                dc.DrawRectangle(null, GroupShadowPen, groupBounds);
                dc.DrawRectangle(null, GroupPen, groupBounds);
            }
        }
        foreach (var tile in layout.Tiles)
        {
            if (tile.Index != selected && tile.Index != hover) continue;
            var bounds = tile.Bounds;
            bounds.Inflate(-1.3, -1.3);
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) dc.DrawRectangle(null, SelectedPen, bounds);
        }
        dc.Pop();
    }

    private FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = HitTile(e.GetPosition(this));
        int index = tile?.Index ?? -1;
        if (index == hover) return;
        hover = index;
        UpdateHoverGroup(index);
        if (index >= 0 && result != null)
        {
            string folder = hoverFolder >= 0 ? result.GetPath(hoverFolder) : "";
            ToolTip = $"{result.GetPath(index)}\n{FormatUtil.Bytes(result.Value(index, metric))}" +
                (tile?.Remainder == true ? "\nAdditional items in this folder; explore them in the table" :
                 result.Nodes[index].IsDirectory ? "\nFolder group · double-click to zoom" : $"\nFolder group: {folder}\nDouble-click to zoom into this folder");
            NodeHovered?.Invoke(index);
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); hover = -1; ClearHoverGroup(); InvalidateVisual(); }

    private void ClearHoverGroup(bool notify = true)
    {
        int previous = hoverFolder;
        hoverFolder = -1;
        hoverGroupGeometry = null;
        hoverGroupBounds = Rect.Empty;
        if (notify && previous >= 0) FolderHovered?.Invoke(-1);
    }

    private void UpdateHoverGroup(int index)
    {
        if (layout == null || result == null || index < 0) { ClearHoverGroup(); return; }
        int folder = result.Nodes[index].IsDirectory ? index : result.Nodes[index].ParentIndex;
        if (folder == hoverFolder) return;
        ClearHoverGroup(false);
        if (!layout.FolderGroups.TryGetValue(folder, out var group)) { FolderHovered?.Invoke(-1); return; }
        hoverFolder = folder;
        hoverGroupBounds = group.Bounds;
        // Geometry is built once per folder transition, not for every tile or mouse movement.
        var geometry = new GeometryGroup();
        foreach (var bounds in group.FileTiles) geometry.Children.Add(new RectangleGeometry(bounds));
        geometry.Freeze();
        hoverGroupGeometry = geometry;
        FolderHovered?.Invoke(folder);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int index = Hit(e.GetPosition(this));
        if (index < 0) return;
        selected = index;
        NodeSelected?.Invoke(index);
        if (e.ClickCount == 2) ActivateFolderGroup(index);
        InvalidateVisual();
    }
    private void ActivateFolderGroup(int index)
    {
        if (result == null || (uint)index >= (uint)result.Count) return;
        int folder = result.Nodes[index].IsDirectory ? index : result.Nodes[index].ParentIndex;
        if ((uint)folder < (uint)result.Count && folder != root) NodeActivated?.Invoke(folder);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (layout == null || layout.Tiles.Length == 0) return;
        if (e.Key == Key.Enter && selected >= 0) { ActivateFolderGroup(selected); e.Handled = true; }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int position = Array.FindIndex(layout.Tiles, tile => tile.Index == selected);
            position = Math.Clamp(position + (e.Key is Key.Left or Key.Up ? -1 : 1), 0, layout.Tiles.Length - 1);
            selected = layout.Tiles[position].Index;
            NodeSelected?.Invoke(selected);
            InvalidateVisual();
            e.Handled = true;
        }
    }
    private int Hit(Point point) => HitTile(point)?.Index ?? -1;
    private MapTile? HitTile(Point point)
    {
        if (layout == null || ActualWidth <= 0 || ActualHeight <= 0) return null;
        point = new Point(point.X * layout.Size.Width / ActualWidth, point.Y * layout.Size.Height / ActualHeight);
        for (int i = layout.Tiles.Length - 1; i >= 0; i--) if (layout.Tiles[i].Bounds.Contains(point)) return layout.Tiles[i];
        return null;
    }

    internal static MapLayout BuildLayout(ScanResult result, int root, SizeMetric metric, Size size, CancellationToken ct)
    {
        var tiles = new List<MapTile>();
        var folderBounds = new Dictionary<int, Rect>();
        var pending = new Stack<(int Index, Rect Bounds, int Depth)>();
        pending.Push((root, new Rect(size), 0));
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (result.Nodes[folder.Index].IsDirectory) folderBounds[folder.Index] = folder.Bounds;
            if (!result.Nodes[folder.Index].IsDirectory || folder.Depth >= 12 || folder.Bounds.Width < 6 || folder.Bounds.Height < 6 || tiles.Count + pending.Count > 12000)
            {
                Add(folder.Index, folder.Bounds, result.Nodes[folder.Index].IsDirectory);
                continue;
            }
            const int childLimit = 4096;
            var largest = new PriorityQueue<int, long>();
            double total = 0;
            int childCount = 0;
            foreach (int child in result.Children(folder.Index))
            {
                if ((childCount & 4095) == 0) ct.ThrowIfCancellationRequested();
                long value = result.Value(child, metric);
                if (value <= 0) continue;
                childCount++;
                total += value;
                if (largest.Count < childLimit) largest.Enqueue(child, value);
                else largest.EnqueueDequeue(child, value);
            }
            var children = largest.UnorderedItems.Select(item => item.Element).ToArray();
            Array.Sort(children, (a, b) => result.Value(b, metric).CompareTo(result.Value(a, metric)));
            if (children.Length == 0) { if (folder.Index != root) Add(folder.Index, folder.Bounds, true); continue; }
            // Bound work for huge flat directories. The remaining measured bytes share one
            // labeled aggregate tile; zooming into that folder still exposes all table rows.
            int limit = children.Length;
            var weighted = new List<(int Index, double Area, bool Grouped)>(limit + 1);
            double scale = folder.Bounds.Width * folder.Bounds.Height / total;
            for (int i = 0; i < limit; i++) weighted.Add((children[i], result.Value(children[i], metric) * scale, false));
            if (childCount > limit) weighted.Add((folder.Index, Math.Max(0, total - children.Sum(index => (double)result.Value(index, metric))) * scale, true));
            weighted.Sort((a, b) => b.Area.CompareTo(a.Area));
            Squarify(weighted, folder.Bounds, (index, bounds, grouped) =>
            {
                if (bounds.Width < .4 || bounds.Height < .4) return;
                if (!grouped && result.Nodes[index].IsDirectory && bounds.Width * bounds.Height >= 120 && bounds.Width > 8 && bounds.Height > 8)
                    pending.Push((index, bounds, folder.Depth + 1));
                else Add(index, bounds, grouped || result.Nodes[index].IsDirectory, grouped);
            });
        }
        var groups = tiles.GroupBy(tile => tile.FolderIndex).ToDictionary(group => group.Key, group =>
            new MapFolderGroup(folderBounds[group.Key], group.Select(tile => tile.Bounds).ToArray()));
        return new(tiles.ToArray(), size, groups);

        void Add(int index, Rect bounds, bool grouped, bool remainder = false)
        {
            bool directory = result.Nodes[index].IsDirectory;
            int folder = directory ? index : result.Nodes[index].ParentIndex;
            if (directory && !remainder) folderBounds[index] = bounds;
            tiles.Add(new(index, bounds, TreemapPalette.IndexFor(result.Nodes[index].Name), grouped, folder, remainder));
        }
    }

    private static void Squarify(List<(int Index, double Area, bool Grouped)> items, Rect remaining, Action<int, Rect, bool> place)
    {
        var row = new List<(int Index, double Area, bool Grouped)>();
        int next = 0;
        while (next < items.Count)
        {
            double side = Math.Min(remaining.Width, remaining.Height);
            if (side <= .0001) break;
            var candidate = items[next];
            if (row.Count == 0 || Worst(row, side, candidate.Area) <= Worst(row, side, null)) { row.Add(candidate); next++; }
            else { remaining = PlaceRow(row, remaining, place); row.Clear(); }
        }
        if (row.Count > 0) PlaceRow(row, remaining, place);
    }
    private static double Worst(List<(int Index, double Area, bool Grouped)> row, double side, double? extra)
    {
        double sum = extra ?? 0, min = extra ?? double.MaxValue, max = extra ?? 0;
        foreach (var item in row) { sum += item.Area; min = Math.Min(min, item.Area); max = Math.Max(max, item.Area); }
        if (sum <= 0 || min <= 0) return double.MaxValue;
        double squared = sum * sum, sideSquared = side * side;
        return Math.Max(sideSquared * max / squared, squared / (sideSquared * min));
    }
    private static Rect PlaceRow(List<(int Index, double Area, bool Grouped)> row, Rect bounds, Action<int, Rect, bool> place)
    {
        double sum = row.Sum(item => item.Area);
        if (bounds.Width >= bounds.Height)
        {
            double width = Math.Min(bounds.Width, sum / Math.Max(bounds.Height, .0001)), y = bounds.Y;
            foreach (var item in row) { double height = item.Area / Math.Max(width, .0001); place(item.Index, new Rect(bounds.X, y, width, height), item.Grouped); y += height; }
            return new Rect(bounds.X + width, bounds.Y, Math.Max(0, bounds.Width - width), bounds.Height);
        }
        else
        {
            double height = Math.Min(bounds.Height, sum / Math.Max(bounds.Width, .0001)), x = bounds.X;
            foreach (var item in row) { double width = item.Area / Math.Max(height, .0001); place(item.Index, new Rect(x, bounds.Y, width, height), item.Grouped); x += width; }
            return new Rect(bounds.X, bounds.Y + height, bounds.Width, Math.Max(0, bounds.Height - height));
        }
    }
    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double thickness) { var pen = new Pen(FrozenBrush(color), thickness); pen.Freeze(); return pen; }
    internal int TileCount => layout?.Tiles.Length ?? 0;
    internal bool IsLayoutPending => layoutCancellation != null;
    internal bool LayoutBoundsValid => layout == null || layout.Tiles.All(tile => double.IsFinite(tile.Bounds.X) && double.IsFinite(tile.Bounds.Y) && double.IsFinite(tile.Bounds.Width) && double.IsFinite(tile.Bounds.Height) && tile.Bounds.X >= -.01 && tile.Bounds.Y >= -.01 && tile.Bounds.Right <= layout.Size.Width + .01 && tile.Bounds.Bottom <= layout.Size.Height + .01);
}
