using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Gfs2;

public sealed partial class Gfs2Writer {
  // GFS2 ExHash on-disk constants. These values are format-defined by
  // include/uapi/linux/gfs2_ondisk.h; the implementation below is clean-room.
  private const uint DifExHash = 0x00000002;
  private const uint MtLeaf = 6, MtJournalData = 7;
  private const uint FmtLeaf = 600, FmtJournalData = 700;
  private const int LeafHeaderSize = 104;
  private const int MaxExHashDepth = 17;
  private const int InitialExHashDepth = 8; // 4096 / 2 / sizeof(__be64) == 256 pointers

  private sealed record ExHashItem(
    string Name, ulong FormalIno, ulong Address, ushort Type, uint Hash, int RecordLength);

  private sealed class ExHashBucket(int prefix, int depth, List<ExHashItem> entries) {
    public int Prefix { get; } = prefix;
    public int Depth { get; } = depth;
    public List<ExHashItem> Entries { get; } = entries;
    public List<long> LeafBlocks { get; } = [];
  }

  /// <summary>
  /// Writes a directory that no longer fits in the stuffed dinode as a native
  /// GFS2 extendible-hash directory. The table is the directory file; its BE64
  /// slots point at leaf metadata blocks containing the actual dirents.
  /// </summary>
  private void WriteExHashDirectory(
      long block,
      ulong formalIno,
      bool system,
      List<(string Name, ulong Fi, ulong Addr, ushort Type)> entries,
      int nlink,
      uint mode) {
    var items = new List<ExHashItem>(entries.Count);
    foreach (var entry in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
      var recordLength = (DirentSize + nameBytes.Length + 7) & ~7;
      items.Add(new ExHashItem(
        entry.Name, entry.Fi, entry.Addr, entry.Type, Crc32(nameBytes), recordLength));
    }

    var (globalDepth, buckets) = BuildExHashBuckets(items);
    var leafPayloadBytes = BlockSize - LeafHeaderSize;
    var leafPages = new Dictionary<ExHashBucket, List<List<ExHashItem>>>();
    var leafBlockCount = 0;

    foreach (var bucket in buckets) {
      var pages = PackLeafPages(bucket.Entries, leafPayloadBytes);
      leafPages.Add(bucket, pages);
      leafBlockCount += pages.Count;
      foreach (var _ in pages)
        bucket.LeafBlocks.Add(this.AllocBlock());
    }

    var pointerCount = 1 << globalDepth;
    var table = new byte[checked(pointerCount * sizeof(ulong))];
    foreach (var bucket in buckets) {
      var firstLeaf = checked((ulong)bucket.LeafBlocks[0]);
      var repetitions = 1 << (globalDepth - bucket.Depth);
      var start = bucket.Prefix << (globalDepth - bucket.Depth);
      for (var i = 0; i < repetitions; ++i)
        BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan((start + i) * 8, 8), firstLeaf);
    }

    var inlineCapacity = BlockSize - DinodeHeaderSize;
    var tableHeight = table.Length <= inlineCapacity ? (ushort)0 : (ushort)1;
    var journalPayload = BlockSize - IndPointerBase;
    var tableBlocks = tableHeight == 0 ? 0 : (table.Length + journalPayload - 1) / journalPayload;
    if (tableBlocks > PointersPerDinode)
      throw new InvalidOperationException(
        $"GFS2 ExHash table requires {tableBlocks} blocks, beyond the dinode pointer capacity.");

    var journalBlocks = new List<long>(tableBlocks);
    for (var i = 0; i < tableBlocks; ++i)
      journalBlocks.Add(this.AllocBlock());

    this.WriteDinode(
      block: block,
      formalIno: formalIno,
      mode: mode,
      nlink: (uint)nlink,
      size: (ulong)table.Length,
      blocks: (ulong)(1 + leafBlockCount + tableBlocks),
      flags: DifJData | DifExHash | (system ? DifSystem : 0u),
      payloadFormat: PfDirent,
      height: tableHeight,
      entries: (uint)entries.Count,
      goalMeta: (ulong)block,
      goalData: (ulong)block);
    BinaryPrimitives.WriteUInt16BigEndian(
      this.Span(block * BlockSize + 146, 2), checked((ushort)globalDepth));

    if (tableHeight == 0) {
      table.AsSpan().CopyTo(this.Span(block * BlockSize + DinodeHeaderSize, table.Length));
    } else {
      var copied = 0;
      for (var i = 0; i < journalBlocks.Count; ++i) {
        var dataBlock = journalBlocks[i];
        BinaryPrimitives.WriteUInt64BigEndian(
          this.Span(block * BlockSize + DinodeHeaderSize + i * 8, 8), checked((ulong)dataBlock));
        this.WriteMetaHeader(dataBlock * BlockSize, MtJournalData, FmtJournalData);
        var count = Math.Min(journalPayload, table.Length - copied);
        table.AsSpan(copied, count).CopyTo(this.Span(dataBlock * BlockSize + IndPointerBase, count));
        copied += count;
        this.MarkUsed(dataBlock);
      }
    }

    foreach (var bucket in buckets) {
      var pages = leafPages[bucket];
      for (var pageIndex = 0; pageIndex < pages.Count; ++pageIndex) {
        var next = pageIndex + 1 < bucket.LeafBlocks.Count
          ? checked((ulong)bucket.LeafBlocks[pageIndex + 1])
          : 0UL;
        this.WriteExHashLeaf(
          bucket.LeafBlocks[pageIndex],
          block,
          bucket.Depth,
          pageIndex + 1,
          next,
          pages[pageIndex]);
      }
    }
  }

  private static (int GlobalDepth, List<ExHashBucket> Buckets) BuildExHashBuckets(List<ExHashItem> items) {
    var globalDepth = InitialExHashDepth;
    var buckets = new List<ExHashBucket> { new(0, 0, items) };
    var leafPayloadBytes = BlockSize - LeafHeaderSize;

    while (true) {
      var splitIndex = buckets.FindIndex(bucket =>
        bucket.Depth < MaxExHashDepth && Footprint(bucket.Entries) > leafPayloadBytes);
      if (splitIndex < 0)
        break;

      var bucket = buckets[splitIndex];
      if (bucket.Depth == globalDepth) {
        if (globalDepth == MaxExHashDepth)
          break;
        ++globalDepth;
      }

      var newDepth = bucket.Depth + 1;
      var left = new List<ExHashItem>();
      var right = new List<ExHashItem>();
      var splitBit = 32 - newDepth;
      foreach (var item in bucket.Entries) {
        if (((item.Hash >> splitBit) & 1) == 0)
          left.Add(item);
        else
          right.Add(item);
      }

      buckets.RemoveAt(splitIndex);
      buckets.Insert(splitIndex, new ExHashBucket(bucket.Prefix << 1, newDepth, left));
      buckets.Insert(splitIndex + 1, new ExHashBucket((bucket.Prefix << 1) | 1, newDepth, right));
    }

    return (globalDepth, buckets);
  }

  private static long Footprint(List<ExHashItem> entries) {
    long result = 0;
    foreach (var entry in entries)
      result += entry.RecordLength;
    return result;
  }

  private static List<List<ExHashItem>> PackLeafPages(List<ExHashItem> entries, int capacity) {
    if (entries.Count == 0)
      return [[]];

    var result = new List<List<ExHashItem>>();
    var page = new List<ExHashItem>();
    var used = 0;
    foreach (var entry in entries) {
      if (entry.RecordLength > capacity)
        throw new InvalidDataException(
          $"GFS2 directory entry '{entry.Name}' cannot fit in an ExHash leaf.");
      if (page.Count > 0 && used + entry.RecordLength > capacity) {
        result.Add(page);
        page = [];
        used = 0;
      }
      page.Add(entry);
      used += entry.RecordLength;
    }
    result.Add(page);
    return result;
  }

  private void WriteExHashLeaf(
      long leafBlock,
      long directoryBlock,
      int localDepth,
      int distance,
      ulong nextLeaf,
      List<ExHashItem> entries) {
    var o = leafBlock * BlockSize;
    this.WriteMetaHeader(o, MtLeaf, FmtLeaf);
    BinaryPrimitives.WriteUInt16BigEndian(this.Span(o + 24, 2), checked((ushort)localDepth));
    BinaryPrimitives.WriteUInt16BigEndian(this.Span(o + 26, 2), checked((ushort)entries.Count));
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(o + 28, 4), PfDirent);
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(o + 32, 8), nextLeaf);
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(o + 40, 8), checked((ulong)directoryBlock));
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(o + 48, 4), checked((uint)distance));
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(o + 52, 4), 0u);
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(o + 56, 8), this._baseTime);

    var areaStart = o + LeafHeaderSize;
    var areaLength = BlockSize - LeafHeaderSize;
    if (entries.Count == 0) {
      // Native GFS2 leaves keep one deleted/empty record spanning the free area.
      BinaryPrimitives.WriteUInt16BigEndian(this.Span(areaStart + 20, 2), checked((ushort)areaLength));
      this.MarkUsed(leafBlock);
      return;
    }

    var pos = 0;
    for (var i = 0; i < entries.Count; ++i) {
      var entry = entries[i];
      var recLen = i == entries.Count - 1 ? areaLength - pos : entry.RecordLength;
      this.WriteDirent(
        areaStart + pos,
        entry.FormalIno,
        entry.Address,
        entry.Name,
        Encoding.UTF8.GetByteCount(entry.Name),
        checked((ushort)recLen),
        entry.Type);
      pos += recLen;
    }

    this.MarkUsed(leafBlock);
  }
}
