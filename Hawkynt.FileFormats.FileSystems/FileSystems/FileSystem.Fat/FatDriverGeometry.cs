#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Fat;

internal readonly record struct FatDriverGeometry(
  int BytesPerSector,
  int SectorsPerCluster,
  int ReservedSectors,
  int FatCount,
  long TotalSectors,
  int FatSize,
  long FirstDataSector,
  long TotalDataClusters,
  int FatType,
  int RootCluster) {

  public int ClusterSize => checked(BytesPerSector * SectorsPerCluster);
  public long DataLength => checked(TotalSectors * BytesPerSector);

  public static FatDriverGeometry Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("FAT driver probing requires a readable, seekable stream.", nameof(image));
    if (image.Length < 512) throw new InvalidDataException("FAT image is shorter than one boot sector.");

    Span<byte> boot = stackalloc byte[512];
    var original = image.Position;
    try {
      image.Position = 0;
      image.ReadExactly(boot);
    } finally {
      image.Position = original;
    }

    if (boot[0] is not (0xEB or 0xE9 or 0x00))
      throw new InvalidDataException("FAT boot jump is invalid.");
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..13]);
    if (bps is not (512 or 1024 or 2048 or 4096))
      throw new InvalidDataException($"FAT bytes-per-sector value {bps} is unsupported.");
    var spc = boot[13];
    if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 128)
      throw new InvalidDataException($"FAT sectors-per-cluster value {spc} is invalid.");
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..16]);
    if (reserved == 0) throw new InvalidDataException("FAT reserved-sector count is zero.");
    var fatCount = boot[16];
    if (fatCount is 0 or > 4) throw new InvalidDataException($"FAT copy count {fatCount} is invalid.");
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..19]);
    var total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..21]);
    var total = total16 != 0 ? total16 : BinaryPrimitives.ReadUInt32LittleEndian(boot[32..36]);
    if (total == 0) throw new InvalidDataException("FAT total sector count is zero.");
    var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..24]);
    var fatSize = fat16 != 0 ? fat16 : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot[36..40]));
    if (fatSize <= 0) throw new InvalidDataException("FAT table size is zero.");

    var rootSectors = ((long)rootEntries * 32 + bps - 1) / bps;
    var firstData = checked((long)reserved + (long)fatCount * fatSize + rootSectors);
    if (firstData >= total) throw new InvalidDataException("FAT data region starts outside the volume.");
    var dataClusters = (total - firstData) / spc;
    var fatType = fat16 == 0 ? 32 : dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
    var rootCluster = fatType == 32 ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot[44..48])) : 0;
    if (fatType == 32 && rootCluster < 2) throw new InvalidDataException("FAT32 root cluster is invalid.");
    var dataLength = checked((long)total * bps);
    if (image.Length < dataLength)
      throw new InvalidDataException($"FAT volume declares {dataLength:N0} bytes but image has only {image.Length:N0}.");

    return new FatDriverGeometry(
      bps, spc, reserved, fatCount, total, fatSize,
      firstData, dataClusters, fatType, rootCluster);
  }

  public long ClusterOffset(int cluster) {
    if (cluster < 2 || cluster >= TotalDataClusters + 2)
      throw new InvalidDataException($"FAT cluster {cluster} lies outside the data-cluster range.");
    return checked((FirstDataSector + (long)(cluster - 2) * SectorsPerCluster) * BytesPerSector);
  }

  public IReadOnlyList<int> ReadChain(Stream image, int startCluster, string owner) {
    if (startCluster < 2) return [];
    var result = new List<int>();
    var seen = new HashSet<int>();
    var cluster = startCluster;
    while (true) {
      if (cluster < 2 || cluster >= TotalDataClusters + 2)
        throw new InvalidDataException($"FAT chain for '{owner}' references out-of-range cluster {cluster}.");
      if (!seen.Add(cluster))
        throw new InvalidDataException($"FAT chain for '{owner}' contains a loop at cluster {cluster}.");
      result.Add(cluster);

      var next = ReadFatEntry(image, 0, cluster);
      for (var copy = 1; copy < FatCount; ++copy) {
        var mirror = ReadFatEntry(image, copy, cluster);
        if (mirror != next)
          throw new InvalidDataException(
            $"FAT copies disagree at cluster {cluster}: primary=0x{next:X}, copy {copy}=0x{mirror:X}.");
      }
      if (IsEndOfChain(next)) return result;
      if (IsBadOrReserved(next))
        throw new InvalidDataException($"FAT chain for '{owner}' terminates in reserved/bad value 0x{next:X}.");
      if (next == 0)
        throw new InvalidDataException($"FAT chain for '{owner}' reaches a free cluster after {cluster}.");
      cluster = next;
    }
  }

  private int ReadFatEntry(Stream image, int fatCopy, int cluster) {
    var fatStart = checked(((long)ReservedSectors + (long)fatCopy * FatSize) * BytesPerSector);
    Span<byte> bytes = stackalloc byte[4];
    var position = FatType switch {
      12 => fatStart + cluster + cluster / 2,
      16 => fatStart + (long)cluster * 2,
      _ => fatStart + (long)cluster * 4,
    };
    var needed = FatType is 12 or 16 ? 2 : 4;
    var original = image.Position;
    try {
      image.Position = position;
      image.ReadExactly(bytes[..needed]);
    } finally {
      image.Position = original;
    }

    if (FatType == 12) {
      var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
      return (cluster & 1) == 0 ? raw & 0x0FFF : raw >> 4;
    }
    if (FatType == 16) return BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
    return checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF));
  }

  private bool IsEndOfChain(int value) => FatType switch {
    12 => value >= 0xFF8,
    16 => value >= 0xFFF8,
    _ => value >= 0x0FFFFFF8,
  };

  private bool IsBadOrReserved(int value) => FatType switch {
    12 => value is >= 0xFF0 and < 0xFF8,
    16 => value is >= 0xFFF0 and < 0xFFF8,
    _ => value is >= 0x0FFFFFF0 and < 0x0FFFFFF8,
  };
}
