#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.DoubleSpace;

/// <summary>
/// Builds a DoubleSpace/DriveSpace Compressed Volume File (CVF).
/// Creates MDBPB header, MDFAT, BitFAT, FAT directory, and compressed file data.
/// </summary>
public sealed class DoubleSpaceWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _driveSpace;

  /// <summary>If true, write "MSDSP6.2" (DriveSpace); otherwise "MSDSP6.0" (DoubleSpace).</summary>
  public bool DriveSpace { get => _driveSpace; set => _driveSpace = value; }

  /// <summary>Adds a file to the CVF. Name should be 8.3 format.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>Builds the CVF image.</summary>
  public byte[] Build() {
    const int bytesPerSector = 512;
    const int sectorsPerCluster = 1;
    const int reservedSectors = 1;
    const int fatCount = 2;
    const int rootEntryCount = 224;
    const int fatSize = 9;

    // Layout: boot | FAT1 | FAT2 | RootDir | MDFAT | CompressedData
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;

    // First, compress all files and compute sector requirements
    var compressedFiles = new List<(string Name, byte[] Original, List<byte[]> CompressedBlocks)>();
    var totalDataSectors = 0;

    foreach (var (name, data) in _files) {
      var blocks = new List<byte[]>();
      var pos = 0;
      while (pos < data.Length) {
        var chunkSize = Math.Min(bytesPerSector, data.Length - pos);
        var chunk = data.AsSpan(pos, chunkSize);
        var compressed = DsCompression.Compress(chunk);
        blocks.Add(compressed);
        pos += chunkSize;
      }

      if (blocks.Count == 0) blocks.Add(DsCompression.Compress(ReadOnlySpan<byte>.Empty));

      // Each compressed block needs ceil(block.Length / bytesPerSector) physical sectors
      foreach (var block in blocks)
        totalDataSectors += (block.Length + bytesPerSector - 1) / bytesPerSector;

      compressedFiles.Add((name, data, blocks));
    }

    // MDFAT: one entry per logical data sector (we map each file's sectors)
    var logicalSectorCount = 0;
    foreach (var (_, orig, _) in compressedFiles)
      logicalSectorCount += Math.Max(1, (orig.Length + bytesPerSector - 1) / bytesPerSector);

    var mdfatSectors = (logicalSectorCount * 4 + bytesPerSector - 1) / bytesPerSector;
    var mdfatStartSector = firstDataSector;
    var dataStartSector = mdfatStartSector + mdfatSectors;

    var totalSectors = dataStartSector + totalDataSectors + 16; // padding
    if (totalSectors < 2880) totalSectors = 2880;

    var disk = new byte[totalSectors * bytesPerSector];

    // --- MDBPB (boot sector) ---
    disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90;
    var sig = _driveSpace ? "MSDSP6.2" : "MSDSP6.0";
    Encoding.ASCII.GetBytes(sig).CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), (ushort)bytesPerSector);
    disk[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), (ushort)reservedSectors);
    disk[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), (ushort)rootEntryCount);
    if (totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);
    disk[21] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255);

    // CVF-specific: MDFAT start and data start at offsets 44 and 48
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), (uint)mdfatStartSector);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(48), (uint)dataStartSector);

    disk[510] = 0x55; disk[511] = 0xAA;

    // --- FAT ---
    var fatOffset = reservedSectors * bytesPerSector;
    disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF; disk[fatOffset + 2] = 0xFF;

    // --- Root directory + MDFAT + Data ---
    var rootDirOffset = (reservedSectors + fatCount * fatSize) * bytesPerSector;
    var dirEntryPos = rootDirOffset;
    var nextCluster = 2;
    var mdfatOffset = mdfatStartSector * bytesPerSector;
    var physicalSectorPos = dataStartSector;
    var logicalSectorIdx = 0;

    foreach (var (name, original, blocks) in compressedFiles) {
      // Write directory entry
      var dotIdx = name.LastIndexOf('.');
      var baseName = dotIdx >= 0 ? name[..dotIdx] : name;
      var ext = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
      var shortBase = baseName.ToUpperInvariant().PadRight(8)[..8];
      var shortExt = ext.ToUpperInvariant().PadRight(3)[..3];

      Encoding.ASCII.GetBytes(shortBase).CopyTo(disk, dirEntryPos);
      Encoding.ASCII.GetBytes(shortExt).CopyTo(disk, dirEntryPos + 8);
      disk[dirEntryPos + 11] = 0x20; // Archive
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirEntryPos + 26), (ushort)nextCluster);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(dirEntryPos + 28), original.Length);
      dirEntryPos += 32;

      // Write compressed data and MDFAT entries
      var fileSectors = Math.Max(1, (original.Length + bytesPerSector - 1) / bytesPerSector);
      for (var s = 0; s < fileSectors && s < blocks.Count; s++) {
        var block = blocks[s];
        var physSectorsNeeded = (block.Length + bytesPerSector - 1) / bytesPerSector;

        // Write compressed block to physical sectors
        var physOffset = physicalSectorPos * bytesPerSector;
        if (physOffset + block.Length <= disk.Length)
          block.CopyTo(disk, physOffset);

        // Check if this block is actually compressed (bit 15 of header set)
        var isCompressed = block.Length >= 2 && (block[1] & 0x80) != 0;

        // MDFAT entry: bits 0-20 = physical sector, bits 21-24 = compressed sector count,
        // bits 25-27 = flags (1=uncompressed, 2=compressed)
        var mdfatEntry = (physicalSectorPos & 0x1FFFFF)
          | ((physSectorsNeeded & 0xF) << 21)
          | ((isCompressed ? 2 : 1) << 25);

        var mdfatEntryOffset = mdfatOffset + logicalSectorIdx * 4;
        if (mdfatEntryOffset + 4 <= disk.Length)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset), (uint)mdfatEntry);

        physicalSectorPos += physSectorsNeeded;
        logicalSectorIdx++;
      }

      // FAT chain for this file
      var clustersNeeded = Math.Max(1, (original.Length + bytesPerSector - 1) / bytesPerSector);
      for (var c = 0; c < clustersNeeded; c++) {
        var cluster = nextCluster + c;
        var nextVal = (c + 1 < clustersNeeded) ? cluster + 1 : 0xFFF;
        WriteFat12Entry(disk, fatOffset, cluster, nextVal);
      }

      nextCluster += clustersNeeded;
    }

    // Copy FAT1 to FAT2
    Array.Copy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  private static void WriteFat12Entry(byte[] disk, int fatOffset, int cluster, int value) {
    var bytePos = fatOffset + cluster * 3 / 2;
    if (bytePos + 1 >= disk.Length) return;
    if ((cluster & 1) == 0) {
      disk[bytePos] = (byte)(value & 0xFF);
      disk[bytePos + 1] = (byte)((disk[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
    } else {
      disk[bytePos] = (byte)((disk[bytePos] & 0x0F) | ((value << 4) & 0xF0));
      disk[bytePos + 1] = (byte)((value >> 4) & 0xFF);
    }
  }
}
