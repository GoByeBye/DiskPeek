using System.Buffers.Binary;
using System.Text;

namespace DiskPeek.Core;

public readonly record struct MftDataRun(long Vcn, long Lcn, long ClusterCount);
public readonly record struct MftName(ulong ParentReference, string Name, byte Namespace);
public sealed record MftStream(string Name, long StartingVcn, long LogicalSize, bool Resident, IReadOnlyList<MftDataRun> RunList)
{
    public long EndingVcn { get; init; } = -1;
    public ushort AttributeId { get; init; }
}
public sealed class MftRecord
{
    public ulong RecordNumber { get; set; }
    public ushort Sequence { get; set; }
    public ulong Reference => RecordNumber | ((ulong)Sequence << 48);
    public ulong BaseReference { get; set; }
    public bool InUse { get; set; }
    public bool IsDirectory { get; set; }
    public FileAttributes Attributes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public List<MftName> Names { get; } = [];
    public List<MftStream> DataStreams { get; } = [];
    public List<ulong> AttributeReferences { get; } = [];
    public List<MftStream> ExternalAttributeLists { get; } = [];
}

/// <summary>Bounds-checked parser for NTFS 3.x FILE records. Never follows disk offsets itself.</summary>
public static class MftParser
{
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    public static bool TryParseRecord(ReadOnlySpan<byte> data, int bytesPerSector, out MftRecord record, out string? error)
    {
        record = new(); error = null;
        try
        {
            // NTFS multi-sector protection always uses 512-byte strides, including 4Kn volumes.
            const int fixupStride = 512;
            if (data.Length < fixupStride || data.Length > 65536 || bytesPerSector < 512 || (bytesPerSector & (bytesPerSector - 1)) != 0 || data.Length % fixupStride != 0)
                throw new InvalidDataException("Invalid FILE record/sector length.");
            if (!data[..4].SequenceEqual("FILE"u8)) throw new InvalidDataException("Invalid FILE signature.");
            Span<byte> span = data.Length <= 4096 ? stackalloc byte[data.Length] : new byte[data.Length];
            data.CopyTo(span);
            int usaOffset = U16(span, 4), usaCount = U16(span, 6);
            int sectors = span.Length / fixupStride;
            if (usaCount != sectors + 1 || usaOffset < 8 || usaOffset + usaCount * 2 > fixupStride - 2)
                throw new InvalidDataException("Invalid update-sequence array.");
            ushort sequence = U16(span, usaOffset);
            for (int i = 1; i <= sectors; i++)
            {
                int end = i * fixupStride - 2;
                if (U16(span, end) != sequence) throw new InvalidDataException("Torn FILE record (update-sequence mismatch).");
                span[end] = span[usaOffset + i * 2];
                span[end + 1] = span[usaOffset + i * 2 + 1];
            }
            record.Sequence = U16(span, 16);
            record.InUse = (U16(span, 22) & 1) != 0;
            record.IsDirectory = (U16(span, 22) & 2) != 0;
            record.RecordNumber = U32(span, 44);
            record.BaseReference = U64(span, 32);
            int used = checked((int)U32(span, 24));
            int offset = U16(span, 20);
            if (used < 48 || used > span.Length || offset < 48 || offset % 8 != 0 || offset + 4 > used)
                throw new InvalidDataException("Invalid FILE attribute boundaries.");
            int count = 0;
            bool terminated = false;
            while (offset + 4 <= used && count++ < 4096)
            {
                uint type = U32(span, offset);
                if (type == uint.MaxValue) { terminated = true; break; }
                if (offset + 16 > used) throw new InvalidDataException("Truncated attribute header.");
                int length = checked((int)U32(span, offset + 4));
                bool nonresident = span[offset + 8] != 0;
                int headerSize = nonresident ? 64 : 24;
                if (length < headerSize || length % 8 != 0 || offset + (long)length > used)
                    throw new InvalidDataException("Invalid attribute length.");
                var attribute = span.Slice(offset, length);
                int nameLength = attribute[9] * 2, nameOffset = U16(attribute, 10);
                if (nameLength != 0 && (nameOffset < headerSize || nameOffset + nameLength > length))
                    throw new InvalidDataException("Invalid attribute name.");
                string name = nameLength == 0 ? "" : Utf16.GetString(attribute.Slice(nameOffset, nameLength));
                ushort attributeId = U16(attribute, 14);
                if (!nonresident)
                {
                    int valueLength = checked((int)U32(attribute, 16)), valueOffset = U16(attribute, 20);
                    if (valueOffset < 24 || valueOffset + (long)valueLength > length)
                        throw new InvalidDataException("Invalid resident value.");
                    var value = attribute.Slice(valueOffset, valueLength);
                    if (type == 0x10 && value.Length >= 36)
                    {
                        record.Attributes = (FileAttributes)U32(value, 32);
                        long time = I64(value, 8);
                        if (time >= 0 && time <= DateTime.MaxValue.ToFileTimeUtc()) record.ModifiedUtcTicks = DateTime.FromFileTimeUtc(time).Ticks;
                    }
                    else if (type == 0x30)
                    {
                        if (value.Length < 66 || 66 + value[64] * 2 > value.Length)
                            throw new InvalidDataException("Invalid FILE_NAME value.");
                        record.Names.Add(new(U64(value, 0), Utf16.GetString(value.Slice(66, value[64] * 2)), value[65]));
                    }
                    else if (type == 0x80)
                        record.DataStreams.Add(new(name, 0, valueLength, true, []) { AttributeId = attributeId });
                    else if (type == 0x20)
                        record.AttributeReferences.AddRange(ParseAttributeList(value));
                }
                else if (type is 0x80 or 0x20)
                {
                    long vcn = I64(attribute, 16), lastVcn = I64(attribute, 24);
                    int mappings = U16(attribute, 32);
                    if (vcn < 0 || lastVcn < vcn - 1 || mappings < 64 || mappings >= length)
                        throw new InvalidDataException("Invalid nonresident data bounds.");
                    long logical = vcn == 0 ? I64(attribute, 48) : -1;
                    if (vcn == 0 && logical < 0) throw new InvalidDataException("Negative stream length.");
                    var runs = DecodeRunList(attribute[mappings..], vcn);
                    if (runs.Count != 0 && checked(runs[^1].Vcn + runs[^1].ClusterCount - 1) != lastVcn)
                        throw new InvalidDataException("Runlist does not cover the declared VCN range.");
                    if (runs.Count == 0 && logical > 0) throw new InvalidDataException("Missing stream runlist.");
                    var stream = new MftStream(name, vcn, logical, false, runs) { EndingVcn = lastVcn, AttributeId = attributeId };
                    if (type == 0x80) record.DataStreams.Add(stream);
                    else record.ExternalAttributeLists.Add(stream);
                }
                offset = checked(offset + length);
            }
            if (!terminated) throw new InvalidDataException("Missing attribute terminator.");
            if (record.IsDirectory) record.Attributes |= FileAttributes.Directory;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException or DecoderFallbackException)
        {
            error = ex.Message;
            record = new();
            return false;
        }
    }

    public static IReadOnlyList<MftDataRun> DecodeRunList(ReadOnlySpan<byte> bytes, long startingVcn = 0)
    {
        if (startingVcn < 0) throw new InvalidDataException("Negative starting VCN.");
        var runs = new List<MftDataRun>();
        long vcn = startingVcn, lcn = 0;
        int cursor = 0;
        while (cursor < bytes.Length)
        {
            byte header = bytes[cursor++];
            if (header == 0) return runs;
            int lengthBytes = header & 15, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || cursor + lengthBytes + offsetBytes > bytes.Length)
                throw new InvalidDataException("Invalid mapping-pair widths.");
            ulong lengthValue = 0;
            for (int i = 0; i < lengthBytes; i++) lengthValue |= (ulong)bytes[cursor++] << (i * 8);
            if (lengthValue == 0 || lengthValue > long.MaxValue) throw new InvalidDataException("Invalid run length.");
            long length = (long)lengthValue;
            long actualLcn = -1;
            if (offsetBytes > 0)
            {
                ulong delta = 0;
                for (int i = 0; i < offsetBytes; i++) delta |= (ulong)bytes[cursor + i] << (i * 8);
                if (offsetBytes < 8 && (bytes[cursor + offsetBytes - 1] & 0x80) != 0) delta |= ulong.MaxValue << (offsetBytes * 8);
                cursor += offsetBytes;
                lcn = checked(lcn + unchecked((long)delta));
                if (lcn < 0) throw new InvalidDataException("Run points before the volume.");
                _ = checked(lcn + length);
                actualLcn = lcn;
            }
            runs.Add(new(vcn, actualLcn, length));
            vcn = checked(vcn + length);
            if (runs.Count > 1_000_000) throw new InvalidDataException("Excessive run count.");
        }
        throw new InvalidDataException("Unterminated runlist.");
    }

    public static IReadOnlyList<ulong> ParseAttributeList(ReadOnlySpan<byte> bytes)
    {
        var references = new List<ulong>();
        for (int offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 4) throw new InvalidDataException("Truncated attribute list.");
            uint type = U32(bytes, offset);
            if (type == uint.MaxValue || type == 0) break;
            if (bytes.Length - offset < 26) throw new InvalidDataException("Truncated attribute-list entry.");
            int length = U16(bytes, offset + 4);
            if (length < 26 || offset + (long)length > bytes.Length) throw new InvalidDataException("Invalid attribute-list entry length.");
            int nameLength = bytes[offset + 6] * 2, nameOffset = bytes[offset + 7];
            if (nameLength != 0 && (nameOffset < 26 || nameOffset + nameLength > length)) throw new InvalidDataException("Invalid attribute-list name.");
            references.Add(U64(bytes, offset + 16));
            offset += length;
        }
        return references;
    }
    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p, 2));
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p, 4));
    private static ulong U64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(p, 8));
    private static long I64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b.Slice(p, 8));
}
