#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.CramFs;

/// <summary>
/// Moves a file's compressed blocks inside a CramFS image and repoints both its
/// inode and the block pointer table that travels with it.
/// </summary>
/// <remarks>
/// <para>A CramFS file is a table of block end offsets followed by the
/// compressed blocks that table ends. The inode says where the table starts;
/// the entries in it are absolute byte offsets into the image. Moving the pair
/// is the copy, the inode's offset field, and the same delta added to every
/// entry — the first block's start is implied by where the table ends, so it
/// follows on its own.</para>
///
/// <para>The inode is found by the offset it still names rather than by the
/// file's path, so two entries pointing at the same table cannot send the wrong
/// one somewhere.</para>
///
/// <para>The superblock carries a checksum over the whole image. Restamping it
/// per move would cost a pass over the image each time, so the caller does it
/// once after every move has run.</para>
/// </remarks>
public sealed class CramFsBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the size-and-gid word inside an inode.</summary>
  private const int InodeOffsetWord = 8;

  private long _firstDataByte;

  /// <summary>Finds where the first file's table starts.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    var reader = new CramFsReader(image);
    var first = image.Length;
    foreach (var entry in reader.Entries) {
      if (!entry.IsRegularFile) continue;
      var (offset, length) = reader.DataExtent(entry);
      if (length > 0) first = Math.Min(first, offset);
    }
    this._firstDataByte = first;
  }

  /// <summary>
  /// Four bytes. An inode records a file's start divided by four, so that is
  /// the grid a table can begin on.
  /// </summary>
  public int BlockSize => 4;

  /// <summary>First byte a file may occupy: past the superblock and the inodes.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
    if (oldOffset == newOffset) return;

    if (newOffset % 4 != 0)
      throw new NotSupportedException(
        $"CramFS: {newOffset} is not on a four-byte boundary, which is all an inode can name.");
    if (newOffset / 4 > (1 << 26) - 1)
      throw new NotSupportedException(
        $"CramFS: {newOffset} is past what an inode's 26-bit offset field holds.");

    int inodeOffset;
    int blocks;
    {
      image.Position = 0;
      var reader = new CramFsReader(image);
      inodeOffset = -1;
      blocks = 0;
      foreach (var entry in reader.Entries) {
        if (!entry.IsRegularFile || entry.DataOffset != oldOffset) continue;
        inodeOffset = entry.InodeOffset;
        blocks = CramFsReader.BlockCount(entry);
        break;
      }
    }

    if (inodeOffset < 0)
      throw new InvalidOperationException(
        $"CramFS: no inode names {oldOffset}, so '{fileName}' cannot be repointed.");

    // The inode's third word packs the name length into its low six bits and
    // the start offset, divided by four, into the rest.
    Span<byte> word = stackalloc byte[4];
    image.Position = inodeOffset + InodeOffsetWord;
    image.ReadExactly(word);
    var packed = BinaryPrimitives.ReadUInt32LittleEndian(word);
    BinaryPrimitives.WriteUInt32LittleEndian(word, (packed & 0x3Fu) | ((uint)(newOffset / 4) << 6));
    image.Position = inodeOffset + InodeOffsetWord;
    image.Write(word);

    // Every table entry is an absolute offset into the image, so the whole
    // table shifts by however far the file moved.
    var delta = newOffset - oldOffset;
    for (var i = 0; i < blocks; ++i) {
      var at = newOffset + (long)i * 4;
      if (at + 4 > image.Length) break;
      image.Position = at;
      image.ReadExactly(word);
      var end = BinaryPrimitives.ReadUInt32LittleEndian(word);
      BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)(end + delta));
      image.Position = at;
      image.Write(word);
    }

    image.Flush();
  }

  /// <summary>
  /// Recomputes the checksum the superblock carries over the whole image, with
  /// the checksum field itself read as zero — which is how it was computed.
  /// </summary>
  public static void RestampChecksum(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    var bytes = new byte[image.Length];
    image.ReadExactly(bytes);
    Array.Clear(bytes, CramFsConstants.CrcOffset, 4);

    var crc = new Compression.Core.Checksums.Crc32();
    crc.Update(bytes);

    Span<byte> stamp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(stamp, crc.Value);
    image.Position = CramFsConstants.CrcOffset;
    image.Write(stamp);
    image.Flush();
  }
}
