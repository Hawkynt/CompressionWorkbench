#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Adf;

/// <summary>
/// Creates Amiga Disk File (.adf) images using the Fast File System (FFS).
/// Produces standard DD disk images of exactly 901,120 bytes (1760 sectors of 512 bytes).
/// </summary>
public sealed class AdfWriter {
  private const int SectorSize = 512;
  private const int TotalSectors = 1760;
  private const int DiskSize = TotalSectors * SectorSize;
  private const int RootSector = 880;
  private const int BitmapSector = 881;
  private const int HashTableCount = 72;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to the disk image being built.
  /// </summary>
  /// <param name="name">The filename (up to 30 ASCII characters).</param>
  /// <param name="data">The file content.</param>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds and returns the complete 901,120-byte ADF disk image.
  /// </summary>
  /// <param name="diskName">The volume name written to the root block (up to 30 characters).</param>
  /// <returns>A byte array of exactly 901,120 bytes representing the disk image.</returns>
  public byte[] Build(string diskName = "DISK") {
    var disk = new byte[DiskSize];
    var used = new bool[TotalSectors];

    // Boot block: "DOS\1" (FFS)
    disk[0] = (byte)'D';
    disk[1] = (byte)'O';
    disk[2] = (byte)'S';
    disk[3] = 1; // FFS
    used[0] = true;
    used[1] = true;

    // Reserve root and bitmap
    used[RootSector] = true;
    used[BitmapSector] = true;

    // Build root hash table
    var hashTable = new uint[HashTableCount];

    foreach (var (name, data) in _files) {
      var hash = HashName(name);

      // Allocate file header block
      var headerSector = AllocateSector(used, RootSector + 2);
      if (headerSector < 0) break;

      // Allocate data blocks
      var dataBlockCount = (data.Length + SectorSize - 1) / SectorSize;
      if (dataBlockCount == 0) dataBlockCount = 0; // empty file is ok
      var dataBlocks = new int[dataBlockCount];
      for (var i = 0; i < dataBlockCount; i++) {
        dataBlocks[i] = AllocateSector(used, headerSector + 1);
        if (dataBlocks[i] < 0) break;
      }

      // Write data blocks (FFS: pure data, no header)
      var remaining = data.Length;
      for (var i = 0; i < dataBlockCount; i++) {
        var off = dataBlocks[i] * SectorSize;
        var chunk = Math.Min(SectorSize, remaining);
        data.AsSpan(i * SectorSize, chunk).CopyTo(disk.AsSpan(off));
        remaining -= chunk;
      }

      // Write file header block
      var hdrOff = headerSector * SectorSize;
      WriteUInt32BE(disk, hdrOff, 2); // T_HEADER
      WriteUInt32BE(disk, hdrOff + 4, (uint)headerSector); // own key
      WriteUInt32BE(disk, hdrOff + 8, (uint)dataBlockCount); // high_seq (data block count)
      // Data block pointers at offsets 308, 304, 300, ... (reverse order)
      for (var i = 0; i < dataBlockCount && i < HashTableCount; i++)
        WriteUInt32BE(disk, hdrOff + 308 - i * 4, (uint)dataBlocks[i]);
      WriteUInt32BE(disk, hdrOff + 324, (uint)data.Length); // file size
      WriteFilename(disk, hdrOff + 432, name); // filename
      WriteUInt32BE(disk, hdrOff + 508, 0xFFFFFFFD); // sec_type = ST_FILE
      WriteUInt32BE(disk, hdrOff + 504, (uint)RootSector); // parent

      // Hash chain: insert into hash table
      if (hashTable[hash] == 0) {
        hashTable[hash] = (uint)headerSector;
      } else {
        // Chain: follow existing entries to end
        var current = (int)hashTable[hash];
        while (true) {
          var chainOff = current * SectorSize + 496;
          var next = ReadUInt32BE(disk, chainOff);
          if (next == 0) {
            WriteUInt32BE(disk, chainOff, (uint)headerSector);
            break;
          }
          current = (int)next;
        }
      }

      // Compute header checksum
      ComputeChecksum(disk, hdrOff);
    }

    // Write root block
    var rootOff = RootSector * SectorSize;
    WriteUInt32BE(disk, rootOff, 2); // T_HEADER
    WriteUInt32BE(disk, rootOff + 4, (uint)RootSector); // own key
    // Hash table at offset 24
    for (var i = 0; i < HashTableCount; i++)
      WriteUInt32BE(disk, rootOff + 24 + i * 4, hashTable[i]);
    // Bitmap flag at offset 312: -1 = valid
    WriteUInt32BE(disk, rootOff + 312, 0xFFFFFFFF);
    // Bitmap pointer at offset 316
    WriteUInt32BE(disk, rootOff + 316, (uint)BitmapSector);
    WriteFilename(disk, rootOff + 432, diskName);
    WriteUInt32BE(disk, rootOff + 508, 1); // sec_type = ST_ROOT
    ComputeChecksum(disk, rootOff);

    // Write bitmap block
    WriteBitmap(disk, used);

    return disk;
  }

  private static int AllocateSector(bool[] used, int preferred) {
    // Try near preferred first
    for (var s = preferred; s < TotalSectors; s++) {
      if (!used[s]) { used[s] = true; return s; }
    }
    for (var s = 2; s < preferred; s++) {
      if (!used[s]) { used[s] = true; return s; }
    }
    return -1;
  }

  private static int HashName(string name) {
    var hash = (uint)name.Length;
    foreach (var c in name)
      hash = (hash * 13 + (byte)char.ToUpperInvariant(c)) & 0x7FF;
    return (int)(hash % HashTableCount);
  }

  private static void WriteFilename(byte[] disk, int offset, string name) {
    if (name.Length > 30) name = name[..30];
    disk[offset] = (byte)name.Length;
    Encoding.ASCII.GetBytes(name).CopyTo(disk, offset + 1);
  }

  private static void WriteUInt32BE(byte[] data, int offset, uint value) {
    data[offset] = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }

  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

  private static void ComputeChecksum(byte[] disk, int blockOffset) {
    // Checksum is at offset 20 within the block
    WriteUInt32BE(disk, blockOffset + 20, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(disk, blockOffset + i * 4);
    WriteUInt32BE(disk, blockOffset + 20, (uint)(-(int)sum));
  }

  private static void WriteBitmap(byte[] disk, bool[] used) {
    var off = BitmapSector * SectorSize;
    // Bitmap starts at offset 4 (offset 0 is checksum)
    // Each bit represents a sector: 1=free, 0=used
    // Sectors 2 through 1759 mapped to bits
    for (var s = 2; s < TotalSectors; s++) {
      var bitIndex = s - 2;
      var wordIndex = bitIndex / 32;
      var bitPos = bitIndex % 32;
      if (!used[s])
        disk[off + 4 + wordIndex * 4 + (3 - bitPos / 8)] |= (byte)(1 << (bitPos % 8));
    }

    // Compute bitmap checksum (same algorithm, checksum at offset 0)
    WriteUInt32BE(disk, off, 0);
    uint sum = 0;
    for (var i = 0; i < SectorSize / 4; i++)
      sum += ReadUInt32BE(disk, off + i * 4);
    WriteUInt32BE(disk, off, (uint)(-(int)sum));
  }
}
