#pragma warning disable CS1591
using System.Buffers;
using System.Text;
using Compression.Registry;

namespace FileSystem.Cpm;

/// <summary>
/// In-place CP/M block mover. Moves 1024-byte allocation blocks within a CP/M
/// image and patches the directory entry's 16-byte block-pointer list so the
/// file remains reachable at its new location.
///
/// <para>CP/M has no separate allocation bitmap — block usage is implicit in
/// the union of directory-entry block lists. Updating the block pointers in the
/// affected directory entries is sufficient to redirect the file.</para>
/// </summary>
public sealed class CpmBlockMover : IFilesystemBlockMover {

  /// <summary>
  /// Byte offset where the file-data region begins, i.e. past the BIOS-reserved
  /// tracks AND the 2 KB directory area. The directory is metadata that the
  /// defrag planner must never overwrite. Block N still maps to <c>ReservedBytes
  /// + N*BlockSize</c>; we just exclude blocks 0 and 1 (the directory) from the
  /// data-region origin so the planner picks them as forbidden when finding
  /// target slots.
  /// </summary>
  public long DataOrigin => CpmLayout.ReservedBytes + CpmLayout.DirectoryBytes;

  /// <summary>Allocation block size in bytes (1024).</summary>
  public int BlockSize => CpmLayout.BlockSize;

  /// <summary>Converts a byte offset to a block index.</summary>
  public int OffsetToBlock(long offset) => (int)((offset - CpmLayout.ReservedBytes) / CpmLayout.BlockSize);

  /// <summary>Converts a block index to a byte offset.</summary>
  public long BlockToOffset(int block) => CpmLayout.ReservedBytes + block * (long)CpmLayout.BlockSize;

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
    var blockCount = (int)((length + CpmLayout.BlockSize - 1) / CpmLayout.BlockSize);

    // Build the old → new block mapping.
    var mapping = new Dictionary<int, int>(blockCount);
    for (var i = 0; i < blockCount; i++)
      mapping[oldBlock + i] = newBlock + i;

    // Split target filename into CP/M 8.3 format for matching.
    var (targetBase, targetExt) = SplitName(fileName);

    // Read the directory area and patch matching entries.
    var directory = new byte[CpmLayout.DirectoryBytes];
    image.Position = CpmLayout.ReservedBytes;
    image.ReadExactly(directory);

    var dirty = false;
    for (var i = 0; i < CpmLayout.DirectoryEntries; i++) {
      var off = i * CpmLayout.DirectoryEntrySize;
      var u = directory[off];
      if (u == CpmLayout.EmptyEntryUserCode || u > 0x1F) continue;

      var entryBase = Encoding.ASCII.GetString(directory, off + 1, 8).TrimEnd(' ');
      var entryExt = Encoding.ASCII.GetString(directory, off + 9, 3).TrimEnd(' ');

      // Strip CP/M attribute bits.
      entryBase = StripHighBits(entryBase);
      entryExt = StripHighBits(entryExt);

      if (!string.Equals(entryBase, targetBase, StringComparison.OrdinalIgnoreCase)) continue;
      if (!string.Equals(entryExt, targetExt, StringComparison.OrdinalIgnoreCase)) continue;

      // Patch the block pointers in this extent.
      for (var b = 0; b < 16; b++) {
        var blk = directory[off + 16 + b];
        if (blk == 0) continue;
        if (mapping.TryGetValue(blk, out var newBlk)) {
          directory[off + 16 + b] = (byte)newBlk;
          dirty = true;
        }
      }
    }

    if (dirty) {
      image.Position = CpmLayout.ReservedBytes;
      image.Write(directory);
      // Crash barrier: metadata commit durable before return.
      image.Flush();
    }
  }

  private static (string Base, string Ext) SplitName(string fullName) {
    var file = Path.GetFileName(fullName);
    var dot = file.LastIndexOf('.');
    if (dot < 0) return (file.Length > 8 ? file[..8] : file, "");
    var name = file[..dot];
    var ext = file[(dot + 1)..];
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return (name.ToUpperInvariant(), ext.ToUpperInvariant());
  }

  private static string StripHighBits(string raw) {
    var chars = new char[raw.Length];
    for (var i = 0; i < raw.Length; i++)
      chars[i] = (char)(raw[i] & 0x7F);
    return new string(chars).TrimEnd(' ');
  }
}
