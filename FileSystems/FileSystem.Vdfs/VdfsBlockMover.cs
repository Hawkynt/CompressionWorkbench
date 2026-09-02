#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Vdfs;

/// <summary>
/// Moves a file's payload inside a VDFS container and repoints its entry.
/// </summary>
/// <remarks>
/// A VDFS entry is 80 bytes: the name, then the byte offset its payload starts
/// at and the payload's length. There is no chain and no allocation map — the
/// offset is the whole of what says where a file lives — so relocating one is
/// the copy plus a four-byte write.
/// </remarks>
public sealed class VdfsBlockMover : IFilesystemBlockMover {

  /// <summary>Bytes per entry in the table.</summary>
  private const int EntrySize = 80;

  /// <summary>Offset of the payload pointer inside an entry.</summary>
  private const int EntryDataOffset = 64;

  /// <summary>Header field holding the entry count.</summary>
  private const int HeaderEntryCountOffset = 16;

  /// <summary>Header field holding where the entry table starts.</summary>
  private const int HeaderRootOffset = 32;

  private long _entriesStart;
  private int _entryCount;
  private long _dataStart;

  /// <summary>Reads the container's header.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    Span<byte> header = stackalloc byte[36];
    image.Position = 0;
    image.ReadExactly(header);

    this._entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderEntryCountOffset..]);
    var root = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderRootOffset..]);
    this._entriesStart = root > 0 ? root : 36;
    this._dataStart = this._entriesStart + (long)this._entryCount * EntrySize;
  }

  /// <summary>
  /// One byte. VDFS packs payloads end to end rather than on a grid, so the
  /// planner is free to place a file anywhere past the entry table.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a payload may occupy: past the header and the table.</summary>
  public long FirstDataByte => this._dataStart;

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
    if (this._entriesStart == 0) this.Init(image);
    if (oldOffset == newOffset) return;

    // The entry is found by the offset it still names rather than by the file's
    // name, so a duplicate name cannot send the wrong payload somewhere.
    Span<byte> pointer = stackalloc byte[4];
    for (var index = 0; index < this._entryCount; ++index) {
      var at = this._entriesStart + (long)index * EntrySize + EntryDataOffset;
      if (at + 4 > image.Length) break;

      image.Position = at;
      image.ReadExactly(pointer);
      if (BinaryPrimitives.ReadUInt32LittleEndian(pointer) != (uint)oldOffset) continue;

      BinaryPrimitives.WriteUInt32LittleEndian(pointer, (uint)newOffset);
      image.Position = at;
      image.Write(pointer);
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"VDFS: no entry points at offset {oldOffset:N0}, so '{fileName}' cannot be repointed.");
  }
}
