namespace DiskPeek.Core;

/// <summary>Builds compact adjacency and totals without one object/viewmodel per filesystem entry.</summary>
public static class ScanAggregation
{
    public static ScanResult Complete(ScanResult result)
    {
        var nodes = result.Nodes;
        int n = nodes.Length;
        result.FirstChild = new int[n];
        result.NextSibling = new int[n];
        Array.Fill(result.FirstChild, -1);
        Array.Fill(result.NextSibling, -1);
        result.LogicalTotals = new long[n];
        result.AllocatedTotals = new long[n];
        result.FileCounts = new int[n];
        result.DirectoryCounts = new int[n];
        result.UnknownCounts = new int[n];
        if (n == 0) return result;
        nodes[0].ParentIndex = -1;
        // Parents may occur after children in the MFT. Build a traversal, rather than relying on record order.
        for (int i = n - 1; i > 0; i--)
        {
            int p = nodes[i].ParentIndex;
            if (p < 0 || p >= n || p == i || !nodes[p].IsDirectory)
            {
                p = 0;
                nodes[i].ParentIndex = p;
                result.Partial = true;
            }
            result.NextSibling[i] = result.FirstChild[p];
            result.FirstChild[p] = i;
        }
        var owners = new Dictionary<FileIdentity, int>();
        for (int i = 0; i < n; i++)
        {
            ref var node = ref nodes[i];
            node.Flags &= ~NodeFlags.SharedAllocation;
            if (node.IsDirectory) continue;
            if (!node.Identity.IsKnown)
            {
                result.HardLinkAccountingAvailable = false;
                node.Flags |= NodeFlags.UnstableIdentity;
                continue;
            }
            if (!owners.TryGetValue(node.Identity, out int existing)) owners.Add(node.Identity, i);
            else
            {
                var comparison = ComparePaths(result, i, existing);
                if (comparison < 0)
                {
                    nodes[existing].Flags |= NodeFlags.SharedAllocation;
                    owners[node.Identity] = i;
                }
                else node.Flags |= NodeFlags.SharedAllocation;
            }
        }
        var order = new int[n];
        var seen = new bool[n];
        int used = 1;
        order[0] = 0;
        seen[0] = true;
        for (int head = 0; head < used; head++)
        {
            int current = order[head];
            for (int child = result.FirstChild[current]; child >= 0; child = result.NextSibling[child])
            {
                if (seen[child]) throw new InvalidDataException("Scan contains a parent cycle.");
                seen[child] = true;
                order[used++] = child;
            }
        }
        // Disconnected cycles are not silently omitted from totals.
        if (used != n) throw new InvalidDataException("Scan contains disconnected cyclic parent references.");
        for (int step = used - 1; step >= 0; step--)
        {
            int i = order[step];
            ref var node = ref nodes[i];
            if (node.IsDirectory)
            {
                // A skipped directory is an unmeasured subtree, not an empty folder.
                if ((node.Flags & (NodeFlags.Inaccessible | NodeFlags.ReparseSkipped)) != 0)
                    result.UnknownCounts[i]++;
            }
            else
            {
                result.LogicalTotals[i] = Math.Max(0, node.LogicalBytes);
                result.AllocatedTotals[i] = node.AllocatedBytes < 0 || (node.Flags & NodeFlags.SharedAllocation) != 0 ? 0 : node.AllocatedBytes;
                result.FileCounts[i] = 1;
                if (node.AllocatedBytes < 0) result.UnknownCounts[i] = 1;
            }
            int p = node.ParentIndex;
            if (p >= 0)
            {
                result.LogicalTotals[p] = checked(result.LogicalTotals[p] + result.LogicalTotals[i]);
                result.AllocatedTotals[p] = checked(result.AllocatedTotals[p] + result.AllocatedTotals[i]);
                result.FileCounts[p] = checked(result.FileCounts[p] + result.FileCounts[i]);
                result.DirectoryCounts[p] = checked(result.DirectoryCounts[p] + result.DirectoryCounts[i] + (node.IsDirectory ? 1 : 0));
                result.UnknownCounts[p] = checked(result.UnknownCounts[p] + result.UnknownCounts[i]);
            }
        }
        result.Partial |= result.UnknownCounts[0] > 0 || !result.HardLinkAccountingAvailable;
        return result;
    }
    private static int ComparePaths(ScanResult result, int a, int b)
    {
        string pa = result.GetPath(a), pb = result.GetPath(b);
        int c = StringComparer.OrdinalIgnoreCase.Compare(pa, pb);
        return c != 0 ? c : StringComparer.Ordinal.Compare(pa, pb);
    }
}
