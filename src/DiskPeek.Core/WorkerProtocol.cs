using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("DiskPeek.Worker")]
[assembly: InternalsVisibleTo("DiskPeek.Tests")]

namespace DiskPeek.Core;

// This protocol only carries read-only scan requests and scan data. No paths to open for writing exist.
internal static class WorkerProtocol
{
    internal const int Version = 1;
    internal const int MaximumFrame = 8 * 1024 * 1024;
    internal enum Kind : byte { Hello = 1, Request = 2, Cancel = 3, Progress = 4, Nodes = 5, Complete = 6, Error = 7 }
    internal readonly record struct Frame(Kind Kind, byte[] Payload)
    {
        internal BinaryReader Reader() => new(new MemoryStream(Payload, false), Encoding.UTF8, false);
    }
    internal sealed class Writer(Stream stream) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        internal async Task SendAsync(Kind kind, Action<BinaryWriter>? write = null, CancellationToken token = default)
        {
            using var memory = new MemoryStream();
            memory.WriteByte((byte)kind);
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true)) write?.Invoke(writer);
            if (memory.Length > MaximumFrame) throw new InvalidDataException("Worker frame is too large.");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, checked((int)memory.Length));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(header, token).ConfigureAwait(false);
                await stream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        public void Dispose() => gate.Dispose();
    }

    internal static async Task<Frame> ReceiveAsync(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaximumFrame) throw new InvalidDataException("Invalid worker frame length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        if (!Enum.IsDefined((Kind)data[0])) throw new InvalidDataException("Unknown worker message.");
        return new((Kind)data[0], data[1..]);
    }

    internal static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 131072) throw new InvalidDataException("Worker string too long.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    internal static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 131072 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid worker string length.");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    internal static void WriteNode(BinaryWriter writer, ScanNode node)
    {
        WriteText(writer, node.Name); writer.Write(node.ParentIndex); writer.Write(node.Identity.Low);
        writer.Write(node.Identity.High); writer.Write(node.LogicalBytes); writer.Write(node.AllocatedBytes);
        writer.Write(node.ModifiedUtcTicks); writer.Write((uint)node.Attributes); writer.Write((int)node.Flags);
    }
    internal static ScanNode ReadNode(BinaryReader reader) => new()
    {
        Name = ReadText(reader), ParentIndex = reader.ReadInt32(),
        Identity = new(reader.ReadUInt64(), reader.ReadUInt64()), LogicalBytes = reader.ReadInt64(),
        AllocatedBytes = reader.ReadInt64(), ModifiedUtcTicks = reader.ReadInt64(),
        Attributes = (FileAttributes)reader.ReadUInt32(), Flags = (NodeFlags)reader.ReadInt32()
    };

    internal static void WriteProgress(BinaryWriter writer, ScanProgress progress)
    {
        writer.Write(progress.Entries); writer.Write(progress.Directories); writer.Write(progress.Elapsed.Ticks);
        WriteText(writer, progress.Phase); WriteText(writer, progress.Engine);
    }
    internal static ScanProgress ReadProgress(BinaryReader reader) => new(reader.ReadInt64(), reader.ReadInt64(),
        TimeSpan.FromTicks(reader.ReadInt64()), ReadText(reader), ReadText(reader));

    internal static void WriteCompletion(BinaryWriter writer, ScanResult result)
    {
        writer.Write(result.Count); WriteText(writer, result.Target); WriteText(writer, result.Engine);
        writer.Write(result.Elapsed.Ticks); writer.Write(result.CompletedUtc.Ticks);
        writer.Write(result.Cancelled); writer.Write(result.Partial); writer.Write(result.HardLinkAccountingAvailable);
        writer.Write(result.SkippedEntries); writer.Write(result.VolumeCapacity ?? -1); writer.Write(result.VolumeFree ?? -1);
        writer.Write(Math.Min(1000, result.Issues.Count));
        foreach (var issue in result.Issues.Take(1000)) { WriteText(writer, issue.Path); WriteText(writer, issue.Message); }
    }
    internal static ScanResult ReadCompletion(BinaryReader reader, List<ScanNode> nodes, string expectedTarget)
    {
        int count = reader.ReadInt32();
        string target = ReadText(reader), engine = ReadText(reader);
        if (count != nodes.Count || count < 1 || !StringComparer.OrdinalIgnoreCase.Equals(target, expectedTarget))
            throw new InvalidDataException("Worker result did not match its request.");
        var elapsed = TimeSpan.FromTicks(reader.ReadInt64());
        var completed = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        bool cancelled = reader.ReadBoolean(), partial = reader.ReadBoolean(), stable = reader.ReadBoolean();
        long skipped = reader.ReadInt64(), capacity = reader.ReadInt64(), free = reader.ReadInt64();
        int issueCount = reader.ReadInt32();
        if (issueCount < 0 || issueCount > 1000) throw new InvalidDataException("Invalid worker issue count.");
        var issues = new List<ScanIssue>(issueCount);
        for (int i = 0; i < issueCount; i++) issues.Add(new(ReadText(reader), ReadText(reader)));
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if ((i == 0 && node.ParentIndex != -1) || (i > 0 && (node.ParentIndex < 0 || node.ParentIndex >= count)) ||
                node.LogicalBytes < 0 || node.AllocatedBytes < -1)
                throw new InvalidDataException("Invalid worker scan node.");
        }
        return new() { Target = target, Nodes = nodes.ToArray(), Engine = engine, Elapsed = elapsed,
            CompletedUtc = completed, Cancelled = cancelled, Partial = partial, HardLinkAccountingAvailable = stable,
            SkippedEntries = skipped, VolumeCapacity = capacity < 0 ? null : capacity,
            VolumeFree = free < 0 ? null : free, Issues = issues };
    }
}
