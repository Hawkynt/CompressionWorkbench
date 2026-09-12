#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Pc98;

/// <summary>
/// Enumerates the on-disk byte layout of an NEC PC-98 disk image: the
/// IPL block, the FAT(s), and the root directory are emitted as
/// <see cref="DefragBlockKind.MetadataReserved"/>; every file's cluster
/// run is one <see cref="DefragBlockKind.Used"/> extent; unattributed
/// sectors are left for the caller to fill as <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class Pc98ExtentMap {

  /// <summary>Walks the image and yields the metadata + file extents.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    var imageLen = data.Length;
    if (imageLen < 512) yield break;

    using var reader = new Pc98Reader(new MemoryStream(data));
    if (!reader.ValidVolume) yield break;

    const int sectorSize = Pc98Reader.SectorSize;

    // IPL block (sector 0).
    yield return new DefragBlockInfo(0, sectorSize, DefragBlockKind.MetadataReserved, "ipl");

    // Reserved + FAT(s). FAT starts at offset (1 + ReservedSectors) * sectorSize
    // (the +1 accounts for the IPL block).
    var fatStart = (1 + reader.ReservedSectors) * (long)sectorSize;
    var fatLen = reader.FatCount * reader.SectorsPerFat * (long)sectorSize;
    if (fatStart + fatLen <= imageLen)
      yield return new DefragBlockInfo(fatStart, fatLen, DefragBlockKind.MetadataReserved, "fat");

    // Root directory.
    var rootStart = fatStart + fatLen;
    var rootBytes = (reader.RootEntries * 32 + sectorSize - 1) / sectorSize * sectorSize;
    if (rootStart + rootBytes <= imageLen)
      yield return new DefragBlockInfo(rootStart, rootBytes, DefragBlockKind.MetadataReserved, "root_directory");

    // File extents.
    var bytesPerCluster = reader.SectorsPerCluster * sectorSize;
    var dataStartSector = reader.ReservedSectors + reader.FatCount * reader.SectorsPerFat;
    var dataStartOffset = dataStartSector * (long)sectorSize + rootBytes + sectorSize; // +1 for IPL
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (e.FirstCluster < 2) continue;
      var offset = (e.FirstCluster - 2) * (long)bytesPerCluster + dataStartOffset;
      var clusters = Math.Max(1L, (e.Size + bytesPerCluster - 1) / bytesPerCluster);
      var len = clusters * bytesPerCluster;
      if (offset < 0 || offset >= imageLen) continue;
      if (offset + len > imageLen) len = imageLen - offset;
      if (len <= 0) continue;
      yield return new DefragBlockInfo(offset, len, DefragBlockKind.Used, e.Name);
    }
  }
}
