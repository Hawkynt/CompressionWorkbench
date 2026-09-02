#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.F2fs;

/// <summary>
/// Moves a file's data blocks inside an F2FS volume, repoints the address that
/// named each, and brings the volume's account of its segments along.
/// </summary>
/// <remarks>
/// <para>A block's address sits in the inode's address array or in one of the
/// node blocks below it, so repointing is one field. What reaches further is
/// everything else that records the same fact: the segment information table
/// keeps a validity bitmap and a count for each segment, and the summary area
/// maps every block back to the node that owns it. A move that leaves those
/// behind is a volume fsck calls corrupt.</para>
///
/// <para>A segment is typed, too, and a data block cannot live in one meant for
/// nodes — so the caller keeps a pass inside the region already given over to
/// file data, and this refuses anything else.</para>
/// </remarks>
public sealed class F2fsBlockMover : IFilesystemBlockMover {

  private const int SuperblockOffset = 1024;
  private const int SitEntrySize = 74;
  private const int SummaryEntrySize = 7;

  /// <summary>Segment types the format gives to file data.</summary>
  private const int WarmData = 1;

  private int _blockSize = 4096;
  private int _blocksPerSegment = 512;
  private int _sitBlock;
  private int _ssaBlock;
  private int _mainBlock;
  private int _segmentCount;

  /// <summary>Every address that changed, and what it changed to.</summary>
  private readonly Dictionary<long, long> _moved = [];

  /// <summary>Where the field naming each data block sits, keyed by the block.</summary>
  private readonly Dictionary<long, long> _addressField = [];

  /// <summary>Segments the checkpoint calls current, which a pass must not touch.</summary>
  private readonly HashSet<int> _currentSegments = [];

  /// <summary>Reads the geometry and notes which field names each data block.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = new byte[512];
    image.Position = SuperblockOffset;
    image.ReadExactly(superblock);
    if (BinaryPrimitives.ReadUInt32LittleEndian(superblock) != 0xF2F52010u)
      throw new InvalidDataException("F2FS: the superblock does not carry the volume signature.");

    this._blockSize = 1 << (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(16));
    if (this._blockSize < 512) this._blockSize = 4096;
    this._blocksPerSegment = 1 << (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(20));
    if (this._blocksPerSegment <= 0) this._blocksPerSegment = 512;

    this._segmentCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(68));
    var checkpointBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(76));
    this._sitBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(80));
    this._ssaBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(88));
    this._mainBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(92));

    // The six segments the checkpoint calls current are out of bounds. A block
    // in one of them has its summary read from the checkpoint's journal rather
    // than from the summary area, so a block moved into one is a block fsck
    // looks up in the wrong place.
    this._currentSegments.Clear();
    var checkpoint = new byte[512];
    if ((long)checkpointBlock * this._blockSize + 512 <= image.Length) {
      image.Position = (long)checkpointBlock * this._blockSize;
      image.ReadExactly(checkpoint);
      for (var i = 0; i < 3; ++i) {
        this._currentSegments.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(checkpoint.AsSpan(84 + i * 4)));
        this._currentSegments.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(checkpoint.AsSpan(36 + i * 4)));
      }
    }

    this._moved.Clear();
    this._addressField.Clear();
    foreach (var (block, at) in F2fsLayout.DataAddresses(image))
      this._addressField[block] = at;
  }

  /// <summary>A block, which is what an address counts in.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>
  /// First byte a data block may occupy: the start of the region the volume has
  /// already given over to file data.
  /// </summary>
  public long FirstDataByte { get; private set; }

  /// <summary>Last byte of that region, which a pass must also stay inside.</summary>
  public long DataRegionEnd { get; private set; }

  /// <summary>
  /// Works out the region file data lives in, from the types the segment table
  /// records.
  /// </summary>
  public void FindDataRegion(Stream image, IEnumerable<long> dataOffsets) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(dataOffsets);

    long first = long.MaxValue, last = 0;
    foreach (var offset in dataOffsets) {
      var segment = (int)(offset / this._blockSize - this._mainBlock) / this._blocksPerSegment;
      if (segment < 0 || segment >= this._segmentCount) continue;

      var start = (long)(this._mainBlock + segment * this._blocksPerSegment) * this._blockSize;
      first = Math.Min(first, start);
      last = Math.Max(last, start + (long)this._blocksPerSegment * this._blockSize);
    }

    this.FirstDataByte = first == long.MaxValue
      ? (long)this._mainBlock * this._blockSize
      : first;
    this.DataRegionEnd = Math.Max(this.FirstDataByte, last);

    // Stop the region at the first current segment inside it, for the same
    // reason a move into one is refused.
    foreach (var segment in this._currentSegments) {
      var start = (long)(this._mainBlock + segment * this._blocksPerSegment) * this._blockSize;
      if (start > this.FirstDataByte && start < this.DataRegionEnd) this.DataRegionEnd = start;
    }
  }

  /// <summary>
  /// Each call repoints the address naming the block it is given, so a file in
  /// several blocks is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A block may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full region be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"F2FS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is what an " +
        "address counts in.");

    if (newOffset < this.FirstDataByte || newOffset + length > this.DataRegionEnd)
      throw new NotSupportedException(
        "F2FS: a data block cannot leave the region the segment table gives over to file data, " +
        "because a segment carries one type for everything in it.");

    for (var at = newOffset; at < newOffset + length; at += this._blockSize)
      if (this._currentSegments.Contains(this.SegmentOf(at / this._blockSize)))
        throw new NotSupportedException(
          "F2FS: a block cannot be put in a segment the checkpoint calls current — its summary " +
          "would be looked for in the checkpoint's journal rather than the summary area.");

    Span<byte> field = stackalloc byte[4];
    var moved = 0L;
    while (moved < length) {
      var from = (oldOffset + moved) / this._blockSize;
      var to = (newOffset + moved) / this._blockSize;
      if (!this._addressField.Remove(from, out var at))
        throw new InvalidOperationException(
          $"F2FS: nothing names block {from}, so '{fileName}' cannot be repointed.");

      BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)to);
      image.Position = at;
      image.Write(field);

      this._addressField[to] = at;
      this._moved[from] = to;
      moved += this._blockSize;
    }

    image.Flush();
  }

  /// <summary>
  /// Brings the segment table and the summary area to where the blocks now are.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. The table says how many blocks of
  /// each segment are live and which, and the summary area says which node owns
  /// each block; both are keyed by where a block sits, so both move with it.
  /// </remarks>
  public void SettleSegmentTables(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._moved.Count == 0) return;

    // Every source is read before any is cleared, and every destination is
    // written last. One block's old home is routinely another's new one, so
    // clearing as we went wiped a summary a previous move had just written —
    // which fsck reads as an invalid data segment summary.
    var summaries = new Dictionary<long, byte[]>();
    foreach (var (from, _) in this._moved)
      summaries[from] = this.ReadSummary(image, from);

    foreach (var (from, _) in this._moved) {
      this.ClearValid(image, from);
      this.WriteSummary(image, from, new byte[SummaryEntrySize]);
    }

    foreach (var (from, to) in this._moved) {
      this.SetValid(image, to);
      this.WriteSummary(image, to, summaries[from]);
    }

    image.Flush();
  }

  /// <summary>Marks a block live in its segment's bitmap and counts it.</summary>
  private void SetValid(Stream image, long block) => this.SetValid(image, block, true);

  /// <summary>Marks a block dead in its segment's bitmap and stops counting it.</summary>
  private void ClearValid(Stream image, long block) => this.SetValid(image, block, false);

  private void SetValid(Stream image, long block, bool live) {
    var index = block - this._mainBlock;
    if (index < 0) return;

    var segment = (int)(index / this._blocksPerSegment);
    var withinSegment = (int)(index % this._blocksPerSegment);
    if (segment < 0 || segment >= this._segmentCount) return;

    var entriesPerBlock = this._blockSize / SitEntrySize;
    if (entriesPerBlock <= 0) return;

    var at = (long)(this._sitBlock + segment / entriesPerBlock) * this._blockSize
           + (segment % entriesPerBlock) * SitEntrySize;
    if (at + SitEntrySize > image.Length) return;

    var entry = new byte[SitEntrySize];
    image.Position = at;
    image.ReadExactly(entry);

    var packed = BinaryPrimitives.ReadUInt16LittleEndian(entry);
    var count = packed & 0x3FF;
    var type = packed >> 10;

    var mask = (byte)(1 << (7 - withinSegment % 8));
    var already = (entry[2 + withinSegment / 8] & mask) != 0;
    if (live && !already) { entry[2 + withinSegment / 8] |= mask; ++count; }
    else if (!live && already) { entry[2 + withinSegment / 8] &= (byte)~mask; --count; }
    else return;

    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)((type << 10) | (count & 0x3FF)));
    image.Position = at;
    image.Write(entry, 0, SitEntrySize);
  }

  /// <summary>The summary entry that says which node owns this block.</summary>
  private byte[] ReadSummary(Stream image, long block) {
    var at = this.SummaryOffset(block);
    var entry = new byte[SummaryEntrySize];
    if (at < 0 || at + SummaryEntrySize > image.Length) return entry;

    image.Position = at;
    image.ReadExactly(entry);
    return entry;
  }

  private void WriteSummary(Stream image, long block, byte[] entry) {
    var at = this.SummaryOffset(block);
    if (at < 0 || at + SummaryEntrySize > image.Length) return;

    image.Position = at;
    image.Write(entry, 0, SummaryEntrySize);
  }

  /// <summary>Which segment a block sits in.</summary>
  private int SegmentOf(long block) {
    var index = block - this._mainBlock;
    return index < 0 ? -1 : (int)(index / this._blocksPerSegment);
  }

  private long SummaryOffset(long block) {
    var index = block - this._mainBlock;
    if (index < 0) return -1;

    var segment = index / this._blocksPerSegment;
    if (segment < 0 || segment >= this._segmentCount) return -1;

    return (this._ssaBlock + segment) * (long)this._blockSize
         + index % this._blocksPerSegment * SummaryEntrySize;
  }

  /// <summary>The type the segment table gives a segment.</summary>
  internal static int SegmentTypeFor(int packed) => packed >> 10;

  /// <summary>The type file data is written with, which a pass must stay inside.</summary>
  internal static int DataSegmentType => WarmData;
}
