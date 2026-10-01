#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ocfs2;

/// <summary>
/// Moves a file's clusters inside an OCFS2 volume, repoints the extent record
/// in its dinode, and — once every move is done — moves the allocation with it.
/// </summary>
/// <remarks>
/// <para>Only a file whose data is one run in its own dinode's leaf list is
/// movable (<see cref="Ocfs2Reader.FilePlacement.IsSingleRun"/>): relocating it
/// is the copy and the eight bytes that name the start. Everything else is
/// reported as reserved by the extent map and stays put.</para>
///
/// <para>The global bitmap is settled by <see cref="CommitAllocation"/> after the
/// last move rather than per move: a run held outside the volume gives its old
/// space up the moment it is lifted, and something else may move in before it is
/// put down, so per-move bit flips would free or claim the same cluster twice.
/// At the end the difference is exact — clusters the moved files left, minus
/// those they now occupy, go free; clusters they newly occupy are claimed — and
/// every flip is checked against the bitmap's current state.</para>
///
/// <para>A record is found by the file's full path and the block it still names,
/// so two files sharing a leaf name in different directories, or a run that
/// something else has moved over while it was held, cannot send the wrong one
/// somewhere.</para>
/// </remarks>
public sealed class Ocfs2BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first extent record inside the dinode's id2 union.</summary>
  private const int ExtentRecordsOffset = Ocfs2Reader.Id2Offset + 0x10;

  /// <summary>Bytes of one extent record.</summary>
  private const int ExtentRecordSize = 16;

  private int _blockSize;
  private long _firstDataByte;

  /// <summary>Every movable file: its dinode, where its run started and where it is now, and its length.</summary>
  private readonly Dictionary<string, (long Dinode, long Original, long Current, long Blocks)> _files = new(StringComparer.Ordinal);

  /// <summary>Reads the geometry and notes where every movable file's data starts.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var buffer = ReadWhole(image);
    this._blockSize = Ocfs2Reader.ReadBlockSize(buffer);
    if (this._blockSize <= 0)
      throw new InvalidDataException("OCFS2: the superblock does not name a block size.");

    this._files.Clear();
    foreach (var placement in Ocfs2Reader.ReadFilePlacements(buffer)) {
      if (!placement.IsSingleRun || placement.Size <= 0) continue;
      var run = placement.Extents[0];
      this._files[placement.Name] = (placement.DinodeBlkno, run.Blkno, run.Blkno, run.Blocks);
    }

    // Metadata — journal, inode groups, group descriptors, directory blocks —
    // sits among the data and is reported reserved, so the data region starts
    // right after the fixed system blocks. Taking the lowest file's block put
    // any hole in front of it outside what the planner may fill.
    this._firstDataByte = (long)Ocfs2Writer.FirstFileBlkno * this._blockSize;
  }

  /// <summary>The volume's block, which is also its cluster at these geometries.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the fixed system blocks.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all. Safe here because
  /// records are found by path and the bitmap is settled once at the end.
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

  /// <summary>Repoints the moved file's extent record; the bitmap follows in <see cref="CommitAllocation"/>.</summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => this.Repoint(image, fileName, oldOffset, newOffset);

  /// <summary>
  /// Repoints the moved file's extent record. Whether the old space is released
  /// does not matter here: the bitmap is settled once, in <see cref="CommitAllocation"/>.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length, bool releaseOldSpace)
    => this.Repoint(image, fileName, oldOffset, newOffset);

  private void Repoint(Stream image, string fileName, long oldOffset, long newOffset) {
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

    if (!this._files.TryGetValue(fileName, out var file) || file.Current != oldBlock)
      throw new InvalidOperationException(
        $"OCFS2: '{fileName}' is not a movable file whose run starts at block {oldBlock}.");

    this.RepointExtent(image, file.Dinode * this._blockSize, oldBlock, newBlock);
    this._files[fileName] = file with { Current = newBlock };
    image.Flush();
  }

  /// <summary>
  /// Settles the global bitmap after the moves: what the moved runs left goes
  /// free, what they now cover is claimed. Throws, before writing, if any of it
  /// disagrees with what the bitmap currently says.
  /// </summary>
  public void CommitAllocation(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var before = new HashSet<long>();
    var after = new HashSet<long>();
    foreach (var (_, original, current, blocks) in this._files.Values) {
      if (original == current) continue;
      for (var b = 0L; b < blocks; ++b) {
        before.Add(original + b);
        after.Add(current + b);
      }
    }
    if (before.Count == 0) return;

    var released = before.Except(after).Order().ToList();
    var claimed = after.Except(before).Order().ToList();
    var volume = Ocfs2Allocators.Open(image);
    volume.SetClusters(Runs(released), used: false);
    volume.SetClusters(Runs(claimed), used: true);
    foreach (var name in this._files.Keys.ToList())
      this._files[name] = this._files[name] with { Original = this._files[name].Current };
    image.Flush();
  }

  private static IEnumerable<(long Start, long Count)> Runs(List<long> sorted) {
    for (var i = 0; i < sorted.Count;) {
      var start = sorted[i];
      var j = i + 1;
      while (j < sorted.Count && sorted[j] == sorted[j - 1] + 1) ++j;
      yield return (start, j - i);
      i = j;
    }
  }

  /// <summary>Rewrites the extent record that starts at <paramref name="oldBlock" />.</summary>
  private void RepointExtent(Stream image, long dinodeOffset, long oldBlock, long newBlock) {
    Span<byte> header = stackalloc byte[8];
    image.Position = dinodeOffset + Ocfs2Reader.Id2Offset;
    image.ReadExactly(header);
    var treeDepth = BinaryPrimitives.ReadUInt16LittleEndian(header);
    var records = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
    if (treeDepth != 0 || records != 1)
      throw new NotSupportedException("OCFS2: only a file held in one leaf extent record is moved.");

    var record = new byte[ExtentRecordSize];
    image.Position = dinodeOffset + ExtentRecordsOffset;
    image.ReadExactly(record);
    if ((long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8)) != oldBlock)
      throw new InvalidOperationException(
        $"OCFS2: the dinode at block {dinodeOffset / this._blockSize} has no extent record " +
        $"starting at block {oldBlock}.");

    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), (ulong)newBlock);
    image.Position = dinodeOffset + ExtentRecordsOffset + 8;
    image.Write(record.AsSpan(8, 8));
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
