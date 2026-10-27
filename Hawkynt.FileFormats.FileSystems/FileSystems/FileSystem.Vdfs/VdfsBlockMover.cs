using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Vdfs;

/// <summary>
/// Moves a file's payload inside a VDF volume and repoints its catalog entry.
/// </summary>
/// <remarks>
/// A catalog entry is 80 bytes: the name, then the byte offset its payload starts at, its
/// size, its type flags. There is no chain and no allocation map — the offset is the whole
/// of what says where a file lives — so relocating one is the copy plus a four-byte write.
/// A ZippedStream moves as one run, the same as raw bytes.
/// </remarks>
public sealed class VdfsBlockMover : IFilesystemBlockMover {

  private long _entriesStart;
  private int _entryCount;
  private long _dataStart;

  /// <summary>Reads the volume's header.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    Span<byte> header = stackalloc byte[VdfsLayout.HeaderSize];
    image.Position = 0;
    image.ReadExactly(header);

    this._entryCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header[VdfsLayout.EntryCountOffset..]), int.MaxValue);
    var catalog = BinaryPrimitives.ReadUInt32LittleEndian(header[VdfsLayout.CatalogOffsetOffset..]);
    this._entriesStart = catalog > 0 ? catalog : VdfsLayout.HeaderSize;
    this._dataStart = Math.Max(VdfsLayout.HeaderSize, this._entriesStart + (long)this._entryCount * VdfsLayout.EntrySize);
  }

  /// <summary>
  /// One byte. Payloads are packed end to end rather than on a grid, so the planner is
  /// free to place a file anywhere past the catalog.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a payload may occupy: past the header and the catalog.</summary>
  public long FirstDataByte => this._dataStart;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length overwrites its own
    // tail, and copying that front to back reads bytes the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._entriesStart == 0) this.Init(image);
    if (oldOffset == newOffset) return;

    // The entry is found by the offset it still names rather than by the file's name, so a
    // duplicate name cannot send the wrong payload somewhere. Directory entries carry a child
    // index in the same field.
    Span<byte> fields = stackalloc byte[12];
    var emptyMatch = -1L;
    for (var index = 0; index < this._entryCount; ++index) {
      var at = this._entriesStart + (long)index * VdfsLayout.EntrySize + VdfsLayout.EntryOffsetField;
      if (at + fields.Length > image.Length) break;

      image.Position = at;
      image.ReadExactly(fields);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
      if ((type & VdfsLayout.TypeDirectory) != 0) continue;
      if (BinaryPrimitives.ReadUInt32LittleEndian(fields) != (uint)oldOffset) continue;

      // In a stored volume an empty file may name the offset of the payload after it without
      // owning a byte there; the file that does own the run wins. In a zipped volume an empty
      // file owns its 12-byte stream header, and it is the only entry naming that offset.
      if (BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]) == 0) {
        if (emptyMatch < 0) emptyMatch = at;
        continue;
      }

      Repoint(image, at, newOffset);
      return;
    }

    if (emptyMatch >= 0) {
      Repoint(image, emptyMatch, newOffset);
      return;
    }

    throw new InvalidOperationException(
      $"VDFS: no entry points at offset {oldOffset:N0}, so '{fileName}' cannot be repointed.");
  }

  private static void Repoint(Stream image, long at, long newOffset) {
    Span<byte> pointer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(pointer, (uint)newOffset);
    image.Position = at;
    image.Write(pointer);
    image.Flush();
  }
}
