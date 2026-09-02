#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ods1;

/// <summary>
/// Moves a file's blocks inside an ODS-1 volume and repoints its file header.
/// </summary>
/// <remarks>
/// <para>Files-11 describes a file with retrieval pointers in its header: each
/// is a block count and a split 32-bit LBN, and the header's map area holds as
/// many as the file needs. A file laid out in one run has one pointer, and
/// moving it is the copy plus rewriting that pointer's two halves.</para>
///
/// <para>A file with more than one pointer is refused. Restating a whole map
/// area is a different operation, and repointing only the first would leave the
/// rest of the file where it was.</para>
/// </remarks>
public sealed class Ods1BlockMover : IFilesystemBlockMover {

  /// <summary>Bytes per logical block.</summary>
  private const int LbnSize = 512;

  /// <summary>Home block's field naming where the index file starts.</summary>
  private const int HomeIndexFileOffset = 0x040;

  /// <summary>Headers this pass will look through — the reader walks the same number.</summary>
  private const int HeaderScanLimit = 64;

  private uint _indexFileLbn = 4;

  /// <summary>Reads where the file headers begin.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var homeOffset = (long)1 * LbnSize;
    if (homeOffset + LbnSize > image.Length) return;

    Span<byte> field = stackalloc byte[2];
    image.Position = homeOffset + HomeIndexFileOffset;
    image.ReadExactly(field);
    var lbn = BinaryPrimitives.ReadUInt16LittleEndian(field);
    this._indexFileLbn = lbn == 0 ? 4u : lbn;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => LbnSize;

  /// <summary>First byte a file may occupy: past the headers this pass walks.</summary>
  public long FirstDataByte => (long)(this._indexFileLbn + HeaderScanLimit) * LbnSize;

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
    if (this._indexFileLbn == 0) this.Init(image);

    var oldLbn = (uint)(oldOffset / LbnSize);
    var newLbn = (uint)(newOffset / LbnSize);
    if (oldLbn == newLbn) return;

    var header = new byte[LbnSize];
    for (var i = 0; i < HeaderScanLimit; ++i) {
      var at = (long)(this._indexFileLbn + i) * LbnSize;
      if (at + LbnSize > image.Length) break;

      image.Position = at;
      image.ReadExactly(header);
      if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) == 0) continue;  // free header

      var mapOffset = header[1] * 2;
      if (mapOffset + 6 > LbnSize) continue;

      // The header is found by the block it still names, so a duplicate name
      // cannot send the wrong file somewhere.
      var high = (uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(mapOffset + 2));
      var low = (uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(mapOffset + 4));
      if (((high << 16) | low) != oldLbn) continue;

      // A second pointer means the file is in more than one piece.
      if (mapOffset + 12 <= LbnSize) {
        var nextHigh = (uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(mapOffset + 8));
        var nextLow = (uint)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(mapOffset + 10));
        if (((nextHigh << 16) | nextLow) != 0)
          throw new NotSupportedException(
            $"ODS-1: '{fileName}' is described by more than one retrieval pointer, which this " +
            "pass cannot restate.");
      }

      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(mapOffset + 2), (ushort)(newLbn >> 16));
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(mapOffset + 4), (ushort)(newLbn & 0xFFFF));
      image.Position = at + mapOffset + 2;
      image.Write(header.AsSpan(mapOffset + 2, 4));
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"ODS-1: no file header names block {oldLbn}, so '{fileName}' cannot be repointed.");
  }
}
