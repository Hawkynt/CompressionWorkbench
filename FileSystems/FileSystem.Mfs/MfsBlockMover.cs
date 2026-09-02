#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Mfs;

/// <summary>
/// In-place MFS block mover. Moves allocation-block-aligned extents within an
/// MFS image and patches the directory entry's first-block field so the file
/// remains reachable at its new location.
///
/// <para>MFS stores files in contiguous allocation blocks. Each directory entry
/// records a first-block field (u16 BE at +26) and a file size (u32 BE at +28).
/// The allocation map is derived implicitly from the union of directory entries,
/// so updating the directory entry's first-block pointer is sufficient to
/// redirect the file to its new location.</para>
/// </summary>
public sealed class MfsBlockMover : IFilesystemBlockMover {

  private const int SectorSize = 512;
  private const int MdbOffset = 1024;
  private const int DirStartOffset = MdbOffset + 128; // 1152

  private uint _blockSize;
  private ushort _firstAllocSector;
  private long _firstAllocOffset;
  private int _dirEnd;

  /// <summary>
  /// Initialises geometry from the MFS MDB. Must be called before any move.
  /// </summary>
  public void Init(Stream image) {
    var mdb = new byte[SectorSize];
    image.Position = MdbOffset;
    image.ReadExactly(mdb);

    var sig = BinaryPrimitives.ReadUInt16BigEndian(mdb);
    if (sig != 0xD2D7)
      throw new InvalidDataException("MFS: invalid MDB signature.");

    _blockSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(20));
    if (_blockSize == 0) _blockSize = 1024;
    _firstAllocSector = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(28));
    if (_firstAllocSector == 0) _firstAllocSector = 12;
    _firstAllocOffset = _firstAllocSector * SectorSize;
    _dirEnd = (int)_firstAllocOffset;
  }

  /// <summary>Byte offset of the first allocation block.</summary>
  public long DataOrigin => _firstAllocOffset;

  /// <summary>Allocation block size in bytes.</summary>
  public int BlockSize => (int)_blockSize;

  /// <summary>Converts a byte offset to a 0-based block index.</summary>
  public int OffsetToBlock(long offset) => (int)((offset - _firstAllocOffset) / _blockSize);

  /// <summary>Converts a 0-based block index to a byte offset.</summary>
  public long BlockToOffset(int block) => _firstAllocOffset + block * (long)_blockSize;

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
    var oldBlock = OffsetToBlock(oldOffset);
    var newBlock = OffsetToBlock(newOffset);

    // Walk directory entries and patch the matching file's first-block field.
    var header = new byte[40];
    var pos = DirStartOffset;
    while (pos + header.Length <= _dirEnd) {
      image.Position = pos;
      image.ReadExactly(header);
      var flags = header[0];
      if (flags == 0) break; // end of directory

      if ((flags & 0x80) != 0) {
        var firstBlock = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(26));
        var nameLen = header[38];
        var nameBytes = new byte[nameLen];
        image.Position = pos + 39;
        image.ReadExactly(nameBytes);
        var entryName = Encoding.ASCII.GetString(nameBytes);

        if (firstBlock == oldBlock &&
            (string.Equals(entryName, fileName, StringComparison.Ordinal) ||
             fileName == "*")) {
          // Patch the first-block field in place.
          var patch = new byte[2];
          BinaryPrimitives.WriteUInt16BigEndian(patch, (ushort)newBlock);
          image.Position = pos + 26;
          image.Write(patch);
          // Crash barrier: metadata commit durable before return.
          image.Flush();
          return;
        }
      }

      var entryLen = 39 + header[38];
      if ((entryLen & 1) != 0) entryLen++;
      pos += entryLen;
    }
  }
}
