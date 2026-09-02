#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ocfs2;

/// <summary>
/// Moves a file's clusters inside an OCFS2 volume, repoints the extent record
/// in its dinode, and moves the allocation with it.
/// </summary>
/// <remarks>
/// <para>A file this reader resolves is one extent record in its own dinode — a
/// starting block and a cluster count — so relocating it is the copy, the eight
/// bytes that name the start, and the bits in the global bitmap that say which
/// clusters are taken. Without the bitmap half, the next file added would be
/// allocated straight on top of one that had moved.</para>
///
/// <para>The dinode is found by the block it still names rather than by the
/// file's name, so two files sharing a leaf name in different directories
/// cannot send the wrong one somewhere.</para>
/// </remarks>
public sealed class Ocfs2BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first extent record inside the dinode's id2 union.</summary>
  private const int ExtentRecordsOffset = Ocfs2Reader.Id2Offset + 0x10;

  /// <summary>Bytes of one extent record.</summary>
  private const int ExtentRecordSize = 16;

  /// <summary>Offset of the tree depth inside the extent list.</summary>
  private const int TreeDepthOffset = Ocfs2Reader.Id2Offset;

  /// <summary>Offset of the used-record count inside the extent list.</summary>
  private const int NextFreeRecordOffset = Ocfs2Reader.Id2Offset + 4;

  private int _blockSize;
  private long _firstDataByte;
  private readonly List<(long Dinode, long Data)> _placements = [];

  /// <summary>Reads the geometry and notes where every file's data starts.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var buffer = ReadWhole(image);
    this._blockSize = Ocfs2Reader.ReadBlockSize(buffer);
    if (this._blockSize <= 0)
      throw new InvalidDataException("OCFS2: the superblock does not name a block size.");

    this._placements.Clear();
    var first = long.MaxValue;
    foreach (var placement in Ocfs2Reader.ReadFilePlacements(buffer)) {
      if (placement.Inline || placement.Size <= 0 || placement.DataBlkno <= 0) continue;
      this._placements.Add((placement.DinodeBlkno, placement.DataBlkno));
      first = Math.Min(first, placement.DataBlkno * this._blockSize);
    }

    this._firstDataByte = first == long.MaxValue
      ? (long)Ocfs2Writer.GlobalBitmapGroupBlkno * this._blockSize
      : first;
  }

  /// <summary>The volume's block, which is also its cluster at these geometries.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the system inodes and their groups.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"OCFS2: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all an " +
        "extent record can name.");

    var oldBlock = oldOffset / this._blockSize;
    var newBlock = newOffset / this._blockSize;
    if (oldBlock == newBlock) return;

    var index = this._placements.FindIndex(p => p.Data == oldBlock);
    if (index < 0)
      throw new InvalidOperationException(
        $"OCFS2: no dinode names block {oldBlock}, so '{fileName}' cannot be repointed.");

    var dinodeOffset = this._placements[index].Dinode * this._blockSize;
    var clusters = this.RepointExtent(image, dinodeOffset, oldBlock, newBlock);
    this._placements[index] = (this._placements[index].Dinode, newBlock);

    // The bitmap says which clusters are taken; leaving it behind would let the
    // next file added to the volume be allocated straight on top of this one.
    this.SetBits(image, oldBlock, clusters, used: false);
    this.SetBits(image, newBlock, clusters, used: true);
    image.Flush();
  }

  /// <summary>
  /// Rewrites the extent record that starts at <paramref name="oldBlock" /> and
  /// returns how many clusters it covers.
  /// </summary>
  private int RepointExtent(Stream image, long dinodeOffset, long oldBlock, long newBlock) {
    Span<byte> header = stackalloc byte[8];
    image.Position = dinodeOffset + TreeDepthOffset;
    image.ReadExactly(header);
    var treeDepth = BinaryPrimitives.ReadUInt16LittleEndian(header);
    if (treeDepth != 0)
      throw new NotSupportedException(
        "OCFS2: this file's extents hang off an interior tree, which nothing here can rewrite.");

    image.Position = dinodeOffset + NextFreeRecordOffset;
    image.ReadExactly(header[..2]);
    var records = BinaryPrimitives.ReadUInt16LittleEndian(header);

    var record = new byte[ExtentRecordSize];
    for (var i = 0; i < records; ++i) {
      var at = dinodeOffset + ExtentRecordsOffset + (long)i * ExtentRecordSize;
      if (at + ExtentRecordSize > image.Length) break;

      image.Position = at;
      image.ReadExactly(record);
      var clusters = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
      if (clusters == 0) continue;
      if ((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8)) != oldBlock) continue;

      BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), (ulong)newBlock);
      image.Position = at + 8;
      image.Write(record.AsSpan(8, 8));
      return clusters;
    }

    throw new InvalidOperationException(
      $"OCFS2: the dinode at block {dinodeOffset / this._blockSize} has no extent record " +
      $"starting at block {oldBlock}.");
  }

  /// <summary>
  /// Flips <paramref name="count" /> allocation bits from
  /// <paramref name="startCluster" />. A set bit means allocated.
  /// </summary>
  private void SetBits(Stream image, long startCluster, int count, bool used) {
    var bitmapOffset = (long)Ocfs2Writer.GlobalBitmapGroupBlkno * this._blockSize
                     + Ocfs2Writer.BitmapInGroupOffset;
    var bits = (this._blockSize - Ocfs2Writer.BitmapInGroupOffset) * 8;

    for (var i = 0; i < count; ++i) {
      var cluster = startCluster + i;
      if (cluster < 0 || cluster >= bits) break;

      var at = bitmapOffset + cluster / 8;
      if (at >= image.Length) break;

      image.Position = at;
      var current = image.ReadByte();
      if (current < 0) break;

      var mask = 1 << (int)(cluster % 8);
      var updated = used ? current | mask : current & ~mask;
      if (updated == current) continue;

      image.Position = at;
      image.WriteByte((byte)updated);
    }
  }

  /// <summary>The whole image as bytes — the reader's structures are walked that way.</summary>
  private static byte[] ReadWhole(Stream image) {
    image.Position = 0;
    if (image.Length > Array.MaxLength)
      throw new NotSupportedException(
        $"OCFS2: a {image.Length:N0}-byte volume is past what this pass can walk in memory.");

    var buffer = new byte[image.Length];
    image.ReadExactly(buffer);
    return buffer;
  }
}
