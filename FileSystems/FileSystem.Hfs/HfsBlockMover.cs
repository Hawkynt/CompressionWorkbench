#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Hfs;

/// <summary>
/// Moves a file's allocation blocks inside a classic HFS volume and repoints
/// the extent descriptor that named them, so the file reads the same from its
/// new home.
/// </summary>
/// <remarks>
/// <para>A fork is three extent descriptors in the catalog record — a starting
/// allocation block and a count, four bytes each — and whatever the extents
/// overflow file holds beyond them. Each call rewrites exactly the descriptor
/// whose blocks have just moved, which is what lets a file in several pieces
/// be moved one piece at a time.</para>
///
/// <para>The volume bitmap is not written per move. One run's old home is
/// routinely where another has just landed, so releasing as we go hands live
/// space out twice; the caller settles the bitmap once the pass is over, from
/// where the runs actually ended up.</para>
/// </remarks>
public sealed class HfsBlockMover : IFilesystemBlockMover {
  private const int MdbOffset = 1024;
  private const int SectorSize = 512;
  private const ushort HfsMagic = 0x4244;
  private const byte RecFile = 2;

  /// <summary>Offset of the data fork's extent record inside a file record.</summary>
  private const int DataForkExtents = 74;

  /// <summary>Offset of the resource fork's extent record inside a file record.</summary>
  private const int ResourceForkExtents = 86;

  /// <summary>Offset of the field naming the data fork's first allocation block.</summary>
  private const int DataForkFirstBlock = 24;

  /// <summary>Offset of the field naming the resource fork's first allocation block.</summary>
  private const int ResourceForkFirstBlock = 34;

  /// <summary>Descriptors an extent record holds before the overflow file takes over.</summary>
  private const int ExtentsPerRecord = 3;

  private int _blockSize;
  private int _totalBlocks;
  private long _allocationBase;
  private long _bitmapBase;
  private long _catalogOffset;
  private long _imageLength;

  /// <summary>Reads the master directory block and locates bitmap and catalog.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < MdbOffset + 162)
      throw new InvalidDataException("HFS: the image is too small to hold a master directory block.");

    Span<byte> mdb = stackalloc byte[SectorSize];
    image.Position = MdbOffset;
    image.ReadExactly(mdb);
    if (BinaryPrimitives.ReadUInt16BigEndian(mdb) != HfsMagic)
      throw new InvalidDataException("HFS: the master directory block does not carry the volume signature.");

    this._blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(mdb[20..]);
    if (this._blockSize <= 0) this._blockSize = SectorSize;
    this._totalBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb[18..]);
    this._allocationBase = (long)BinaryPrimitives.ReadUInt16BigEndian(mdb[28..]) * SectorSize;
    this._bitmapBase = (long)BinaryPrimitives.ReadUInt16BigEndian(mdb[14..]) * SectorSize;
    this._imageLength = image.Length;

    var catalogStartBlock = BinaryPrimitives.ReadUInt16BigEndian(mdb[150..]);
    var catalogBlockCount = BinaryPrimitives.ReadUInt16BigEndian(mdb[152..]);
    this._catalogOffset = catalogBlockCount == 0
      ? -1
      : this._allocationBase + (long)catalogStartBlock * this._blockSize;
  }

  /// <summary>An allocation block, as the master directory block sizes it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the boot blocks, the MDB and the bitmap.</summary>
  public long FirstDataByte => this._allocationBase;

  /// <summary>
  /// Each call repoints the descriptor naming the run it is given and leaves
  /// the fork's other descriptors alone, so an owner in several runs is simply
  /// several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
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
    if (this._catalogOffset < 0) return;

    if ((newOffset - this._allocationBase) % this._blockSize != 0)
      throw new NotSupportedException(
        $"HFS: {newOffset} is not on an allocation block boundary, which is all an extent " +
        "descriptor can name.");

    var oldBlock = (oldOffset - this._allocationBase) / this._blockSize;
    var newBlock = (newOffset - this._allocationBase) / this._blockSize;
    if (oldBlock == newBlock) return;
    if (newBlock is < 0 or > ushort.MaxValue)
      throw new NotSupportedException(
        $"HFS: allocation block {newBlock} is past the 65535 an extent descriptor holds.");

    using var cache = new SectorCache(image);
    if (!this.PatchExtentDescriptor(image, cache, fileName, (ushort)oldBlock, (ushort)newBlock))
      throw new InvalidOperationException(
        $"HFS: no catalog extent names allocation block {oldBlock}, so '{fileName}' cannot be " +
        "repointed — its extents may live in the overflow file, which this mover does not walk.");

    // The old blocks are deliberately not released here; see the remarks.
    var blocks = (length + this._blockSize - 1) / this._blockSize;
    for (var i = 0L; i < blocks; ++i)
      SetBitmapBit(image, this._bitmapBase, newBlock + i);

    this.MirrorAlternateMdb(image);
    image.Flush();
  }

  /// <summary>
  /// Writes the volume bitmap from the runs the volume actually holds.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. Releasing a run's old blocks as it
  /// moves cannot be right while other runs are still moving: an old home is
  /// routinely another run's new one, and clearing it hands live space out
  /// twice. From the finished layout the answer is simply what is covered.
  /// </remarks>
  public void SettleAllocationBitmap(Stream image, IEnumerable<(long Offset, long Length)> live) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(live);
    if (this._blockSize == 0) this.Init(image);

    var totalBlocks = this._totalBlocks > 0
      ? (int)Math.Min(this._totalBlocks, (this._imageLength - this._allocationBase) / this._blockSize)
      : (int)((this._imageLength - this._allocationBase) / this._blockSize);
    if (totalBlocks <= 0) return;

    var claimed = new bool[totalBlocks];
    foreach (var (offset, length) in live) {
      if (length <= 0 || offset < this._allocationBase) continue;
      var first = (offset - this._allocationBase) / this._blockSize;
      var last = (offset - this._allocationBase + length + this._blockSize - 1) / this._blockSize;
      for (var block = first; block < last && block < totalBlocks; ++block)
        if (block >= 0) claimed[block] = true;
    }

    var free = 0;
    for (var block = 0; block < totalBlocks; ++block)
      if (claimed[block]) SetBitmapBit(image, this._bitmapBase, block);
      else { ClearBitmapBit(image, this._bitmapBase, block); ++free; }

    // The MDB carries the free count as a number of its own rather than
    // counting the bitmap, so leaving it behind makes a sound volume read as
    // full or as emptier than it is.
    Span<byte> field = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)Math.Min(free, ushort.MaxValue));
    image.Position = MdbOffset + 34;
    image.Write(field);

    this.MirrorAlternateMdb(image);
    image.Flush();
  }

  /// <summary>
  /// Finds the extent descriptor that still names <paramref name="oldBlock" />
  /// and writes <paramref name="newBlock" /> into it. Returns whether one was
  /// found.
  /// </summary>
  private bool PatchExtentDescriptor(Stream image, SectorCache cache, string fileName,
      ushort oldBlock, ushort newBlock) {
    if (this._catalogOffset + 32 > this._imageLength) return false;

    var header = cache.Read(this._catalogOffset, 32);
    if ((sbyte)header[8] != 1) return false;                 // not a header node

    var btree = cache.Read(this._catalogOffset + 14, 30);
    var firstLeaf = (int)BinaryPrimitives.ReadUInt32BigEndian(btree.AsSpan(10));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(btree.AsSpan(18));
    if (nodeSize == 0) nodeSize = SectorSize;

    // The map names a file by its catalog entry, not by its path, so the leaf
    // name is what arrives here; a path would only ever match its last segment.
    var leafName = fileName;
    var slash = leafName.LastIndexOf('/');
    if (slash >= 0) leafName = leafName[(slash + 1)..];

    var node = firstLeaf;
    var visited = new HashSet<int>();
    Span<byte> field = stackalloc byte[2];
    while (node != 0 && visited.Add(node)) {
      var nodeOffset = this._catalogOffset + (long)node * nodeSize;
      if (nodeOffset + nodeSize > this._imageLength) break;

      var bytes = cache.Read(nodeOffset, nodeSize);
      if ((sbyte)bytes[8] != -1) break;                      // not a leaf node

      var records = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10));
      for (var r = 0; r < records; ++r) {
        var offsetAt = nodeSize - 2 * (r + 1);
        if (offsetAt < 12) break;
        var recordAt = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offsetAt));
        if (recordAt + 8 > nodeSize) continue;

        var keyLength = bytes[recordAt];
        if (keyLength < 6) continue;
        var nameLength = bytes[recordAt + 6];
        if (recordAt + 7 + nameLength > nodeSize) continue;
        var name = nameLength > 0 ? Encoding.Latin1.GetString(bytes, recordAt + 7, nameLength) : "";

        var dataAt = recordAt + 1 + keyLength;
        if ((dataAt & 1) != 0) ++dataAt;
        if (dataAt + 102 > nodeSize) continue;
        if (bytes[dataAt] != RecFile) continue;
        if (!name.Equals(leafName, StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("*", StringComparison.Ordinal)) continue;

        foreach (var (extentRecord, firstBlockField) in new[] {
            (DataForkExtents, DataForkFirstBlock),
            (ResourceForkExtents, ResourceForkFirstBlock) }) {
          for (var e = 0; e < ExtentsPerRecord; ++e) {
            var at = dataAt + extentRecord + e * 4;
            var startBlock = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at));
            var extentBlocks = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2));
            if (extentBlocks == 0) break;                    // unused descriptor: the fork ends
            if (startBlock != oldBlock) continue;

            BinaryPrimitives.WriteUInt16BigEndian(field, newBlock);
            image.Position = nodeOffset + at;
            image.Write(field);
            cache.Invalidate(nodeOffset + at, 2);

            // The record also carries the fork's first block on its own, which
            // only the first descriptor speaks for.
            if (e == 0) {
              image.Position = nodeOffset + dataAt + firstBlockField;
              image.Write(field);
              cache.Invalidate(nodeOffset + dataAt + firstBlockField, 2);
            }

            return true;
          }
        }
      }

      node = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0));
    }

    return false;
  }

  /// <summary>
  /// Copies the master directory block over the alternate one HFS keeps in the
  /// second-to-last sector. A driver reads whichever it finds intact, so
  /// leaving the copy behind would make the volume read two different ways.
  /// </summary>
  private void MirrorAlternateMdb(Stream image) {
    var sectors = this._imageLength / SectorSize;
    if (sectors < 4) return;

    var alternate = (sectors - 2) * SectorSize;
    if (alternate + SectorSize > this._imageLength) return;

    Span<byte> mdb = stackalloc byte[SectorSize];
    image.Position = MdbOffset;
    image.ReadExactly(mdb);
    image.Position = alternate;
    image.Write(mdb);
  }

  private static void SetBitmapBit(Stream image, long bitmapBase, long block) {
    var at = bitmapBase + block / 8;
    if (at < 0 || at >= image.Length) return;

    Span<byte> b = stackalloc byte[1];
    image.Position = at;
    image.ReadExactly(b);
    b[0] |= (byte)(1 << (int)(7 - block % 8));
    image.Position = at;
    image.Write(b);
  }

  private static void ClearBitmapBit(Stream image, long bitmapBase, long block) {
    var at = bitmapBase + block / 8;
    if (at < 0 || at >= image.Length) return;

    Span<byte> b = stackalloc byte[1];
    image.Position = at;
    image.ReadExactly(b);
    b[0] &= (byte)~(1 << (int)(7 - block % 8));
    image.Position = at;
    image.Write(b);
  }
}
