#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// In-place ext2/3/4 block mover. Moves block-aligned runs within an ext image and
/// repoints the owning inode's block map, the block bitmaps of whichever groups the
/// blocks live in, and — where the volume carries them — every checksum that covers
/// what changed (inode, extent-tree block, bitmap, group descriptor, superblock).
/// </summary>
/// <remarks>
/// The metadata work is done by <see cref="ExtModifier"/>, which reads each group's
/// descriptor rather than assuming group 0 and applies <c>metadata_csum</c> /
/// <c>gdt_csum</c> the same way its in-place add and remove do. An earlier version
/// addressed every inode through group 0's inode table and every block through group
/// 0's bitmap and recomputed no checksum, so on a volume made by <c>mkfs.ext4</c> a
/// moved file read back as "Bad message" and e2fsck reported the volume damaged.
/// </remarks>
public sealed class ExtBlockMover : IFilesystemBlockMover, IFilesystemMetadataMover {
  private int _blockSize;
  private uint _firstDataBlock;
  private uint _blocksCount;
  private uint _blocksPerGroup;
  private uint _groupCount;

  private const int SuperblockOffset = 1024;

  /// <summary>Initialises the mover from a byte buffer (legacy callers).</summary>
  public void Init(byte[] image) => ParseSuperblock(image.AsSpan(SuperblockOffset, 1024));

  /// <summary>Streaming init — reads only the superblock.</summary>
  public void Init(Stream image) {
    Span<byte> sb = stackalloc byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    ParseSuperblock(sb);
  }

  private void ParseSuperblock(ReadOnlySpan<byte> sb) {
    _blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]);
    _blockSize = 1024 << (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
    _blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[32..]);
    _firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..]);
    _groupCount = _blocksPerGroup == 0
      ? 0
      : (_blocksCount - _firstDataBlock + _blocksPerGroup - 1) / _blocksPerGroup;
  }

  /// <summary>Gets the first data byte.</summary>
  public long FirstDataByte => (long)_firstDataBlock * _blockSize;

  /// <summary>Gets the block size.</summary>
  public int BlockSize => _blockSize;

  /// <inheritdoc />
  public int AllocationBlockSize => _blockSize;

  /// <inheritdoc />
  /// <remarks>
  /// Each call finds the owning inode by path, checks that it really maps the run,
  /// and rewrites exactly the pointers or extents that name the run — so a
  /// fragmented file is simply several calls.
  /// </remarks>
  public bool RepointsRunsIndependently => true;

  private uint OffsetToBlock(long offset) => (uint)(offset / _blockSize);

  // ── IFilesystemMetadataMover ──────────────────────────────────────────

  /// <summary>
  /// Each group's block bitmap, inode bitmap and inode table. All three are
  /// located by fields in that group's descriptor, so moving one is a matter of
  /// writing the new block number there — which is how a real resize2fs shifts
  /// them about. The superblock, the descriptor table and their backups are
  /// pinned: their positions are computed from the geometry, not recorded.
  /// </summary>
  public IReadOnlySet<string> RelocatableMetadata {
    get {
      var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (var g = 0u; g < this._groupCount; ++g) {
        names.Add(BlockBitmapName(g));
        names.Add(InodeBitmapName(g));
        names.Add(InodeTableName(g));
      }
      return names;
    }
  }

  private static string BlockBitmapName(uint group) => $"ext block bitmap (group {group})";
  private static string InodeBitmapName(uint group) => $"ext inode bitmap (group {group})";
  private static string InodeTableName(uint group) => $"ext inode table (group {group})";

  /// <inheritdoc />
  public void UpdateMetadataAfterMove(Stream image, string metadataName,
      long oldOffset, long newOffset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadataName);

    var (fieldOffset, group) = ParseRegionName(metadataName);
    var oldBlock = OffsetToBlock(oldOffset);
    var newBlock = OffsetToBlock(newOffset);
    if (oldBlock == newBlock) return;
    var blocks = (uint)((length + _blockSize - 1) / _blockSize);

    ExtModifier.RepointGroupMetadata(image, fieldOffset, group, oldBlock, newBlock, blocks,
      block => IsLive((long)block * _blockSize, _blockSize, liveRanges));
  }

  /// <summary>Field offset inside the group descriptor, and the group.</summary>
  private static (int FieldOffset, uint Group) ParseRegionName(string name) {
    var open = name.LastIndexOf("(group ", StringComparison.OrdinalIgnoreCase);
    var close = name.LastIndexOf(')');
    if (open < 0 || close <= open
        || !uint.TryParse(name.AsSpan(open + 7, close - open - 7), out var group))
      throw new NotSupportedException($"ext: '{name}' is not a region this volume can be repointed at.");

    if (name.StartsWith("ext block bitmap", StringComparison.OrdinalIgnoreCase)) return (0, group);
    if (name.StartsWith("ext inode bitmap", StringComparison.OrdinalIgnoreCase)) return (4, group);
    if (name.StartsWith("ext inode table", StringComparison.OrdinalIgnoreCase)) return (8, group);
    throw new NotSupportedException($"ext: '{name}' is not a region this volume can be repointed at.");
  }

  /// <summary>Whether any live range covers part of this block.</summary>
  private static bool IsLive(long offset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges) {
    if (liveRanges == null) return false;
    foreach (var (start, len) in liveRanges)
      if (offset < start + len && start < offset + length)
        return true;
    return false;
  }

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

  /// <inheritdoc />
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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => this.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length, releaseOldSpace: true);

  /// <inheritdoc />
  /// <remarks>
  /// The run's old blocks are released unless it was held outside the volume while
  /// the rest moved, in which case it gave them up then and something else has very
  /// likely taken them.
  /// </remarks>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset,
      long length, bool releaseOldSpace) {
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);
    ExtModifier.RepointDataRun(image, fileName, OffsetToBlock(oldOffset), OffsetToBlock(newOffset), blockCount,
      releaseOldSpace);
  }
}
