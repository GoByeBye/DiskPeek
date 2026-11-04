using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using DiskPeek.Core;

namespace DiskPeek.App;

internal readonly record struct FlatEntry(int Index, int Depth);
internal sealed record TypeSummary(string Extension, long Bytes, int Files, double Percentage, Brush Color)
{
    public string Size => FormatUtil.Bytes(Bytes);
    public string Count => $"{Files:N0} files";
    public string Share => $"{Percentage:P1}";
    public string DisplayName => Extension == "" ? "No extension" : Extension;
}

// Row adapters are made only when WPF requests an item; the backing collection is an array
// of two integers per visible row, not an object graph containing every scanned file.
internal sealed class NodeRows(ScanResult result, FlatEntry[] entries, SizeMetric metric, int root, HashSet<int> expanded) : IList
{
    public object? this[int index] { get => new NodeRow(result, entries[index], metric, root, expanded.Contains(entries[index].Index)); set => throw new NotSupportedException(); }
    public int Count => entries.Length;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (value is not NodeRow row) return -1;
        for (int i = 0; i < entries.Length; i++) if (entries[i].Index == row.Index) return i;
        return -1;
    }
    public int IndexOfNode(int index) => Array.FindIndex(entries, entry => entry.Index == index);
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]!; }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

internal sealed class NodeRow(ScanResult result, FlatEntry entry, SizeMetric metric, int root, bool expanded)
{
    public int Index => entry.Index;
    public string Name => Index == 0 ? result.Target : result.Nodes[Index].Name;
    public string Size => metric == SizeMetric.Allocated ? Allocated : Logical;
    public string Logical => FormatUtil.Bytes(result.Value(Index, SizeMetric.Logical));
    public string Allocated => ResultView.AllocatedSize(result, Index);
    public string Files => result.Nodes[Index].IsDirectory ? $"{result.FileCounts[Index]:N0}" : "—";
    public string Share => result.Value(root, metric) > 0 ? $"{(double)result.Value(Index, metric) / result.Value(root, metric):P1}" : "—";
    public bool IsDirectory => result.Nodes[Index].IsDirectory;
    public string ExpandGlyph => IsDirectory ? expanded ? "▾" : "▸" : "";
    public string KindGlyph => IsDirectory ? "▰" : "▪";
    public Brush KindBrush => IsDirectory ? (Brush)Application.Current.FindResource("AccentBrush") : TreemapPalette.BrushFor(result.Nodes[Index].Name);
    public Thickness Indent => new(entry.Depth * 17, 0, 0, 0);
    public string Details
    {
        get
        {
            var node = result.Nodes[Index];
            var flags = new List<string>();
            if ((node.Flags & NodeFlags.SharedAllocation) != 0) flags.Add("Hard link: shared allocation is charged to one link only.");
            if ((node.Flags & NodeFlags.Inaccessible) != 0) flags.Add("Some metadata could not be read.");
            if ((node.Flags & NodeFlags.ReparseSkipped) != 0) flags.Add("Link or mount point: its target was not traversed.");
            if ((node.Flags & NodeFlags.AdditionalStreams) != 0) flags.Add("Additional NTFS data streams exist; these sizes cover the unnamed stream only.");
            if (result.UnknownCounts[Index] > 0) flags.Add($"{result.UnknownCounts[Index]:N0} allocated sizes are unknown.");
            return $"{result.GetPath(Index)}\nLogical: {Logical}    Allocated: {Allocated}" + (flags.Count == 0 ? "" : "\n" + string.Join("\n", flags));
        }
    }
}

internal static class ResultView
{
    public static string AllocatedSize(ScanResult result, int index) => !result.Nodes[index].IsDirectory && result.Nodes[index].AllocatedBytes < 0 ? "—" : FormatUtil.Bytes(result.Value(index, SizeMetric.Allocated)) + (result.UnknownCounts[index] > 0 ? " + ?" : "");
    public static FlatEntry[] BuildRows(ScanResult result, int root, SizeMetric metric, HashSet<int> expanded, string query, bool largest, string sort, bool descending, CancellationToken ct, string? extensionFilter = null, int contextFolder = -1)
    {
        var match = Matcher(query);
        if (largest)
        {
            var top = new PriorityQueue<int, long>();
            for (int i = 1; i < result.Count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (result.Nodes[i].IsDirectory || !result.IsDescendantOf(i, root) || !match(result.Nodes[i].Name) || extensionFilter != null && !string.Equals(Path.GetExtension(result.Nodes[i].Name), extensionFilter, StringComparison.OrdinalIgnoreCase)) continue;
                long value = result.Value(i, metric);
                if (top.Count < 1000) top.Enqueue(i, value);
                else top.EnqueueDequeue(i, value);
            }
            int[] items = top.UnorderedItems.Select(item => item.Element).ToArray();
            bool rankUnknown = items.Any(i => result.Nodes[i].AllocatedBytes < 0);
            Array.Sort(items, (a, b) => Compare(result, a, b, metric, sort, descending, rankUnknown));
            return items.Select(index => new FlatEntry(index, 0)).ToArray();
        }

        bool[]? included = null;
        if (!string.IsNullOrWhiteSpace(query) || extensionFilter != null)
        {
            included = new bool[result.Count];
            included[root] = true;
            for (int i = 0; i < result.Count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (!match(result.Nodes[i].Name) || !result.IsDescendantOf(i, root) || extensionFilter != null && (result.Nodes[i].IsDirectory || !string.Equals(Path.GetExtension(result.Nodes[i].Name), extensionFilter, StringComparison.OrdinalIgnoreCase))) continue;
                for (int item = i, steps = 0; item >= 0 && !included[item] && steps++ < result.Count; item = result.Nodes[item].ParentIndex) included[item] = true;
            }
            // Keep the hovered directory visible as context without replacing the user's filter.
            if ((uint)contextFolder < (uint)result.Count && result.IsDescendantOf(contextFolder, root))
                for (int item = contextFolder, steps = 0; item >= 0 && steps++ < result.Count; item = result.Nodes[item].ParentIndex)
                    included[item] = true;
        }
        var rows = new List<FlatEntry> { new(root, 0) };
        var stack = new Stack<FlatEntry>();
        if (included != null || expanded.Contains(root)) PushChildren(root, 1);
        while (stack.TryPop(out var row))
        {
            if ((rows.Count & 4095) == 0) ct.ThrowIfCancellationRequested();
            rows.Add(row);
            if (result.Nodes[row.Index].IsDirectory && (included != null || expanded.Contains(row.Index))) PushChildren(row.Index, row.Depth + 1);
        }
        return rows.ToArray();

        void PushChildren(int index, int depth)
        {
            var children = result.Children(index).Where(child => included == null || included[child]).ToArray();
            bool rankUnknown = children.Any(i => !result.Nodes[i].IsDirectory && result.Nodes[i].AllocatedBytes < 0);
            Array.Sort(children, (a, b) => Compare(result, a, b, metric, sort, descending, rankUnknown));
            for (int i = children.Length - 1; i >= 0; i--) stack.Push(new(children[i], depth));
        }
    }

    private static int Compare(ScanResult result, int a, int b, SizeMetric metric, string sort, bool descending, bool rankUnknown)
    {
        bool allocatedSort = sort == "Allocated" || (sort is not ("Name" or "Files" or "Logical") && metric == SizeMetric.Allocated);
        if (rankUnknown && allocatedSort)
        {
            bool unknownA = !result.Nodes[a].IsDirectory && result.Nodes[a].AllocatedBytes < 0;
            bool unknownB = !result.Nodes[b].IsDirectory && result.Nodes[b].AllocatedBytes < 0;
            if (unknownA != unknownB) return unknownA ? 1 : -1;
        }
        int value = sort switch
        {
            "Name" => StringComparer.OrdinalIgnoreCase.Compare(result.Nodes[a].Name, result.Nodes[b].Name),
            "Files" => result.FileCounts[a].CompareTo(result.FileCounts[b]),
            "Allocated" => result.Value(a, SizeMetric.Allocated).CompareTo(result.Value(b, SizeMetric.Allocated)),
            "Logical" => result.Value(a, SizeMetric.Logical).CompareTo(result.Value(b, SizeMetric.Logical)),
            _ => result.Value(a, metric).CompareTo(result.Value(b, metric))
        };
        if (value == 0) value = StringComparer.OrdinalIgnoreCase.Compare(result.Nodes[a].Name, result.Nodes[b].Name);
        return descending ? -value : value;
    }

    private static Func<string, bool> Matcher(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _ => true;
        if (!query.Contains('*') && !query.Contains('?')) return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
        string pattern = "^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return name => regex.IsMatch(name);
    }

    public static TypeSummary[] BuildTypes(ScanResult result, int root, SizeMetric metric, CancellationToken ct)
    {
        var groups = new Dictionary<string, (long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < result.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (result.Nodes[i].IsDirectory || !result.IsDescendantOf(i, root)) continue;
            var extension = Path.GetExtension(result.Nodes[i].Name).ToLowerInvariant();
            if (extension.Length > 30) extension = "(other)";
            if (!groups.ContainsKey(extension) && groups.Count >= 8192) extension = "(other)";
            groups.TryGetValue(extension, out var current);
            groups[extension] = (current.Bytes + Math.Max(0, result.Value(i, metric)), current.Files + 1);
        }
        long total = result.Value(root, metric);
        return groups.OrderByDescending(pair => pair.Value.Bytes).Take(200).Select(pair => new TypeSummary(pair.Key, pair.Value.Bytes, pair.Value.Files, total > 0 ? (double)pair.Value.Bytes / total : 0, TreemapPalette.BrushFor("file" + pair.Key))).ToArray();
    }
}

internal static class TreemapPalette
{
    internal static readonly Color[] Colors = [Color.FromRgb(68, 169, 182), Color.FromRgb(99, 139, 213), Color.FromRgb(147, 116, 196), Color.FromRgb(199, 121, 166), Color.FromRgb(221, 153, 91), Color.FromRgb(172, 177, 94), Color.FromRgb(94, 173, 137), Color.FromRgb(97, 156, 197), Color.FromRgb(172, 130, 110), Color.FromRgb(116, 153, 158), Color.FromRgb(197, 136, 120), Color.FromRgb(110, 132, 180)];
    private static readonly Brush[] Brushes = Colors.Select(color => { var b = new SolidColorBrush(color); b.Freeze(); return (Brush)b; }).ToArray();
    public static int IndexFor(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        uint hash = 2166136261;
        foreach (char c in extension) hash = (hash ^ c) * 16777619;
        return (int)(hash % (uint)Colors.Length);
    }
    public static Brush BrushFor(string name) => Brushes[IndexFor(name)];
    public static Brush BrushAt(int index) => Brushes[index % Brushes.Length];
}
