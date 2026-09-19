#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Tux3;

/// <summary>One contiguous run from the effective TUX3 allocation bitmap.</summary>
public readonly record struct Tux3BlockRun(ulong StartBlock, ulong BlockCount, bool IsAllocated);

/// <summary>Native TUX3 journal record identifiers.</summary>
public enum Tux3JournalRecordType : byte {
  BlockAllocate = 0x33,
  BlockFree = 0x34,
  BlockFreeOnUnify = 0x35,
  BlockFreeRelog = 0x36,
  LeafRedirect = 0x37,
  LeafFree = 0x38,
  BNodeRedirect = 0x39,
  BNodeRoot = 0x3A,
  BNodeSplit = 0x3B,
  BNodeAdd = 0x3C,
  BNodeUpdate = 0x3D,
  BNodeMerge = 0x3E,
  BNodeDelete = 0x3F,
  BNodeAdjust = 0x40,
  BNodeFree = 0x41,
  OrphanAdd = 0x42,
  OrphanDelete = 0x43,
  FreeBlocks = 0x44,
  Unify = 0x45,
  Delta = 0x46,
}

/// <summary>
/// Decoded journal record. Fields which are not meaningful for a record type are zero.
/// Block addresses and keys are widened from the native 48-bit representation.
/// </summary>
public readonly record struct Tux3JournalRecord(
  Tux3JournalRecordType Type,
  ulong Block = 0,
  ulong OtherBlock = 0,
  ulong Key = 0,
  uint Count = 0);

internal sealed record Tux3NativeMetadataResult(
  bool JournalValid,
  bool AllocationMapValid,
  IReadOnlyList<Tux3JournalRecord> JournalRecords,
  IReadOnlyList<Tux3BlockRun> AllocationRuns,
  string Status);

/// <summary>
/// Clean-room parser for the specification-defined TUX3 metadata needed to prove allocation state.
/// It intentionally does not replay structural tree mutations: a journal containing such active
/// records makes the allocation map unavailable rather than allowing stale tree data to drive Wipe.
/// </summary>
internal sealed class Tux3NativeMetadataParser {
  private const ushort BNodeMagic = 0xB4DE;
  private const ushort DLeafMagic = 0xBEAF;
  private const ushort ILeafMagic = 0x90DE;
  private const ushort LogMagic = 0x10AD;
  private const ulong AddressMask = (1UL << 48) - 1;
  private const ulong BitmapInode = 1;
  private const int MaxTreeDepth = 64;
  private const int MaxAllocationRuns = 1_000_000;
  private const uint MaxJournalBlocks = 1_000_000;
  private const int SuperblockReserveLength = 1 << 12;

  private readonly ImageAccessor _image;
  private readonly int _blockSize;
  private readonly ulong _volumeBlocks;
  private readonly ulong _inodeRootPacked;
  private readonly ulong _logChain;
  private readonly uint _logCount;
  private readonly HashSet<ulong> _metadataBlocks = [];
  private readonly HashSet<ulong> _journalBlocks = [];

  private Tux3NativeMetadataParser(
    ImageAccessor image,
    ushort blockBits,
    ulong volumeBlocks,
    ulong inodeRootPacked,
    ulong logChain,
    uint logCount) {
    this._image = image;
    this._blockSize = 1 << blockBits;
    this._volumeBlocks = volumeBlocks;
    this._inodeRootPacked = inodeRootPacked;
    this._logChain = logChain;
    this._logCount = logCount;
  }

  public static Tux3NativeMetadataResult Parse(
    ImageAccessor image,
    ushort blockBits,
    ulong volumeBlocks,
    ulong inodeRootPacked,
    ulong logChain,
    uint logCount) {
    ArgumentNullException.ThrowIfNull(image);

    if (blockBits is < 9 or > 12 || volumeBlocks == 0)
      return Invalid("unsupported block geometry");

    var blockSize = 1UL << blockBits;
    if (volumeBlocks > (ulong)long.MaxValue / blockSize)
      return Invalid("volume byte size is not representable");

    var volumeBytes = volumeBlocks * blockSize;
    if (volumeBytes > (ulong)image.Length)
      return Invalid("declared volume is truncated");

    try {
      return new Tux3NativeMetadataParser(image, blockBits, volumeBlocks, inodeRootPacked, logChain, logCount).ParseCore();
    } catch (InvalidDataException ex) {
      return Invalid(ex.Message);
    } catch (IOException ex) {
      return Invalid(ex.Message);
    } catch (OverflowException) {
      return Invalid("metadata arithmetic overflow");
    }
  }

  private Tux3NativeMetadataResult ParseCore() {
    var journal = this.ParseJournal(out var activeStart);
    if (journal is null)
      return new(false, false, [], [], "journal-invalid");

    if (journal.Skip(activeStart).Any(static record => IsStructuralRecord(record.Type)))
      return new(true, false, journal, [], "journal-needs-structural-replay");

    var bitmapRoot = this.ReadBitmapDataRoot();
    if (bitmapRoot is null)
      return new(true, false, journal, [], "allocation-tree-invalid");

    var runs = this.ReadAllocationBitmap(bitmapRoot.Value);
    if (runs is null)
      return new(true, false, journal, [], "allocation-bitmap-invalid");

    foreach (var record in journal.Skip(activeStart)) {
      switch (record.Type) {
        case Tux3JournalRecordType.BlockAllocate:
          if (!SetAllocation(runs, record.Block, record.Count, true, this._volumeBlocks))
            return new(true, false, journal, [], "journal-allocation-range-invalid");
          break;
        case Tux3JournalRecordType.BlockFree:
        case Tux3JournalRecordType.BlockFreeRelog:
          if (!SetAllocation(runs, record.Block, record.Count, false, this._volumeBlocks))
            return new(true, false, journal, [], "journal-free-range-invalid");
          break;
        case Tux3JournalRecordType.BlockFreeOnUnify:
          // Upstream replay defers this free until the next unify; the block remains live now.
          break;
      }
    }

    // Tree blocks used to discover the bitmap must agree with the effective allocation state.
    // If they do not, the bitmap/tree pair is internally inconsistent and cannot safely drive Wipe.
    if (this._metadataBlocks.Any(block => !IsAllocated(runs, block)))
      return new(true, false, journal, [], "allocation-tree-references-free-metadata");

    // Some regions are format metadata even if an old or partially committed bitmap says otherwise.
    // Reserving additional blocks is safe; declaring them free would not be.
    if (!SetAllocation(runs, 0, 1, true, this._volumeBlocks))
      return new(true, false, journal, [], "header-range-invalid");

    var superStart = (ulong)Tux3Reader.SuperblockOffset / (ulong)this._blockSize;
    var superEndByte = (ulong)Tux3Reader.SuperblockOffset + SuperblockReserveLength;
    var superEnd = (superEndByte + (ulong)this._blockSize - 1) / (ulong)this._blockSize;
    if (superStart < this._volumeBlocks &&
        !SetAllocation(runs, superStart, Math.Min(superEnd, this._volumeBlocks) - superStart, true, this._volumeBlocks))
      return new(true, false, journal, [], "superblock-range-invalid");

    foreach (var block in this._journalBlocks)
      if (!SetAllocation(runs, block, 1, true, this._volumeBlocks))
        return new(true, false, journal, [], "journal-block-range-invalid");

    if (runs.Count > MaxAllocationRuns)
      return new(true, false, journal, [], "allocation-map-too-fragmented");

    return new(true, true, journal, runs, "allocation-map+journal");
  }

  private List<Tux3JournalRecord>? ParseJournal(out int activeStart) {
    activeStart = 0;
    if (this._logCount == 0)
      return [];
    if (this._logCount > MaxJournalBlocks || this._logCount > this._volumeBlocks || this._logChain == 0)
      return null;

    var newestFirst = new List<(ulong Block, byte[] Data)>((int)this._logCount);
    var seen = new HashSet<ulong>();
    var block = this._logChain;

    for (var i = 0U; i < this._logCount; ++i) {
      if (block == 0 || block >= this._volumeBlocks || !seen.Add(block))
        return null;

      var data = this.ReadBlock(block, this._journalBlocks);
      if (data is null)
        return null;

      var span = data.AsSpan();
      if (BinaryPrimitives.ReadUInt16BigEndian(span) != LogMagic)
        return null;

      var payloadBytes = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
      if (16 + payloadBytes > this._blockSize)
        return null;

      newestFirst.Add((block, data));
      block = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(8, 8));
    }

    newestFirst.Reverse();
    var records = new List<Tux3JournalRecord>();
    foreach (var (_, data) in newestFirst) {
      var span = data.AsSpan();
      var payloadBytes = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
      var offset = 16;
      var limit = offset + payloadBytes;

      while (offset < limit) {
        var type = (Tux3JournalRecordType)span[offset];
        var size = GetJournalRecordSize(type);
        if (size == 0 || offset > limit - size)
          return null;

        if (!TryDecodeJournalRecord(span.Slice(offset, size), type, out var record))
          return null;
        records.Add(record);
        offset += size;
      }

      if (offset != limit)
        return null;
    }

    var lastUnify = records.FindLastIndex(static record => record.Type == Tux3JournalRecordType.Unify);
    activeStart = lastUnify >= 0 ? lastUnify : 0;
    return records;
  }

  private PackedRoot? ReadBitmapDataRoot() {
    var inodeRoot = PackedRoot.Decode(this._inodeRootPacked);
    if (inodeRoot.Direct || inodeRoot.Depth == 0 || inodeRoot.Depth > MaxTreeDepth)
      return null;

    var leafBlock = this.FindLeaf(inodeRoot, BitmapInode, ILeafMagic);
    if (leafBlock is null)
      return null;

    var leaf = this.ReadBlock(leafBlock.Value, this._metadataBlocks);
    if (leaf is null || BinaryPrimitives.ReadUInt16BigEndian(leaf) != ILeafMagic)
      return null;

    var span = leaf.AsSpan();
    var count = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
    const int headerSize = 16;
    if (count == 0 || headerSize + count * 2 > this._blockSize)
      return null;

    var inodeBase = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(8, 8));
    if (BitmapInode < inodeBase)
      return null;
    var index64 = BitmapInode - inodeBase;
    if (index64 >= count)
      return null;
    var index = (int)index64;

    var tableCapacity = this._blockSize - headerSize - count * 2;
    var previous = 0;
    for (var i = 1; i <= count; ++i) {
      var current = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(this._blockSize - i * 2, 2));
      if (current < previous || current > tableCapacity)
        return null;
      previous = current;
    }

    var start = index == 0
      ? 0
      : BinaryPrimitives.ReadUInt16BigEndian(span.Slice(this._blockSize - index * 2, 2));
    var end = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(this._blockSize - (index + 1) * 2, 2));
    if (end < start || end > tableCapacity)
      return null;

    return TryReadDataTreeRoot(span.Slice(headerSize + start, end - start), out var dataRoot)
      ? dataRoot
      : null;
  }

  private static bool TryReadDataTreeRoot(ReadOnlySpan<byte> attrs, out PackedRoot root) {
    root = default;
    ulong? packedRoot = null;
    var offset = 0;

    while (offset < attrs.Length) {
      if (offset > attrs.Length - 2)
        return false;

      var head = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset, 2));
      offset += 2;
      var kind = head >> 12;
      if (kind >= 16)
        return false;

      if (kind == 2) {
        if (offset > attrs.Length - 8)
          return false;
        var candidate = BinaryPrimitives.ReadUInt64BigEndian(attrs.Slice(offset, 8));
        if (packedRoot is { } previous && previous != candidate)
          return false;
        packedRoot = candidate;
        offset += 8;
        continue;
      }

      var fixedSize = kind switch {
        0 => 8,
        1 => 12,
        3 => 16,
        4 => 4,
        5 => 8,
        11 or 12 => -1,
        _ => 0,
      };

      if (fixedSize >= 0) {
        if (offset > attrs.Length - fixedSize)
          return false;
        offset += fixedSize;
        continue;
      }

      if (offset > attrs.Length - 2)
        return false;
      var bytes = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset, 2));
      offset += 2;
      if (offset > attrs.Length - bytes)
        return false;
      offset += bytes;
    }

    if (packedRoot is null)
      return false;
    root = PackedRoot.Decode(packedRoot.Value);
    return true;
  }

  private List<Tux3BlockRun>? ReadAllocationBitmap(PackedRoot root) {
    if (root.Direct && root.Count == 0)
      return null;
    if (!root.Direct && (root.Depth == 0 || root.Depth > MaxTreeDepth))
      return null;

    var bitmapBytes = (this._volumeBlocks + 7) / 8;
    var bitmapLogicalBlocks = (bitmapBytes + (ulong)this._blockSize - 1) / (ulong)this._blockSize;
    var runs = new List<Tux3BlockRun>();
    ulong producedBlocks = 0;

    for (ulong logicalBlock = 0; logicalBlock < bitmapLogicalBlocks; ++logicalBlock) {
      var physical = this.MapDataBlock(root, logicalBlock);
      if (physical is null)
        return null;

      var data = this.ReadBlock(physical.Value, this._metadataBlocks);
      if (data is null)
        return null;

      var bytesThisBlock = (int)Math.Min((ulong)this._blockSize, bitmapBytes - logicalBlock * (ulong)this._blockSize);
      for (var byteIndex = 0; byteIndex < bytesThisBlock && producedBlocks < this._volumeBlocks; ++byteIndex) {
        var value = data[byteIndex];
        var remaining = this._volumeBlocks - producedBlocks;
        if (remaining >= 8 && value is 0x00 or 0xFF) {
          if (!AddRun(runs, producedBlocks, 8, value == 0xFF))
            return null;
          producedBlocks += 8;
          continue;
        }

        var bits = (int)Math.Min(8UL, remaining);
        for (var bit = 0; bit < bits; ++bit) {
          var allocated = (value & (1 << bit)) != 0;
          if (!AddRun(runs, producedBlocks, 1, allocated))
            return null;
          ++producedBlocks;
        }
      }
    }

    return producedBlocks == this._volumeBlocks ? runs : null;
  }

  private ulong? MapDataBlock(PackedRoot root, ulong logicalBlock) {
    if (root.Direct) {
      if (logicalBlock >= root.Count || logicalBlock >= this._volumeBlocks ||
          root.Block > this._volumeBlocks - 1 - logicalBlock)
        return null;
      return root.Block + logicalBlock;
    }

    var leafBlock = this.FindLeaf(root, logicalBlock, DLeafMagic);
    if (leafBlock is null)
      return null;

    var leaf = this.ReadBlock(leafBlock.Value, this._metadataBlocks);
    if (leaf is null)
      return null;
    var span = leaf.AsSpan();
    if (BinaryPrimitives.ReadUInt16BigEndian(span) != DLeafMagic)
      return null;

    var count = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
    var capacity = (this._blockSize - 8) / 16;
    if (count < 2 || count > capacity)
      return null;

    Span<DLeafEntry> entries = count <= 128 ? stackalloc DLeafEntry[count] : new DLeafEntry[count];
    ulong previousLogical = 0;
    for (var i = 0; i < count; ++i) {
      var at = 8 + i * 16;
      var logicalWord = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(at, 8));
      var physicalWord = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(at + 8, 8));
      var version = (uint)(((logicalWord >> 48) << 16) | (physicalWord >> 48));
      var logical = logicalWord & AddressMask;
      var physical = physicalWord & AddressMask;
      if (version != 0 || (i > 0 && logical <= previousLogical))
        return null;
      entries[i] = new(logical, physical);
      previousLogical = logical;
    }

    if (entries[^1].Physical != 0)
      return null;

    for (var i = 0; i < entries.Length - 1; ++i) {
      var current = entries[i];
      var next = entries[i + 1];
      if (logicalBlock < current.Logical || logicalBlock >= next.Logical)
        continue;
      if (current.Physical == 0)
        return null;
      var delta = logicalBlock - current.Logical;
      if (delta >= this._volumeBlocks || current.Physical > this._volumeBlocks - 1 - delta)
        return null;
      return current.Physical + delta;
    }

    return null;
  }

  private ulong? FindLeaf(PackedRoot root, ulong key, ushort expectedLeafMagic) {
    if (root.Direct || root.Depth == 0 || root.Depth > MaxTreeDepth)
      return null;

    var block = root.Block;
    var visited = new HashSet<ulong>();
    for (var level = 0; level < root.Depth; ++level) {
      if (!visited.Add(block))
        return null;

      var data = this.ReadBlock(block, this._metadataBlocks);
      if (data is null)
        return null;

      if (level == root.Depth - 1)
        return BinaryPrimitives.ReadUInt16BigEndian(data) == expectedLeafMagic ? block : null;

      var span = data.AsSpan();
      if (BinaryPrimitives.ReadUInt16BigEndian(span) != BNodeMagic)
        return null;
      var count = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
      var capacity = (uint)((this._blockSize - 8) / 16);
      if (count == 0 || count > capacity)
        return null;

      ulong selectedBlock = 0;
      ulong previousKey = 0;
      for (var i = 0U; i < count; ++i) {
        var at = 8 + checked((int)i * 16);
        var entryKey = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(at, 8));
        var child = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(at + 8, 8));
        if (child > AddressMask || child >= this._volumeBlocks || (i > 0 && entryKey <= previousKey))
          return null;
        if (i == 0 && entryKey > key)
          return null;
        if (entryKey <= key)
          selectedBlock = child;
        else
          break;
        previousKey = entryKey;
      }

      if (selectedBlock == 0)
        return null;
      block = selectedBlock;
    }

    return null;
  }

  private byte[]? ReadBlock(ulong block, ISet<ulong> destination) {
    if (block >= this._volumeBlocks)
      return null;
    if (block > (ulong)long.MaxValue / (ulong)this._blockSize)
      return null;

    var offset = block * (ulong)this._blockSize;
    if (offset > (ulong)this._image.Length || (ulong)this._image.Length - offset < (ulong)this._blockSize)
      return null;

    destination.Add(block);
    return this._image.Read((long)offset, this._blockSize);
  }

  private static bool IsStructuralRecord(Tux3JournalRecordType type)
    => type is Tux3JournalRecordType.LeafRedirect or
      Tux3JournalRecordType.LeafFree or
      Tux3JournalRecordType.BNodeRedirect or
      Tux3JournalRecordType.BNodeRoot or
      Tux3JournalRecordType.BNodeSplit or
      Tux3JournalRecordType.BNodeAdd or
      Tux3JournalRecordType.BNodeUpdate or
      Tux3JournalRecordType.BNodeMerge or
      Tux3JournalRecordType.BNodeDelete or
      Tux3JournalRecordType.BNodeAdjust or
      Tux3JournalRecordType.BNodeFree;

  private static int GetJournalRecordSize(Tux3JournalRecordType type)
    => type switch {
      Tux3JournalRecordType.BlockAllocate or
      Tux3JournalRecordType.BlockFree or
      Tux3JournalRecordType.BlockFreeOnUnify or
      Tux3JournalRecordType.BlockFreeRelog => 11,
      Tux3JournalRecordType.LeafRedirect or Tux3JournalRecordType.BNodeRedirect or Tux3JournalRecordType.BNodeMerge => 13,
      Tux3JournalRecordType.LeafFree or Tux3JournalRecordType.BNodeFree or Tux3JournalRecordType.FreeBlocks => 7,
      Tux3JournalRecordType.BNodeRoot => 26,
      Tux3JournalRecordType.BNodeSplit or Tux3JournalRecordType.BNodeDelete => 15,
      Tux3JournalRecordType.BNodeAdd or Tux3JournalRecordType.BNodeUpdate or Tux3JournalRecordType.BNodeAdjust => 19,
      Tux3JournalRecordType.OrphanAdd or Tux3JournalRecordType.OrphanDelete => 9,
      Tux3JournalRecordType.Unify or Tux3JournalRecordType.Delta => 1,
      _ => 0,
    };

  private static bool TryDecodeJournalRecord(
    ReadOnlySpan<byte> data,
    Tux3JournalRecordType type,
    out Tux3JournalRecord record) {
    record = new(type);
    switch (type) {
      case Tux3JournalRecordType.BlockAllocate:
      case Tux3JournalRecordType.BlockFree:
      case Tux3JournalRecordType.BlockFreeOnUnify:
      case Tux3JournalRecordType.BlockFreeRelog:
        record = new(type, ReadUInt48BigEndian(data.Slice(5, 6)), Count: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4)));
        return true;

      case Tux3JournalRecordType.LeafRedirect:
      case Tux3JournalRecordType.BNodeRedirect:
      case Tux3JournalRecordType.BNodeMerge:
        record = new(type, ReadUInt48BigEndian(data.Slice(1, 6)), ReadUInt48BigEndian(data.Slice(7, 6)));
        return true;

      case Tux3JournalRecordType.LeafFree:
      case Tux3JournalRecordType.BNodeFree:
      case Tux3JournalRecordType.FreeBlocks:
        record = new(type, ReadUInt48BigEndian(data.Slice(1, 6)));
        return true;

      case Tux3JournalRecordType.BNodeRoot:
        record = new(
          type,
          ReadUInt48BigEndian(data.Slice(2, 6)),
          ReadUInt48BigEndian(data.Slice(8, 6)),
          ReadUInt48BigEndian(data.Slice(20, 6)),
          data[1]);
        return true;

      case Tux3JournalRecordType.BNodeSplit:
      case Tux3JournalRecordType.BNodeDelete:
        record = new(
          type,
          ReadUInt48BigEndian(data.Slice(3, 6)),
          ReadUInt48BigEndian(data.Slice(9, 6)),
          Count: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2)));
        return true;

      case Tux3JournalRecordType.BNodeAdd:
      case Tux3JournalRecordType.BNodeUpdate:
      case Tux3JournalRecordType.BNodeAdjust:
        record = new(
          type,
          ReadUInt48BigEndian(data.Slice(1, 6)),
          ReadUInt48BigEndian(data.Slice(7, 6)),
          ReadUInt48BigEndian(data.Slice(13, 6)));
        return true;

      case Tux3JournalRecordType.OrphanAdd:
      case Tux3JournalRecordType.OrphanDelete:
        record = new(type, BinaryPrimitives.ReadUInt64BigEndian(data.Slice(1, 8)));
        return true;

      case Tux3JournalRecordType.Unify:
      case Tux3JournalRecordType.Delta:
        return true;

      default:
        return false;
    }
  }

  private static ulong ReadUInt48BigEndian(ReadOnlySpan<byte> data)
    => ((ulong)BinaryPrimitives.ReadUInt16BigEndian(data[..2]) << 32) |
       BinaryPrimitives.ReadUInt32BigEndian(data.Slice(2, 4));

  private static bool AddRun(List<Tux3BlockRun> runs, ulong start, ulong count, bool allocated) {
    if (count == 0)
      return true;
    if (runs.Count > 0) {
      var last = runs[^1];
      if (last.IsAllocated == allocated && last.StartBlock + last.BlockCount == start) {
        runs[^1] = last with { BlockCount = last.BlockCount + count };
        return true;
      }
    }
    if (runs.Count >= MaxAllocationRuns)
      return false;
    runs.Add(new(start, count, allocated));
    return true;
  }

  private static bool SetAllocation(
    List<Tux3BlockRun> runs,
    ulong start,
    ulong count,
    bool allocated,
    ulong volumeBlocks) {
    if (count == 0)
      return true;
    if (start >= volumeBlocks || count > volumeBlocks - start)
      return false;

    var end = start + count;
    var rewritten = new List<Tux3BlockRun>(Math.Min(MaxAllocationRuns, runs.Count + 2));
    foreach (var run in runs) {
      var runEnd = run.StartBlock + run.BlockCount;
      if (runEnd <= start || run.StartBlock >= end) {
        if (!AddRun(rewritten, run.StartBlock, run.BlockCount, run.IsAllocated))
          return false;
        continue;
      }

      if (run.StartBlock < start && !AddRun(rewritten, run.StartBlock, start - run.StartBlock, run.IsAllocated))
        return false;
      var overlapStart = Math.Max(run.StartBlock, start);
      var overlapEnd = Math.Min(runEnd, end);
      if (!AddRun(rewritten, overlapStart, overlapEnd - overlapStart, allocated))
        return false;
      if (runEnd > end && !AddRun(rewritten, end, runEnd - end, run.IsAllocated))
        return false;
    }

    if (rewritten.Count == 0 || rewritten[0].StartBlock != 0 || rewritten[^1].StartBlock + rewritten[^1].BlockCount != volumeBlocks)
      return false;
    runs.Clear();
    runs.AddRange(rewritten);
    return true;
  }

  private static bool IsAllocated(IReadOnlyList<Tux3BlockRun> runs, ulong block) {
    var low = 0;
    var high = runs.Count - 1;
    while (low <= high) {
      var mid = low + ((high - low) >> 1);
      var run = runs[mid];
      if (block < run.StartBlock) {
        high = mid - 1;
      } else if (block >= run.StartBlock + run.BlockCount) {
        low = mid + 1;
      } else {
        return run.IsAllocated;
      }
    }
    return false;
  }

  private readonly record struct PackedRoot(bool Direct, ushort CountOrDepth, ulong Block) {
    public ushort Count => this.CountOrDepth;
    public ushort Depth => this.CountOrDepth;

    public static PackedRoot Decode(ulong packed)
      => new((packed & (1UL << 63)) != 0, (ushort)((packed >> 48) & 0x7FFF), packed & AddressMask);
  }

  private readonly record struct DLeafEntry(ulong Logical, ulong Physical);

  private static Tux3NativeMetadataResult Invalid(string status)
    => new(false, false, [], [], status);
}
