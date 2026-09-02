#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Bfs;

/// <summary>
/// Moves a file's block runs inside a BFS volume, repoints the run in its
/// inode, and moves the allocation with it.
/// </summary>
/// <remarks>
/// <para>A BFS file is a list of runs — twelve in the inode's data stream and
/// as many more as the indirect block holds — each one an allocation group, a
/// starting block inside it, and a length. Moving a run is the copy plus the
/// eight bytes that describe it, so a volume can be laid out again without
/// reading every file out and writing a fresh one.</para>
///
/// <para>The run is found by the block it still names rather than by the file's
/// name, which is what lets a file with several runs be moved one run at a
/// time: each call rewrites exactly the run whose bytes have just moved.</para>
/// </remarks>
public sealed class BfsBlockMover : IFilesystemBlockMover {

  /// <summary>Runs the inode's data stream holds before the indirect one.</summary>
  private const int DirectRuns = 12;

  /// <summary>Offset of the data stream inside an inode.</summary>
  private const int InodeDataStreamOffset = 72;

  private int _blockSize;
  private long _blocksPerAg;
  private long _bitmapOffset;
  private long _bitmapBits;
  private long _firstDataByte;

  /// <summary>Reads the geometry and finds the allocation bitmap.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    var head = new byte[(int)Math.Min(image.Length, 64 * 1024)];
    image.ReadExactly(head);
    var superblock = BfsSuperblock.TryParse(head);
    if (!superblock.Valid)
      throw new InvalidDataException("BFS: the superblock does not parse.");

    this._blockSize = (int)superblock.BlockSize;
    if (this._blockSize < 512)
      throw new InvalidDataException($"BFS: a {this._blockSize}-byte block is not a block size.");
    this._blocksPerAg = superblock.BlocksPerAg;

    // The bitmap starts where the log ends, and spans as many blocks as it
    // takes to hold one bit per block of the volume.
    var log = ReadRun(image, superblock.SuperblockOffset + 88, this._blocksPerAg);
    var bitmapBlock = log.Block + log.Length;
    var bitmapBlocks = Math.Max(1L,
      ((superblock.NumBlocks + 7) / 8 + this._blockSize - 1) / this._blockSize);
    this._bitmapOffset = bitmapBlock * this._blockSize;
    this._bitmapBits = bitmapBlocks * this._blockSize * 8;
    this._firstDataByte = (bitmapBlock + bitmapBlocks) * this._blockSize;
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the superblock, the log and the bitmap.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the run it is given and nothing else, so an owner
  /// scattered over several runs is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

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
        $"BFS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all a " +
        "block run can name.");

    var oldBlock = oldOffset / this._blockSize;
    var newBlock = newOffset / this._blockSize;
    if (oldBlock == newBlock) return;

    if (this._blocksPerAg > 0 && newBlock % this._blocksPerAg > ushort.MaxValue)
      throw new NotSupportedException(
        $"BFS: block {newBlock} sits {newBlock % this._blocksPerAg} into its allocation group, " +
        "past the 65535 a run's start field holds.");

    var at = this.FindRunNaming(image, oldBlock, out var runLength);
    if (at < 0)
      throw new InvalidOperationException(
        $"BFS: no block run names block {oldBlock}, so '{fileName}' cannot be repointed.");

    WriteRun(image, at, newBlock, runLength, this._blocksPerAg);

    // The bitmap says which blocks are taken; leaving it behind would let the
    // next file added to the volume be allocated straight on top of this one.
    this.SetBits(image, oldBlock, runLength, used: false);
    this.SetBits(image, newBlock, runLength, used: true);
    image.Flush();
  }

  /// <summary>
  /// The byte offset of the run that still starts at <paramref name="block" />,
  /// or -1. Every file's direct runs are searched first, then the runs its
  /// indirect block holds.
  /// </summary>
  private long FindRunNaming(Stream image, long block, out int runLength) {
    runLength = 0;
    image.Position = 0;
    var reader = new BfsReader(image);

    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      var inodeOffset = entry.InodeBlock * this._blockSize;
      if (inodeOffset + InodeDataStreamOffset + (DirectRuns + 2) * 8 > image.Length) continue;

      for (var i = 0; i < DirectRuns; ++i) {
        var at = inodeOffset + InodeDataStreamOffset + i * 8;
        var run = ReadRun(image, at, this._blocksPerAg);
        if (run.Length == 0) break;
        if (run.Block != block) continue;
        runLength = run.Length;
        return at;
      }

      var indirect = ReadRun(image, inodeOffset + InodeDataStreamOffset + (DirectRuns + 1) * 8,
        this._blocksPerAg);
      if (indirect.Length == 0) continue;

      var runsPerBlock = this._blockSize / 8;
      for (var b = 0; b < indirect.Length; ++b)
        for (var j = 0; j < runsPerBlock; ++j) {
          var at = (indirect.Block + b) * this._blockSize + (long)j * 8;
          if (at + 8 > image.Length) break;
          var run = ReadRun(image, at, this._blocksPerAg);
          if (run.Length == 0) break;
          if (run.Block != block) continue;
          runLength = run.Length;
          return at;
        }
    }

    return -1;
  }

  /// <summary>Reads a block run and resolves it to an absolute block.</summary>
  private static (long Block, int Length) ReadRun(Stream image, long at, long blocksPerAg) {
    if (at < 0 || at + 8 > image.Length) return (0, 0);
    Span<byte> run = stackalloc byte[8];
    image.Position = at;
    image.ReadExactly(run);
    var group = BinaryPrimitives.ReadUInt32LittleEndian(run);
    var start = BinaryPrimitives.ReadUInt16LittleEndian(run[4..]);
    var length = BinaryPrimitives.ReadUInt16LittleEndian(run[6..]);
    return (group * blocksPerAg + start, length);
  }

  /// <summary>Writes a block run, splitting an absolute block into group and start.</summary>
  private static void WriteRun(Stream image, long at, long block, int length, long blocksPerAg) {
    var group = blocksPerAg > 0 ? block / blocksPerAg : 0;
    var start = blocksPerAg > 0 ? block % blocksPerAg : block;

    Span<byte> run = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(run, (uint)group);
    BinaryPrimitives.WriteUInt16LittleEndian(run[4..], (ushort)start);
    BinaryPrimitives.WriteUInt16LittleEndian(run[6..], (ushort)length);
    image.Position = at;
    image.Write(run);
  }

  /// <summary>
  /// Flips <paramref name="count" /> allocation bits from
  /// <paramref name="startBlock" />. A set bit means allocated, most
  /// significant bit first inside each byte — the convention BFS uses.
  /// </summary>
  private void SetBits(Stream image, long startBlock, int count, bool used) {
    for (var i = 0; i < count; ++i) {
      var block = startBlock + i;
      if (block < 0 || block >= this._bitmapBits) break;

      var at = this._bitmapOffset + block / 8;
      if (at >= image.Length) break;

      image.Position = at;
      var current = image.ReadByte();
      if (current < 0) break;

      var mask = 1 << (int)(7 - block % 8);
      var updated = used ? current | mask : current & ~mask;
      if (updated == current) continue;

      image.Position = at;
      image.WriteByte((byte)updated);
    }
  }
}
