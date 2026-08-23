#pragma warning disable CS1591
using System.Buffers;
using System.Text;
using Compression.Registry;

namespace FileSystem.Adf;

/// <summary>
/// In-place Amiga FFS block mover. Moves sector-aligned extents within an
/// ADF image and patches the file header block's data-block pointer table,
/// the root hash table (if the header block itself moved), and the bitmap.
/// </summary>
public sealed class AdfBlockMover : IFilesystemBlockMover {

  private const int SectorSize = 512;
  private const int TotalSectors = 1760;
  private const int RootSector = 880;
  private const int BitmapSector = 881;
  private const int HashTableCount = 72;
  private const int HashTableOffset = 24;
  private const int DataBlockPtrsTop = 308;
  private const int NameOffset = 432;
  private const int HashChainOffset = 496;
  /// <summary>Where a block names the first of its file-extension blocks.</summary>
  private const int ExtBlockOffset = 504;
  private const int SecTypeWordOff = 508;

  /// <summary>
  /// Follows a file's extension blocks, repointing the chain itself and the data
  /// blocks each one names.
  /// </summary>
  private static void PatchExtensionChain(byte[] data, int ownerOff, IReadOnlyDictionary<int, int> remap) {
    var linkOff = ownerOff + ExtBlockOffset;
    var seen = new HashSet<int>();
    while (true) {
      var ext = (int)ReadUInt32BE(data, linkOff);
      if (ext == 0) return;

      // The extension block may itself have moved, in which case whatever names
      // it has to say so.
      if (remap.TryGetValue(ext, out var moved)) {
        WriteUInt32BE(data, linkOff, (uint)moved);
        ComputeChecksum(data, linkOff - ExtBlockOffset);
        ext = moved;
      }

      if (!seen.Add(ext)) return;                       // a chain that loops names nothing more
      var extOff = ext * SectorSize;
      if (extOff < 0 || extOff + SectorSize > data.Length) return;

      var count = (int)ReadUInt32BE(data, extOff + 8);
      if (count <= 0 || count > HashTableCount) count = HashTableCount;
      for (var i = 0; i < count; i++) {
        var ptrOff = extOff + DataBlockPtrsTop - i * 4;
        var ptr = (int)ReadUInt32BE(data, ptrOff);
        if (ptr != 0 && remap.TryGetValue(ptr, out var newPtr))
          WriteUInt32BE(data, ptrOff, (uint)newPtr);
      }
      ComputeChecksum(data, extOff);
      linkOff = extOff + ExtBlockOffset;
    }
  }

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
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    var blockCount = (int)((length + SectorSize - 1) / SectorSize);
    var oldBlocks = new List<int>(blockCount);
    var newBlocks = new List<int>(blockCount);
    for (var i = 0; i < blockCount; i++) {
      oldBlocks.Add((int)((oldOffset + (long)i * SectorSize) / SectorSize));
      newBlocks.Add((int)((newOffset + (long)i * SectorSize) / SectorSize));
    }

    var remap = new Dictionary<int, int>(blockCount);
    for (var i = 0; i < blockCount; i++)
      remap[oldBlocks[i]] = newBlocks[i];

    // 1. Walk root hash table to find file header blocks and patch data-block pointers.
    var rootOff = RootSector * SectorSize;
    for (var h = 0; h < HashTableCount; h++) {
      var headerBlock = (int)ReadUInt32BE(data, rootOff + HashTableOffset + h * 4);
      if (headerBlock == 0) continue;

      // If the header block itself was remapped, patch the hash table.
      if (remap.TryGetValue(headerBlock, out var newHeader)) {
        WriteUInt32BE(data, rootOff + HashTableOffset + h * 4, (uint)newHeader);
        headerBlock = newHeader;
      }

      // Walk the hash chain for this bucket.
      var current = headerBlock;
      var seen = new HashSet<int>();
      while (current != 0 && seen.Add(current)) {
        var hdrOff = current * SectorSize;
        if (hdrOff + SectorSize > data.Length) break;

        var entryName = ReadFilename(data, hdrOff + NameOffset);
        if (string.Equals(entryName, fileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "*", StringComparison.Ordinal)) {
          // Patch data-block pointers in this header.
          var dbCount = (int)ReadUInt32BE(data, hdrOff + 8);
          if (dbCount <= 0 || dbCount > HashTableCount) dbCount = HashTableCount;
          for (var di = 0; di < dbCount; di++) {
            var ptrOff = hdrOff + DataBlockPtrsTop - di * 4;
            var ptr = (int)ReadUInt32BE(data, ptrOff);
            if (ptr != 0 && remap.TryGetValue(ptr, out var newPtr))
              WriteUInt32BE(data, ptrOff, (uint)newPtr);
          }
          // Recompute header checksum.
          ComputeChecksum(data, hdrOff);

          // A header names at most 72 data blocks; the rest are named by a chain
          // of extension blocks, and this used to walk none of them. Every block
          // of a file past the first 36 864 bytes kept naming where it had been.
          PatchExtensionChain(data, hdrOff, remap);
        }

        // Patch hash-chain link if it was remapped.
        var nextLink = (int)ReadUInt32BE(data, hdrOff + HashChainOffset);
        if (nextLink != 0 && remap.TryGetValue(nextLink, out var newLink)) {
          WriteUInt32BE(data, hdrOff + HashChainOffset, (uint)newLink);
          ComputeChecksum(data, hdrOff);
          nextLink = newLink;
        }
        current = nextLink;
      }
    }

    // Recompute root checksum (we may have patched hash table entries).
    ComputeChecksum(data, rootOff);

    // 2. Update bitmap: free old blocks, allocate new blocks.
    foreach (var b in oldBlocks) SetBitmapBit(data, b, free: true);
    foreach (var b in newBlocks) SetBitmapBit(data, b, free: false);
    // Recompute bitmap checksum.
    RecomputeBitmapChecksum(data);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── Bitmap helpers ────────────────────────────────────────────────

  private static void SetBitmapBit(byte[] data, int sector, bool free) {
    if (sector < 2 || sector >= TotalSectors) return;
    var bitIndex = sector - 2;
    var wordIndex = bitIndex / 32;
    var bitPos = bitIndex % 32;
    var byteOff = BitmapSector * SectorSize + 4 + wordIndex * 4 + (3 - bitPos / 8);
    if (byteOff >= data.Length) return;
    if (free)
      data[byteOff] |= (byte)(1 << (bitPos % 8));
    else
      data[byteOff] &= (byte)~(1 << (bitPos % 8));
  }

  private static void RecomputeBitmapChecksum(byte[] data) {
    var off = BitmapSector * SectorSize;
    WriteUInt32BE(data, off, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(data, off + i * 4);
    WriteUInt32BE(data, off, (uint)(-(int)sum));
  }

  // ── Block helpers ─────────────────────────────────────────────────

  private static void ComputeChecksum(byte[] data, int blockOff) {
    WriteUInt32BE(data, blockOff + 20, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(data, blockOff + i * 4);
    WriteUInt32BE(data, blockOff + 20, (uint)(-(int)sum));
  }

  private static string ReadFilename(byte[] data, int offset) {
    var len = data[offset];
    if (len > 30) len = 30;
    return Encoding.ASCII.GetString(data, offset + 1, len);
  }

  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

  private static void WriteUInt32BE(byte[] data, int offset, uint value) {
    data[offset] = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }
}
