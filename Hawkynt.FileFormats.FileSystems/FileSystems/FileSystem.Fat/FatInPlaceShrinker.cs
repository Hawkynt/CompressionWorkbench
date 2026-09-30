#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Fat;

/// <summary>
/// Trims the unused tail off a FAT12/16/32 volume in place: the boot sector's total
/// sector count (and FAT32's backup boot sector and FSInfo free count) are rewritten
/// and the image is cut after the last cluster still in use. Nothing else moves —
/// every directory entry, attribute, timestamp, the label, the serial and the OEM
/// name stay exactly as they were.
/// </summary>
/// <remarks>
/// <para>How far a volume can shrink is bounded by its FAT type. FAT12 and FAT16 are
/// told apart by nothing but their cluster count (fewer than 4085 is FAT12, fewer
/// than 65525 FAT16), so a FAT16 volume cut below 4085 clusters would be read as
/// FAT12 by every driver and its FAT decoded as garbage; FAT32 volumes keep at least
/// 65525 clusters for the same reason. The FAT tables keep their length — a FAT
/// larger than its volume needs is valid, a type change is not.</para>
///
/// <para>Files are not moved here; pack them towards the start first (the in-place
/// defragmenter) so the free space sits at the tail where this can take it.</para>
/// </remarks>
public static class FatInPlaceShrinker {

  /// <summary>
  /// Shrinks <paramref name="image" /> to the smallest size that keeps every cluster
  /// in use and the FAT type unchanged. Returns the new length (the old one when
  /// nothing could be trimmed).
  /// </summary>
  public static long ShrinkToFit(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Shrink needs a readable, writable, seekable stream.", nameof(image));

    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);
    if (boot[510] != 0x55 || boot[511] != 0xAA)
      throw new InvalidDataException("FAT: no boot sector signature.");

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bps is 0 or > 4096) throw new InvalidDataException("FAT: invalid bytes per sector.");
    int spc = boot[13];
    if (spc == 0) throw new InvalidDataException("FAT: invalid sectors per cluster.");
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
    int fatCount = boot[16];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17));
    var total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19));
    var total = total16 != 0 ? total16 : BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32));
    var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22));
    var fatSize = fatSize16 != 0 ? fatSize16 : BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36));
    var rootDirSectors = (rootEntries * 32u + bps - 1) / bps;
    var firstDataSector = reserved + (uint)fatCount * fatSize + rootDirSectors;
    if (total <= firstDataSector) throw new InvalidDataException("FAT: volume has no data area.");
    var dataClusters = (total - firstDataSector) / (uint)spc;
    var fatType = fatSize16 == 0 ? 32 : dataClusters < 4085 ? 12 : 16;

    // The highest cluster any FAT entry still marks (in use, bad or reserved).
    var fat = new byte[fatSize * bps];
    image.Position = (long)reserved * bps;
    image.ReadExactly(fat);
    uint highest = 1;
    for (uint c = 2; c < dataClusters + 2; c++)
      if (ReadEntry(fat, c, fatType) != 0) highest = c;

    uint minimumForType = fatType switch { 12 => 1, 16 => 4085, _ => 65525 };
    var keep = Math.Max(Math.Max(highest - 1, minimumForType), 1u);
    if (keep >= dataClusters) return image.Length;

    var newTotal = firstDataSector + keep * (uint)spc;
    if (fatType != 32 && total16 != 0 && newTotal < 65536) {
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), (ushort)newTotal);
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), 0);
    } else {
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), newTotal);
    }
    image.Position = 0;
    image.Write(boot);

    if (fatType == 32) {
      // The backup boot sector must agree with the primary.
      var backup = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50));
      if (backup != 0 && backup != 0xFFFF && (long)(backup + 1) * bps <= image.Length) {
        image.Position = (long)backup * bps;
        image.Write(boot);
      }
      // FSInfo: recount what is free inside the new cluster range.
      var fsInfo = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(48));
      if (fsInfo != 0 && fsInfo != 0xFFFF && (long)(fsInfo + 1) * bps <= image.Length) {
        var info = new byte[512];
        image.Position = (long)fsInfo * bps;
        image.ReadExactly(info);
        if (BinaryPrimitives.ReadUInt32LittleEndian(info) == 0x41615252) {
          uint free = 0;
          for (uint c = 2; c < keep + 2; c++)
            if (ReadEntry(fat, c, fatType) == 0) free++;
          BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), free);
          var next = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(492));
          if (next != 0xFFFFFFFF && next >= keep + 2) BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), 0xFFFFFFFF);
          image.Position = (long)fsInfo * bps;
          image.Write(info);
        }
      }
    }

    var newLength = (long)newTotal * bps;
    image.SetLength(newLength);
    image.Flush();
    return newLength;
  }

  private static uint ReadEntry(byte[] fat, uint cluster, int fatType) {
    switch (fatType) {
      case 12: {
        var off = (int)(cluster + cluster / 2);
        if (off + 1 >= fat.Length) return 0;
        var raw = (ushort)(fat[off] | (fat[off + 1] << 8));
        return (cluster & 1) != 0 ? (uint)(raw >> 4) : (uint)(raw & 0x0FFF);
      }
      case 16: {
        var off = (int)(cluster * 2);
        return off + 2 <= fat.Length ? BinaryPrimitives.ReadUInt16LittleEndian(fat.AsSpan(off)) : 0u;
      }
      default: {
        var off = (int)(cluster * 4);
        return off + 4 <= fat.Length ? BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(off)) & 0x0FFFFFFF : 0u;
      }
    }
  }
}
