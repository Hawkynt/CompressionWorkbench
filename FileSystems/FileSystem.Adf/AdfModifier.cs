#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Adf;

/// <summary>
/// Random-access in-place modifier for Amiga Disk File <c>.adf</c> images
/// (FFS — Fast File System). Reads and writes only the root block, the
/// bitmap, the optional hash-chain neighbour, the new file's header block,
/// and the new file's data blocks — never the whole image. This is the
/// O(touched bytes) path for the FFS layout the bundled
/// <see cref="AdfWriter"/> emits.
/// </summary>
/// <remarks>
/// Limitations matching the bundled writer:
/// <list type="bullet">
///   <item>FFS only — OFS images aren't supported by Add/Remove.</item>
///   <item>Single file header block, up to 72 data block pointers
///     (≤ 36 864 bytes per file). Extension blocks are not emitted.</item>
///   <item>Files always live at the volume root.</item>
/// </list>
/// </remarks>
public static class AdfModifier {

  private const int SectorSize = 512;
  private const int TotalSectors = 1760;
  private const int RootSector = 880;
  private const int BitmapSector = 881;
  private const int HashTableCount = 72;
  private const int HashTableOffset = 24;
  private const int DataBlockPtrsTop = 308;
  private const int FileSizeOffset = 324;
  private const int NameOffset = 432;
  private const int HashChainOffset = 496;
  private const int ParentOffset = 504;
  private const int SecTypeWordOff = 508;
  private const uint TypeHeader = 2;
  private const uint SecTypeRoot = 1;
  private const uint SecTypeFile = 0xFFFFFFFD;
  private const int MaxDataBlocksPerFile = HashTableCount; // 72

  /// <summary>
  /// Adds a file to an existing FFS image. Caller is responsible for
  /// ensuring the name does not already exist; use <see cref="RemoveFile"/>
  /// first for replace-by-name semantics.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    EnsureFfs(image);

    var truncated = name.Length > 30 ? name[^30..] : name;
    var dataBlockCount = (data.Length + SectorSize - 1) / SectorSize;
    if (dataBlockCount > MaxDataBlocksPerFile)
      throw new InvalidOperationException(
        $"ADF FFS: file too large ({data.Length} bytes); modifier supports up to {MaxDataBlocksPerFile} blocks (~36 KB).");

    var bitmapBuf = ReadSector(image, BitmapSector);
    var bitmap = DecodeBitmap(bitmapBuf);

    // Allocate header block + data blocks. Prefer sectors near the root for locality.
    var headerSector = AllocateSector(bitmap, RootSector + 2);
    if (headerSector < 0) throw new InvalidOperationException("ADF FFS: out of free sectors.");
    var dataBlocks = new int[dataBlockCount];
    for (var i = 0; i < dataBlockCount; i++) {
      dataBlocks[i] = AllocateSector(bitmap, headerSector + 1);
      if (dataBlocks[i] < 0) throw new InvalidOperationException("ADF FFS: out of free sectors.");
    }

    // Write data blocks (FFS: 512 bytes of raw payload).
    for (var i = 0; i < dataBlockCount; i++) {
      var buf = new byte[SectorSize];
      var off = i * SectorSize;
      var chunk = Math.Min(SectorSize, data.Length - off);
      Buffer.BlockCopy(data, off, buf, 0, chunk);
      WriteSector(image, dataBlocks[i], buf);
    }

    // Build file header block.
    var hdr = new byte[SectorSize];
    WriteUInt32BE(hdr, 0, TypeHeader);
    WriteUInt32BE(hdr, 4, (uint)headerSector);
    WriteUInt32BE(hdr, 8, (uint)dataBlockCount);
    for (var i = 0; i < dataBlockCount; i++)
      WriteUInt32BE(hdr, DataBlockPtrsTop - i * 4, (uint)dataBlocks[i]);
    WriteUInt32BE(hdr, FileSizeOffset, (uint)data.Length);
    WriteFilename(hdr, NameOffset, truncated);
    WriteUInt32BE(hdr, ParentOffset, RootSector);
    WriteUInt32BE(hdr, SecTypeWordOff, SecTypeFile);
    ComputeChecksum(hdr);
    WriteSector(image, headerSector, hdr);

    // Insert into the volume hash chain.
    var hash = HashName(truncated);
    var rootBuf = ReadSector(image, RootSector);
    var current = ReadUInt32BE(rootBuf, HashTableOffset + hash * 4);
    if (current == 0) {
      WriteUInt32BE(rootBuf, HashTableOffset + hash * 4, (uint)headerSector);
      ComputeChecksum(rootBuf);
      WriteSector(image, RootSector, rootBuf);
    } else {
      // Walk to chain tail and patch its hash_chain pointer.
      var prev = (int)current;
      while (true) {
        var prevBuf = ReadSector(image, prev);
        var next = ReadUInt32BE(prevBuf, HashChainOffset);
        if (next == 0) {
          WriteUInt32BE(prevBuf, HashChainOffset, (uint)headerSector);
          ComputeChecksum(prevBuf);
          WriteSector(image, prev, prevBuf);
          break;
        }
        prev = (int)next;
      }
    }

    // Persist bitmap (recomputes checksum).
    EncodeBitmap(bitmapBuf, bitmap);
    WriteSector(image, BitmapSector, bitmapBuf);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, the data blocks and header
  /// block are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    EnsureFfs(image);
    var truncated = name.Length > 30 ? name[^30..] : name;

    var bitmapBuf = ReadSector(image, BitmapSector);
    var bitmap = DecodeBitmap(bitmapBuf);
    var rootBuf = ReadSector(image, RootSector);

    var hash = HashName(truncated);
    var bucketOffset = HashTableOffset + hash * 4;
    var first = ReadUInt32BE(rootBuf, bucketOffset);
    if (first == 0) return false;

    // Walk the hash chain to find the matching entry.
    var prev = -1;            // -1 means "linked from root hash table"
    var current = (int)first;
    while (current != 0) {
      var headerBuf = ReadSector(image, current);
      var entryName = ReadFilename(headerBuf, NameOffset);
      var nextLink = (int)ReadUInt32BE(headerBuf, HashChainOffset);
      if (entryName == truncated) {
        // Collect data blocks and free everything.
        var dataBlocks = CollectDataBlocks(headerBuf);
        if (wipeData) {
          var zero = new byte[SectorSize];
          foreach (var db in dataBlocks)
            if (db is > 1 and < TotalSectors) WriteSector(image, db, zero);
          WriteSector(image, current, zero); // header block
        }
        foreach (var db in dataBlocks)
          if (db is > 1 and < TotalSectors) bitmap[db] = true;
        bitmap[current] = true;

        // Unlink from the chain.
        if (prev == -1) {
          WriteUInt32BE(rootBuf, bucketOffset, (uint)nextLink);
          ComputeChecksum(rootBuf);
          WriteSector(image, RootSector, rootBuf);
        } else {
          var prevBuf = ReadSector(image, prev);
          WriteUInt32BE(prevBuf, HashChainOffset, (uint)nextLink);
          ComputeChecksum(prevBuf);
          WriteSector(image, prev, prevBuf);
        }
        EncodeBitmap(bitmapBuf, bitmap);
        WriteSector(image, BitmapSector, bitmapBuf);
        return true;
      }
      prev = current;
      current = nextLink;
    }
    return false;
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static void EnsureFfs(Stream image) {
    var orig = image.Position;
    image.Position = 0;
    Span<byte> hdr = stackalloc byte[4];
    image.ReadExactly(hdr);
    image.Position = orig;
    if (hdr[0] != 'D' || hdr[1] != 'O' || hdr[2] != 'S')
      throw new InvalidDataException("ADF: missing DOS boot magic.");
    if ((hdr[3] & 1) == 0)
      throw new NotSupportedException("ADF: OFS images are not supported by AdfModifier (FFS only).");
  }

  private static List<int> CollectDataBlocks(byte[] headerBuf) {
    var result = new List<int>();
    var count = (int)ReadUInt32BE(headerBuf, 8);
    if (count <= 0 || count > MaxDataBlocksPerFile) {
      // Fall back: scan the pointer table for non-zero entries.
      for (var i = 0; i < HashTableCount; i++) {
        var p = (int)ReadUInt32BE(headerBuf, DataBlockPtrsTop - i * 4);
        if (p != 0) result.Add(p);
      }
      return result;
    }
    for (var i = 0; i < count; i++) {
      var p = (int)ReadUInt32BE(headerBuf, DataBlockPtrsTop - i * 4);
      if (p != 0) result.Add(p);
    }
    return result;
  }

  private static int AllocateSector(bool[] bitmap, int preferred) {
    for (var s = preferred; s < TotalSectors; s++) {
      if (bitmap[s]) { bitmap[s] = false; return s; }
    }
    for (var s = 2; s < preferred; s++) {
      if (bitmap[s]) { bitmap[s] = false; return s; }
    }
    return -1;
  }

  // Bitmap layout: 32-bit BE words at offsets 4, 8, ..., each covering 32 sectors.
  // Sectors 2..1759 map to bits in order. Bit SET = free.
  // Within each word, AdfWriter packs sequentially: bit (s-2) lives at word (s-2)/32,
  // byte 3 - ((s-2)%32)/8, bit (s-2)%8.

  private static bool[] DecodeBitmap(byte[] bitmapBuf) {
    var bits = new bool[TotalSectors];
    for (var s = 2; s < TotalSectors; s++) {
      var bitIndex = s - 2;
      var wordIndex = bitIndex / 32;
      var bitPos = bitIndex % 32;
      var byteOff = 4 + wordIndex * 4 + (3 - bitPos / 8);
      if (byteOff < bitmapBuf.Length && (bitmapBuf[byteOff] & (1 << (bitPos % 8))) != 0)
        bits[s] = true;
    }
    return bits;
  }

  private static void EncodeBitmap(byte[] bitmapBuf, bool[] bits) {
    // Zero everything except the checksum slot (will be recomputed).
    for (var i = 4; i < bitmapBuf.Length; i++) bitmapBuf[i] = 0;
    for (var s = 2; s < TotalSectors; s++) {
      if (!bits[s]) continue;
      var bitIndex = s - 2;
      var wordIndex = bitIndex / 32;
      var bitPos = bitIndex % 32;
      var byteOff = 4 + wordIndex * 4 + (3 - bitPos / 8);
      bitmapBuf[byteOff] |= (byte)(1 << (bitPos % 8));
    }
    // Bitmap checksum sits at offset 0.
    WriteUInt32BE(bitmapBuf, 0, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(bitmapBuf, i * 4);
    WriteUInt32BE(bitmapBuf, 0, (uint)(-(int)sum));
  }

  private static int HashName(string name) {
    var hash = (uint)name.Length;
    foreach (var c in name)
      hash = (hash * 13 + (byte)char.ToUpperInvariant(c)) & 0x7FF;
    return (int)(hash % HashTableCount);
  }

  private static void WriteFilename(byte[] buf, int offset, string name) {
    if (name.Length > 30) name = name[..30];
    buf[offset] = (byte)name.Length;
    var ascii = Encoding.ASCII.GetBytes(name);
    Buffer.BlockCopy(ascii, 0, buf, offset + 1, ascii.Length);
  }

  private static string ReadFilename(byte[] buf, int offset) {
    var len = buf[offset];
    if (len > 30) len = 30;
    return Encoding.ASCII.GetString(buf, offset + 1, len);
  }

  private static void ComputeChecksum(byte[] block) {
    // T_HEADER blocks (root and file headers) put the checksum at offset 20.
    WriteUInt32BE(block, 20, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(block, i * 4);
    WriteUInt32BE(block, 20, (uint)(-(int)sum));
  }

  // ── Sector I/O ────────────────────────────────────────────────────────

  private static byte[] ReadSector(Stream s, int sector) {
    var buf = new byte[SectorSize];
    s.Position = (long)sector * SectorSize;
    var read = 0;
    while (read < SectorSize) {
      var n = s.Read(buf, read, SectorSize - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteSector(Stream s, int sector, byte[] data) {
    s.Position = (long)sector * SectorSize;
    s.Write(data, 0, SectorSize);
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
