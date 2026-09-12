#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.AdvFs;

/// <summary>
/// Moves a file's bytes inside an AdvFS storage domain and repoints its row in
/// the file table.
/// </summary>
/// <remarks>
/// <para>A file here is one run of bytes and the file table in RBMT page 0
/// records where it starts as an absolute byte offset, so relocating a file is
/// the copy plus one eight-byte write. Nothing else records the position: the
/// domain's block counts describe how large the volume is, not which of it is
/// taken.</para>
///
/// <para>Offsets are byte-exact rather than page-aligned, which is what the
/// reader resolves a file by — so the mover reports an allocation unit of one
/// and lets the planner pack a domain without leaving page-sized gaps.</para>
/// </remarks>
public sealed class AdvFsBlockMover : IFilesystemBlockMover {

  /// <summary>Byte offset of the file table's eyecatcher inside the image.</summary>
  private long _fileTableOffset = -1;

  /// <summary>Rows the table holds.</summary>
  private int _fileCount;

  /// <summary>First byte a file may occupy.</summary>
  private long _firstDataByte = AdvFsWriter.DataAreaOffset;

  /// <summary>Locates the file table and the start of the data area.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var page = new byte[AdvFsWriter.PageSize];
    if (image.Length < AdvFsWriter.RbmtPageOffset + page.Length)
      throw new InvalidDataException("AdvFs: the image is too short to hold an RBMT page.");
    image.Position = AdvFsWriter.RbmtPageOffset;
    image.ReadExactly(page);

    // Search the page for the eyecatcher rather than computing where the
    // header prefix ends: the row that has to be rewritten is the one the
    // reader will read, and finding it the same way the reader does keeps the
    // two from drifting apart if the prefix ever gains a field.
    var eyecatcher = AdvFsWriter.FileTableEyecatcher;
    var at = -1;
    for (var i = 0; i + eyecatcher.Length <= page.Length; ++i) {
      if (!page.AsSpan(i, eyecatcher.Length).SequenceEqual(eyecatcher)) continue;
      at = i;
      break;
    }
    if (at < 0)
      throw new NotSupportedException(
        "AdvFs: this domain carries no file table, so there is nothing that records where a " +
        "file starts.");

    this._fileTableOffset = AdvFsWriter.RbmtPageOffset + at;
    this._fileCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(
      page.AsSpan(at + eyecatcher.Length));

    image.Position = 0;
    var reader = new AdvFsReader(image);
    var first = long.MaxValue;
    foreach (var entry in reader.FileTableEntries)
      if (entry.Offset >= 0 && entry.Size > 0)
        first = Math.Min(first, entry.Offset);
    this._firstDataByte = first == long.MaxValue ? AdvFsWriter.DataAreaOffset : first;
  }

  /// <summary>
  /// One byte. A file table row holds an absolute byte offset, so nothing about
  /// the format asks a file to start on a boundary.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a file may occupy: past the RBMT page.</summary>
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
    if (this._fileTableOffset < 0) this.Init(image);
    if (oldOffset == newOffset) return;

    // Walk the rows the way the reader does — offset, length, name — and
    // rewrite the one that still names the file's old home. Matching on the
    // offset rather than the name keeps two rows with the same name from
    // sending the wrong file somewhere.
    var cursor = this._fileTableOffset + AdvFsWriter.FileTableEyecatcher.Length + 4;
    var row = new byte[18];
    for (var i = 0; i < this._fileCount; ++i) {
      if (cursor + row.Length > image.Length) break;
      image.Position = cursor;
      image.ReadExactly(row);
      var offset = BinaryPrimitives.ReadInt64LittleEndian(row);
      var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(16));

      if (offset == oldOffset) {
        Span<byte> replacement = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(replacement, newOffset);
        image.Position = cursor;
        image.Write(replacement);
        image.Flush();
        return;
      }

      cursor += row.Length + nameLength;
    }

    throw new InvalidOperationException(
      $"AdvFs: no file table row names offset {oldOffset}, so '{fileName}' cannot be repointed.");
  }
}
