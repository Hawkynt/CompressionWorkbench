#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Xenix;

/// <summary>
/// Moves a file's blocks inside a System V volume and repoints the pointers
/// that name them.
/// </summary>
/// <remarks>
/// <para>A System V file's bytes are addressed one block at a time: ten
/// three-byte pointers in the inode, then as many four-byte pointers as the
/// indirect blocks below it hold. Moving a run of them is the copy and the
/// pointers that named it.</para>
///
/// <para>Free space is a chained cache in the superblock, not a bitmap, and
/// this writer only ever fills the in-line part of it — the chain is advisory,
/// which is why a read-only mount is unaffected by it. What must not survive a
/// move is a cache entry pointing at a block a file now occupies, so the caller
/// refreshes it once the bytes have stopped moving.</para>
/// </remarks>
public sealed class XenixBlockMover : IFilesystemBlockMover {

  private int _blockSize;
  private long _firstDataByte;

  /// <summary>Reads the geometry and where file data may start.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var reader = new XenixReader(image);
    this._blockSize = reader.BlockSize;
    this._firstDataByte = reader.FirstDataByte;
  }

  /// <summary>Block size in bytes, as the superblock's type code gives it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the superblock and the inode list.</summary>
  public long FirstDataByte => this._firstDataByte;

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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"Xenix: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all a " +
        "block pointer can name.");

    var oldBlock = oldOffset / this._blockSize;
    var newBlock = newOffset / this._blockSize;
    if (oldBlock == newBlock) return;
    if (newBlock > 0xFFFFFF)
      throw new NotSupportedException(
        $"Xenix: block {newBlock} is past the 24 bits an inode's pointer holds.");

    var blocks = (int)((length + this._blockSize - 1) / this._blockSize);

    long pointerOffset;
    bool inInode;
    {
      image.Position = 0;
      using var reader = new XenixReader(image);
      pointerOffset = -1;
      inInode = false;
      foreach (var (runOffset, runLength, namedAt, owner) in reader.EnumerateLayout()) {
        if (owner == null || namedAt < 0 || runOffset != oldOffset) continue;
        if (runLength < (long)blocks * this._blockSize) continue;
        pointerOffset = namedAt;
        inInode = namedAt < reader.FirstDataByte;
        break;
      }
    }

    if (pointerOffset < 0)
      throw new InvalidOperationException(
        $"Xenix: no pointer run names block {oldBlock}, so '{fileName}' cannot be repointed.");

    // A pointer inside an inode is three bytes, low byte first; one inside an
    // indirect block is a plain four-byte word.
    var stride = inInode ? XenixReader.InodePointerBytes : XenixReader.IndirectPointerBytes;
    Span<byte> pointer = stackalloc byte[XenixReader.IndirectPointerBytes];
    for (var i = 0; i < blocks; ++i) {
      var block = (uint)(newBlock + i);
      BinaryPrimitives.WriteUInt32LittleEndian(pointer, block);
      image.Position = pointerOffset + (long)i * stride;
      image.Write(pointer[..stride]);
    }

    image.Flush();
  }
}
