using System.Buffers.Binary;

namespace FileSystem.HfsPlus;

/// <summary>
/// An HFS+ or HFSX volume held in memory for editing: its volume header, allocation file and the
/// three B-tree files, each edited as its ordered leaf records (<see cref="HfsPlusBTreeFile"/>).
/// <see cref="Flush"/> writes every changed tree back, the free-block count and dates into the
/// volume header, and mirrors that header into the alternate one.
/// </summary>
/// <remarks>
/// <para>
/// Opening refuses what an edit could not keep consistent: a special file continued in the
/// extents overflow file (those extents would have to be edited too), an allocation file too
/// small for the volume, and a journal that still holds transactions — a write beside them would
/// be undone or corrupted by the replay a mount performs. A journal whose start equals its end
/// holds nothing, so editing the volume directly leaves it as consistent as the journal says.
/// Sources: Apple TN1150 "HFS Plus Volume Format" (volume header, allocation file, B-trees,
/// journal) — the normative specification.
/// </para>
/// </remarks>
internal sealed class HfsPlusVolumeEditor {
  internal const int VolumeHeaderOffset = 1024;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX"
  internal const int AllocationForkOffset = 112;
  internal const int ExtentsForkOffset = 192;
  internal const int CatalogForkOffset = 272;
  internal const int AttributesForkOffset = 352;
  private const uint VolumeJournaledMask = 1u << 13;  // kHFSVolumeJournaledBit
  private const byte CaseFolding = 0xCF;               // kHFSCaseFolding
  private const byte BinaryCompare = 0xBC;             // kHFSBinaryCompare
  private const ushort AttributesNodeSize = 4096;
  private const ushort AttributesMaxKeyLength = 266;   // kHFSPlusAttrKeyMaximumLength
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly List<(uint StartBlock, uint BlockCount)> _allocationExtents;

  internal byte[] Image { get; }
  internal uint BlockSize { get; }
  internal uint TotalBlocks { get; }
  internal bool IsHfsx { get; }
  internal HfsPlusBTreeFile Catalog { get; }
  internal HfsPlusBTreeFile Extents { get; }
  internal HfsPlusBTreeFile? Attributes { get; private set; }

  /// <summary>Whether the catalog compares names case-sensitively (an HFSX volume declaring kHFSBinaryCompare).</summary>
  internal bool CaseSensitive => this.Catalog.KeyCompareType == BinaryCompare;

  internal Span<byte> VolumeHeader => this.Image.AsSpan(VolumeHeaderOffset, 512);

  private HfsPlusVolumeEditor(byte[] image, uint blockSize, uint totalBlocks, bool isHfsx) {
    this.Image = image;
    this.BlockSize = blockSize;
    this.TotalBlocks = totalBlocks;
    this.IsHfsx = isHfsx;
    this._allocationExtents = this.ForkExtents(AllocationForkOffset, "allocation file");
    if (this._allocationExtents.Sum(static e => (long)e.BlockCount) * blockSize * 8 < totalBlocks)
      throw new InvalidDataException("HFS+: the allocation file is too small for the volume.");
    this.Catalog = HfsPlusBTreeFile.Open(this, CatalogForkOffset, this.CatalogComparison, "catalog file")
                   ?? throw new InvalidDataException("HFS+: the volume has no catalog file.");
    this.Extents = HfsPlusBTreeFile.Open(this, ExtentsForkOffset, static _ => CompareExtentKeys, "extents overflow file")
                   ?? throw new InvalidDataException("HFS+: the volume has no extents overflow file.");
    this.Attributes = HfsPlusBTreeFile.Open(this, AttributesForkOffset, static _ => CompareAttributeKeys, "attributes file");
  }

  /// <summary>Opens <paramref name="image"/> (edited in place) for editing, or refuses it.</summary>
  internal static HfsPlusVolumeEditor Open(byte[] image) {
    if (image.Length < VolumeHeaderOffset + 512) throw new InvalidDataException("HFS+: the image is smaller than a volume header.");
    var vh = image.AsSpan(VolumeHeaderOffset, 512);
    var signature = BinaryPrimitives.ReadUInt16BigEndian(vh);
    if (signature != HfsPlusSignature && signature != HfsxSignature)
      throw new InvalidDataException($"HFS+: no volume header signature (0x{signature:X4}).");
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh[40..]);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
    if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
      throw new InvalidDataException($"HFS+: the volume declares an allocation block size of {blockSize}.");
    if ((long)totalBlocks * blockSize > image.Length)
      throw new InvalidDataException("HFS+: the volume is larger than the image holding it.");
    RequireEmptyJournal(image, vh, blockSize);
    return new HfsPlusVolumeEditor(image, blockSize, totalBlocks, signature == HfsxSignature);
  }

  /// <summary>
  /// Refuses a journaled volume whose journal holds transactions (TN1150 "Journal": the journal
  /// is empty when its header's start equals its end), one whose journal lives on another device,
  /// and one whose journal header is not recognisable.
  /// </summary>
  private static void RequireEmptyJournal(byte[] image, ReadOnlySpan<byte> vh, uint blockSize) {
    if ((BinaryPrimitives.ReadUInt32BigEndian(vh[4..]) & VolumeJournaledMask) == 0) return;
    var journal = HfsPlusJournal.Locate(image, vh, blockSize)
                  ?? throw new NotSupportedException("HFS+: the volume is journaled but its journal cannot be found; it is not edited.");
    if (journal.OnOtherDevice)
      throw new NotSupportedException("HFS+: the volume's journal lives on another device; the in-place editor cannot see it.");
    if (journal.NeedsInitialization) return;
    if (!journal.TryReadHeader(image, out var header))
      throw new NotSupportedException("HFS+: the volume's journal header is not valid; the in-place editor does not edit it.");
    if (header.Start != header.End)
      throw new NotSupportedException("HFS+: the volume's journal holds transactions not yet replayed; mount the volume (or replay the journal) before editing it.");
  }

  // ── Key order ───────────────────────────────────────────────────────────

  /// <summary>
  /// Catalog keys (TN1150 HFSPlusCatalogKey: keyLength, parentID, name length, UTF-16BE name)
  /// order by parent ID, then name: case-folded (TN1150 FastUnicodeCompare) for kHFSCaseFolding,
  /// by UTF-16 code unit for kHFSBinaryCompare. An HFS+ volume always folds case.
  /// </summary>
  private Comparison<byte[]> CatalogComparison(byte keyCompareType) {
    var binary = this.IsHfsx && keyCompareType == BinaryCompare;
    if (this.IsHfsx && keyCompareType is not (BinaryCompare or CaseFolding))
      throw new NotSupportedException($"HFS+: the catalog declares key compare type 0x{keyCompareType:X2}.");
    return (a, b) => {
      var pa = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(2));
      var pb = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2));
      if (pa != pb) return pa.CompareTo(pb);
      var na = CatalogName(a);
      var nb = CatalogName(b);
      return binary ? string.CompareOrdinal(na, nb) : HfsPlusCaseFold.Compare(na, nb);
    };
  }

  /// <summary>The name a catalog key stores.</summary>
  internal static string CatalogName(byte[] record) {
    var length = BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(6));
    return System.Text.Encoding.BigEndianUnicode.GetString(record, 8, length * 2);
  }

  /// <summary>Extents keys (keyLength, forkType, pad, fileID, startBlock) order by fileID, forkType, startBlock (TN1150).</summary>
  internal static int CompareExtentKeys(byte[] a, byte[] b) {
    var order = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(4)).CompareTo(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)));
    if (order != 0) return order;
    order = a[2].CompareTo(b[2]);
    return order != 0 ? order : BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(8)).CompareTo(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)));
  }

  /// <summary>
  /// Attribute keys (keyLength, pad, fileID, startBlock, name length, UTF-16BE name) order by
  /// fileID, then name by UTF-16 code unit with a prefix first, then startBlock (TN1150).
  /// </summary>
  internal static int CompareAttributeKeys(byte[] a, byte[] b) {
    var order = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(4)).CompareTo(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)));
    if (order != 0) return order;
    var la = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(12));
    var lb = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(12));
    for (var i = 0; i < Math.Min(la, lb); i++) {
      order = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(14 + 2 * i)).CompareTo(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(14 + 2 * i)));
      if (order != 0) return order;
    }
    order = la.CompareTo(lb);
    return order != 0 ? order : BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(8)).CompareTo(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)));
  }

  // ── Forks in the volume header ──────────────────────────────────────────

  /// <summary>
  /// The extents of the special file whose HFSPlusForkData the volume header holds at
  /// <paramref name="forkOffset"/>; refused when they fall short of its totalBlocks (the rest
  /// would be in the extents overflow file) or leave the volume.
  /// </summary>
  internal List<(uint StartBlock, uint BlockCount)> ForkExtents(int forkOffset, string name) {
    var fork = this.Image.AsSpan(VolumeHeaderOffset + forkOffset, 80);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(fork[12..]);
    var extents = new List<(uint, uint)>(8);
    long covered = 0;
    for (var k = 0; k < 8; k++) {
      var count = BinaryPrimitives.ReadUInt32BigEndian(fork[(20 + k * 8)..]);
      if (count == 0) break;
      var start = BinaryPrimitives.ReadUInt32BigEndian(fork[(16 + k * 8)..]);
      if ((long)start + count > this.TotalBlocks) throw new InvalidDataException($"HFS+: the {name} has an extent past the end of the volume.");
      extents.Add((start, count));
      covered += count;
    }
    if (covered < totalBlocks)
      throw new NotSupportedException($"HFS+: the {name} continues in the extents overflow file; the in-place editor does not edit it there.");
    return extents;
  }

  /// <summary>Records a special file's extents (and the size they make) in its volume-header fork.</summary>
  internal void SetForkExtents(int forkOffset, IReadOnlyList<(uint StartBlock, uint BlockCount)> extents) {
    var fork = this.Image.AsSpan(VolumeHeaderOffset + forkOffset, 80);
    var blocks = extents.Sum(static e => (long)e.BlockCount);
    BinaryPrimitives.WriteUInt64BigEndian(fork, (ulong)(blocks * this.BlockSize));
    BinaryPrimitives.WriteUInt32BigEndian(fork[12..], (uint)blocks);
    fork[16..].Clear();
    for (var k = 0; k < extents.Count; k++) {
      BinaryPrimitives.WriteUInt32BigEndian(fork[(16 + k * 8)..], extents[k].StartBlock);
      BinaryPrimitives.WriteUInt32BigEndian(fork[(20 + k * 8)..], extents[k].BlockCount);
    }
  }

  /// <summary>
  /// The attributes file, created empty when the volume has none: a 4 KiB-node B-tree whose keys
  /// compare binary and are variable in index nodes, as TN1150 lays out the attributes file.
  /// </summary>
  internal HfsPlusBTreeFile EnsureAttributes() {
    if (this.Attributes is { } existing) return existing;
    var blocks = (uint)Math.Max(1, (2 * AttributesNodeSize + this.BlockSize - 1) / this.BlockSize);
    var start = this.AllocateContiguous(blocks) ?? throw new NotSupportedException("HFS+: no room for an attributes file.");
    List<(uint, uint)> extents = [(start, blocks)];
    this.SetForkExtents(AttributesForkOffset, extents);
    BinaryPrimitives.WriteUInt32BigEndian(this.VolumeHeader[(AttributesForkOffset + 8)..], blocks * this.BlockSize);  // clumpSize
    return this.Attributes = HfsPlusBTreeFile.CreateEmpty(this, AttributesForkOffset, extents, AttributesNodeSize,
      AttributesMaxKeyLength, 0, 6, CompareAttributeKeys);
  }

  // ── Allocation file ─────────────────────────────────────────────────────

  private int BitmapByte(uint block) {
    var byteIndex = (long)block / 8;
    foreach (var (start, count) in this._allocationExtents) {
      var bytes = (long)count * this.BlockSize;
      if (byteIndex < bytes) return (int)((long)start * this.BlockSize + byteIndex);
      byteIndex -= bytes;
    }
    throw new InvalidDataException("HFS+: block past the allocation file.");
  }

  internal bool IsAllocated(uint block)
    => block >= this.TotalBlocks || (this.Image[this.BitmapByte(block)] & (0x80 >> (int)(block & 7))) != 0;

  private void SetAllocated(uint block, bool used) {
    var at = this.BitmapByte(block);
    var mask = (byte)(0x80 >> (int)(block & 7));
    var was = (this.Image[at] & mask) != 0;
    if (was == used) throw new InvalidDataException($"HFS+: block {block} is already {(used ? "allocated" : "free")}.");
    this.Image[at] = used ? (byte)(this.Image[at] | mask) : (byte)(this.Image[at] & ~mask);
    var free = BinaryPrimitives.ReadUInt32BigEndian(this.VolumeHeader[48..]);
    BinaryPrimitives.WriteUInt32BigEndian(this.VolumeHeader[48..], used ? free - 1 : free + 1);
  }

  /// <summary>Allocates <paramref name="count"/> blocks starting at <paramref name="start"/> when all are free.</summary>
  internal bool TryAllocateAt(uint start, uint count) {
    if ((long)start + count > this.TotalBlocks) return false;
    for (var b = start; b < start + count; b++)
      if (this.IsAllocated(b)) return false;
    for (var b = start; b < start + count; b++) this.SetAllocated(b, true);
    return true;
  }

  /// <summary>The first run of <paramref name="count"/> free blocks, allocated; null when there is none.</summary>
  internal uint? AllocateContiguous(uint count) {
    if (count == 0) return null;
    uint run = 0, runStart = 0;
    for (var b = 0u; b < this.TotalBlocks; b++) {
      if (this.IsAllocated(b)) { run = 0; continue; }
      if (run++ == 0) runStart = b;
      if (run == count) {
        for (var i = 0u; i < count; i++) this.SetAllocated(runStart + i, true);
        return runStart;
      }
    }
    return null;
  }

  /// <summary>
  /// Allocates <paramref name="count"/> blocks: one run when the volume has one that long, else
  /// the free runs in volume order until the count is met. Null (nothing allocated) when the
  /// volume has fewer free blocks.
  /// </summary>
  internal List<(uint StartBlock, uint BlockCount)>? Allocate(uint count) {
    if (count == 0) return [];
    if (this.AllocateContiguous(count) is { } start) return [(start, count)];
    var runs = new List<(uint, uint)>();
    var left = count;
    uint run = 0, runStart = 0;
    for (var b = 0u; b <= this.TotalBlocks && left > 0; b++) {
      if (b < this.TotalBlocks && !this.IsAllocated(b)) {
        if (run++ == 0) runStart = b;
        if (run < left) continue;
      }
      if (run > 0) {
        var take = Math.Min(run, left);
        runs.Add((runStart, take));
        left -= take;
        run = 0;
      }
    }
    if (left > 0) return null;
    foreach (var (s, c) in runs)
      for (var i = 0u; i < c; i++) this.SetAllocated(s + i, true);
    return runs;
  }

  /// <summary>Frees (and, when asked, zeroes) every block of the given extents.</summary>
  internal void Free(IEnumerable<(uint StartBlock, uint BlockCount)> extents, bool wipe) {
    foreach (var (start, count) in extents) {
      if ((long)start + count > this.TotalBlocks) throw new InvalidDataException("HFS+: an extent lies past the end of the volume.");
      if (wipe) this.Image.AsSpan((int)((long)start * this.BlockSize), (int)((long)count * this.BlockSize)).Clear();
      for (var b = start; b < start + count; b++) this.SetAllocated(b, false);
    }
  }

  // ── Volume header ───────────────────────────────────────────────────────

  internal uint ReadHeader(int offset) => BinaryPrimitives.ReadUInt32BigEndian(this.VolumeHeader[offset..]);
  internal void WriteHeader(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(this.VolumeHeader[offset..], value);

  /// <summary>Hands out the next catalog node ID (volume header nextCatalogID).</summary>
  internal uint NextCatalogId() {
    var next = Math.Max(this.ReadHeader(64), 16u);
    if (next == uint.MaxValue) throw new NotSupportedException("HFS+: the volume has run out of catalog node IDs.");
    this.WriteHeader(64, next + 1);
    return next;
  }

  internal static uint HfsTimestamp(DateTime moment) {
    if (moment < HfsEpoch) return 0;
    var seconds = (moment - HfsEpoch).TotalSeconds;
    return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  /// <summary>
  /// Writes every changed B-tree back, stamps modifyDate, counts the write, and mirrors the
  /// volume header into the alternate header 1024 bytes before the volume's end (TN1150).
  /// </summary>
  internal void Flush() {
    this.Catalog.Flush();
    this.Extents.Flush();
    this.Attributes?.Flush();
    this.WriteHeader(20, HfsTimestamp(DateTime.UtcNow));       // modifyDate
    this.WriteHeader(68, this.ReadHeader(68) + 1);             // writeCount
    var alternate = (long)this.TotalBlocks * this.BlockSize - 1024;
    this.VolumeHeader.CopyTo(this.Image.AsSpan((int)alternate, 512));
  }
}
