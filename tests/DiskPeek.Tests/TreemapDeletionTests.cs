using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskPeek.App;
using DiskPeek.Core;

internal static partial class Verification
{
    private static async Task RunTreemapDeletionTests()
    {
        await Run("Treemap: tiny deleted leaf leaves sibling pixels and ancestor borders unchanged", TestTreemapTinyLeaf);
        await Run("Treemap: collapsed and four-DIP tiles paint only the deleted byte fraction", TestTreemapCollapsed);
        await Run("Treemap: remainder excludes drawn siblings and counts nested deleted bytes once", TestTreemapRemainderAccounting);
        await Run("Treemap: unknown, resident and shared allocation follow the selected metric", TestTreemapAccounting);
        await Run("Treemap: real large-directory remainder paints proportional red area", TestTreemapRemainderPixels);
        await Run("Treemap: deletion bounds preserve area and overlay calculation is cancellable", TestTreemapDeletionGeometry);
    }

    private static Task TestTreemapTinyLeaf() => OnTreemapSta(() =>
    {
        var result = Model(Dir("root", -1), Dir("parent", 0), FileNode("direct.bin", 1, 9000, 9000, 1),
            Dir("child", 1), FileNode("tiny.tmp", 3, 10, 10, 2), FileNode("kept.dat", 3, 990, 990, 3),
            Dir("other", 0), FileNode("other.log", 6, 10000, 10000, 4));
        var map = CreateTreemap(result, SizeMetric.Allocated, new Size(800, 500));
        var before = CaptureTreemap(map);
        var layout = CurrentMapLayout(map);
        var tiny = layout.Tiles.Single(tile => tile.Index == 4);
        Assert(!tiny.Grouped, "The small-file regression must exercise an individually drawn leaf.");
        SetTreemapDeletion(map, result, DeletionState(result, 4));
        var after = CaptureTreemap(map);
        Assert(CountMapColor(before, 188, 45, 62) == 0 && CountMapColor(after, 188, 45, 62) > 0, "Deleted leaf did not acquire its own red fill.");
        var allowed = tiny.Bounds; allowed.Inflate(1, 1);
        Assert(UnchangedOutside(before, after, allowed), "Deleting a tiny leaf repainted another tile or a whole ancestor frame.");
        Assert(CountMapColor(after, 255, 112, 124) == 0, "The old full-folder red outline returned.");
        SaveTreemapEvidence(after, "treemap-tiny-leaf.png");
    });

    private static Task TestTreemapCollapsed() => OnTreemapSta(() =>
    {
        var nodes = new List<ScanNode> { Dir("root", -1) };
        for (int i = 1; i <= 12; i++) nodes.Add(Dir("folder-" + i, i - 1));
        nodes.Add(FileNode("tiny.bin", 12, 1, 1, 1));
        nodes.Add(FileNode("remaining.bin", 12, 99, 99, 2));
        var result = Model(nodes.ToArray());
        var map = CreateTreemap(result, SizeMetric.Allocated, new Size(1000, 200));
        Assert(CurrentMapLayout(map).Tiles is [{ Index: 12, Grouped: true }], "The depth limit did not produce the intended collapsed folder.");
        SetTreemapDeletion(map, result, DeletionState(result, 13));
        var partial = CaptureTreemap(map);
        AssertMapRedArea(partial, 2000, 160, "One percent of a collapsed folder");
        Assert(CountMapColor(partial, 255, 112, 124) == 0, "A collapsed folder received a full red border.");
        SaveTreemapEvidence(partial, "treemap-collapsed-one-percent.png");
        SetTreemapDeletion(map, result, DeletionState(result, 12));
        AssertMapRedArea(CaptureTreemap(map), 200000, 4000, "A wholly deleted collapsed folder");

        // The old border/stripe could consume a thin tile in either orientation.
        foreach (var size in new[] { new Size(4, 400), new Size(400, 4) })
        {
            var thin = CreateTreemap(result, SizeMetric.Allocated, size);
            Assert(CurrentMapLayout(thin).Tiles is [{ Index: 0, Grouped: true }], "The narrow fixture must collapse the root.");
            SetTreemapDeletion(thin, result, DeletionState(result, 13));
            AssertMapRedArea(CaptureTreemap(thin), 16, 1, "One percent of a four-DIP-thick tile");
        }
    });

    private static Task TestTreemapRemainderAccounting()
    {
        var result = Model(Dir("root", -1), FileNode("drawn.bin", 0, 900, 900, 1), Dir("omitted", 0),
            FileNode("tiny.bin", 2, 10, 10, 2), FileNode("kept.bin", 2, 90, 90, 3),
            FileNode("empty.bin", 0, 0, 0, 4), FileNode("unknown.bin", 0, 1000, -1, 5),
            FileNode("a-owner.bin", 0, 200, 200, 77), FileNode("z-shared.bin", 0, 200, 200, 77));
        var layout = OneAggregate(0, remainder: true, excluded: [1, 7]);
        var state = DeletionState(result, 1, 3, 5, 6, 8);
        AssertFraction(result, layout, state, SizeMetric.Allocated, .1, "Remainder used drawn, unknown, zero or shared bytes as its denominator/numerator.");
        AssertFraction(result, layout, state, SizeMetric.Logical, 1210d / 1300, "Logical remainder did not include the omitted logical sizes.");
        AssertFraction(result, layout, DeletionState(result, 1), SizeMetric.Allocated, 0, "A drawn sibling colored the remainder.");
        AssertFraction(result, layout, DeletionState(result, 2, 3), SizeMetric.Allocated, 1, "A deleted directory and its descendant were double-counted or omitted.");
        AssertFraction(result, layout, DeletionState(result, 2, 3), SizeMetric.Logical, 100d / 1300, "A nested omitted subtree was charged twice.");
        return Done();
    }

    private static Task TestTreemapAccounting()
    {
        var result = Model(Dir("root", -1), FileNode("measured.bin", 0, 100, 100, 1),
            FileNode("unknown.bin", 0, 900, -1, 2), FileNode("resident.bin", 0, 10, 0, 3),
            FileNode("a-owner.bin", 0, 100, 100, 77), FileNode("z-shared.bin", 0, 100, 100, 77));
        Assert(result.Value(0, SizeMetric.Allocated) == 200 && result.Nodes[5].Flags.HasFlag(NodeFlags.SharedAllocation), "The allocation fixture did not establish a shared link.");
        var layout = OneAggregate(0);
        var unmeasured = DeletionState(result, 2, 3, 5);
        AssertFraction(result, layout, unmeasured, SizeMetric.Allocated, 0, "Unknown, resident or uncharged shared storage painted measured allocation red.");
        AssertFraction(result, layout, unmeasured, SizeMetric.Logical, 1010d / 1210, "Logical mode lost resident, unknown-allocation or hard-link logical bytes.");
        AssertFraction(result, layout, DeletionState(result, 1), SizeMetric.Allocated, .5, "Measured allocation did not determine the collapsed red share.");
        Assert(result.Value(0, SizeMetric.Allocated) == 200 && result.Value(0, SizeMetric.Logical) == 1210, "Deletion painting mutated scan-time totals.");
        return Done();
    }

    private static Task TestTreemapRemainderPixels() => OnTreemapSta(() =>
    {
        const int drawn = 4096, omitted = 4000;
        var nodes = new List<ScanNode> { Dir("root", -1) };
        for (int i = 0; i < drawn; i++) nodes.Add(FileNode($"large-{i:D4}.bin", 0, 100, 100, (ulong)i + 1));
        for (int i = 0; i < omitted; i++) nodes.Add(FileNode($"small-{i:D4}.log", 0, 50, 50, (ulong)(drawn + i + 1)));
        var result = Model(nodes.ToArray());
        var map = CreateTreemap(result, SizeMetric.Allocated, new Size(1000, 600));
        var layout = CurrentMapLayout(map);
        var remainder = layout.Tiles.Single(tile => tile.Remainder);
        Assert(layout.RemainderExcludedRoots[0].Count == drawn, "The production child limit did not create the intended remainder.");
        var before = CaptureTreemap(map);
        var state = DeletionState(result, Enumerable.Range(drawn + 1, 40).ToArray());
        SetTreemapDeletion(map, result, state);
        var after = CaptureTreemap(map);
        double area = remainder.Bounds.Width * remainder.Bounds.Height * .01;
        AssertMapRedArea(after, area, Math.Max(100, remainder.Bounds.Width + remainder.Bounds.Height), "One percent of the omitted bytes");
        var allowed = TreemapControl.GetDeletionBounds(remainder.Bounds, .01); allowed.Inflate(1, 1);
        Assert(UnchangedOutside(before, after, allowed), "Remainder deletion repainted the whole aggregate or explicitly drawn siblings.");
        Assert(CountMapColor(after, 255, 112, 124) == 0, "The remainder acquired the old whole-tile outline.");
        SaveTreemapEvidence(after, "treemap-remainder-one-percent.png");
    });

    private static Task TestTreemapDeletionGeometry()
    {
        foreach (var bounds in new[] { new Rect(17, 23, 1000, 200), new Rect(17, 23, 4, 400), new Rect(17, 23, 200, 1000) })
        foreach (double fraction in new[] { 0, .0001, .01, .5, 1 })
        {
            var red = TreemapControl.GetDeletionBounds(bounds, fraction);
            Assert(Math.Abs(red.Width * red.Height - bounds.Width * bounds.Height * fraction) < .000001, "Overlay geometry changed the requested byte fraction.");
            Assert(red.Left >= bounds.Left && red.Top >= bounds.Top && red.Right <= bounds.Right && red.Bottom <= bounds.Bottom, "Overlay escaped its own tile.");
        }
        Assert(TreemapControl.GetDeletionBounds(new Rect(0, 0, 10, 20), -1).Height == 0, "Negative fractions were not clamped.");
        Assert(TreemapControl.GetDeletionBounds(new Rect(0, 0, 10, 20), 2).Height == 20, "Excess fractions were not clamped.");
        var result = Model(Dir("root", -1), FileNode("tiny.bin", 0, 1, 1, 1));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { TreemapControl.BuildDeletionOverlay(result, OneAggregate(0), SizeMetric.Allocated, DeletionState(result, 1), cancellation.Token); throw new Exception("Cancelled overlay work completed."); }
        catch (OperationCanceledException) { }
        return Done();
    }

    private static MapLayout OneAggregate(int index, bool remainder = false, int[]? excluded = null) => new(
        [new(index, new Rect(0, 0, 1000, 200), 0, true, index, remainder)], new Size(1000, 200),
        new Dictionary<int, MapFolderGroup>(), remainder ? new Dictionary<int, HashSet<int>> { [index] = [.. excluded ?? []] } : new());

    private static ScanDeletionState DeletionState(ScanResult result, params int[] removedRoots)
    {
        var state = new ScanDeletionState(result.Count, new DeletionTrackingHealth());
        var pending = new Stack<int>(removedRoots);
        while (pending.TryPop(out int index))
        {
            if (state.IsDeleted(index)) continue;
            state.SetDeleted(index);
            foreach (int child in result.Children(index)) pending.Push(child);
        }
        for (int i = 0; i < result.Count; i++)
        {
            if (!state.IsDeleted(i)) continue;
            state.AddTotal(1);
            for (int parent = i; parent >= 0; parent = result.Nodes[parent].ParentIndex) state.AddCount(parent, 1);
        }
        return state;
    }

    private static void AssertFraction(ScanResult result, MapLayout layout, ScanDeletionState state, SizeMetric metric, double expected, string message)
    {
        var fractions = TreemapControl.BuildDeletionOverlay(result, layout, metric, state, CancellationToken.None);
        Assert(fractions.Length == layout.Tiles.Length && Math.Abs(fractions[0] - expected) < 1e-12, message + $" Expected {expected:R}; observed {fractions[0]:R}.");
    }

    private static Task OnTreemapSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "Treemap rendering regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static TreemapControl CreateTreemap(ScanResult result, SizeMetric metric, Size size)
    {
        var map = new TreemapControl { Width = size.Width, Height = size.Height };
        map.Resources["WindowBrush"] = Brushes.White;
        map.Resources["MutedBrush"] = Brushes.Gray;
        map.Measure(size); map.Arrange(new Rect(size)); map.UpdateLayout();
        map.SetResult(result, 0, metric);
        // Let the production resize debounce finish as well as the asynchronous layout.
        var clock = Stopwatch.StartNew();
        PumpTreemapUntil(() => clock.ElapsedMilliseconds >= 220 && !map.IsLayoutPending && map.TileCount > 0);
        Assert(map.LayoutBoundsValid, "Production treemap emitted invalid geometry.");
        return map;
    }

    private static MapLayout CurrentMapLayout(TreemapControl map) => (MapLayout)typeof(TreemapControl).GetField("layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!;

    private static void SetTreemapDeletion(TreemapControl map, ScanResult result, ScanDeletionState state)
    {
        map.SetDeletionState(state);
        // This private field is observed only to wait for the real asynchronous renderer;
        // tests never inject layout, drawing commands, or the calculated overlay.
        var pending = typeof(TreemapControl).GetField("deletionOverlayCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PumpTreemapUntil(() => pending.GetValue(map) == null && !map.IsLayoutPending);
    }

    private static void PumpTreemapUntil(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Treemap dispatcher work did not finish.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }

    private sealed record MapPixels(int Width, int Height, byte[] Bytes);
    private static MapPixels CaptureTreemap(TreemapControl map)
    {
        map.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)map.ActualWidth, (int)map.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(map);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static int CountMapColor(MapPixels pixels, byte r, byte g, byte b)
    {
        int count = 0;
        for (int i = 0; i < pixels.Bytes.Length; i += 4)
            if (pixels.Bytes[i] == b && pixels.Bytes[i + 1] == g && pixels.Bytes[i + 2] == r && pixels.Bytes[i + 3] == 255) count++;
        return count;
    }

    private static void AssertMapRedArea(MapPixels pixels, double expected, double tolerance, string context)
    {
        int actual = CountMapColor(pixels, 188, 45, 62);
        Assert(Math.Abs(actual - expected) <= tolerance, $"{context} painted {actual:N0} exact-red pixels; expected {expected:N1} +/- {tolerance:N1}.");
    }

    private static bool UnchangedOutside(MapPixels before, MapPixels after, Rect allowed)
    {
        if (before.Width != after.Width || before.Height != after.Height) return false;
        for (int y = 0; y < before.Height; y++)
        for (int x = 0; x < before.Width; x++)
        {
            if (allowed.Contains(new Point(x + .5, y + .5))) continue;
            int offset = (y * before.Width + x) * 4;
            if (!before.Bytes.AsSpan(offset, 4).SequenceEqual(after.Bytes.AsSpan(offset, 4))) return false;
        }
        return true;
    }

    private static void SaveTreemapEvidence(MapPixels pixels, string name)
    {
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null, pixels.Bytes, pixels.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(Path.Combine(FixtureRoot, name)); encoder.Save(stream);
    }
}
