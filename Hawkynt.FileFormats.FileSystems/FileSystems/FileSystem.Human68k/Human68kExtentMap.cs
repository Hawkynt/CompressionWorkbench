#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Human68k;

/// <summary>
/// Enumerates the on-disk byte layout of a Human68k disk image: boot
/// sector, FAT(s), and root directory are emitted as
/// <see cref="DefragBlockKind.MetadataReserved"/>; every file's first
/// cluster + size is collapsed into one <see cref="DefragBlockKind.Used"/>
/// extent (Human68k's reader currently surfaces only the first contiguous
/// run); unattributed sectors are left for the caller to fill as
/// <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class Human68kExtentMap {

  /// <summary>Walks the image and yields the metadata + file extents.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    var imageLen = data.Length;
    if (imageLen < 512) yield break;

    using var reader = new Human68kReader(new MemoryStream(data));
    if (!reader.ValidVolume) yield break;

    // Reader knows the BPB (sectors are 512 in the reader's constant).
    const int sectorSize = Human68kReader.SectorSize;

    // Boot sector.
    yield return new DefragBlockInfo(0, sectorSize, DefragBlockKind.MetadataReserved, "boot");

    // FATs.
    var fatStart = reader.ReservedSectors * (long)sectorSize;
    var fatLen = reader.FatCount * reader.SectorsPerFat * (long)sectorSize;
    if (fatStart + fatLen <= imageLen)
      yield return new DefragBlockInfo(fatStart, fatLen, DefragBlockKind.MetadataReserved, "fat");

    // Root directory.
    var rootStart = fatStart + fatLen;
    var rootBytes = (reader.RootEntries * 32 + sectorSize - 1) / sectorSize * sectorSize;
    if (rootStart + rootBytes <= imageLen)
      yield return new DefragBlockInfo(rootStart, rootBytes, DefragBlockKind.MetadataReserved, "root_directory");

    // File extents (one contiguous run per file from firstCluster).
    var bytesPerCluster = reader.SectorsPerCluster * sectorSize;
    var dataStartSector = reader.ReservedSectors + reader.FatCount * reader.SectorsPerFat
      + (reader.RootEntries * 32 + sectorSize - 1) / sectorSize;
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (e.FirstCluster < 2) continue;
      var offset = (e.FirstCluster - 2) * (long)bytesPerCluster + dataStartSector * (long)sectorSize;
      var clusters = Math.Max(1L, (e.Size + bytesPerCluster - 1) / bytesPerCluster);
      var len = clusters * bytesPerCluster;
      if (offset < 0 || offset >= imageLen) continue;
      if (offset + len > imageLen) len = imageLen - offset;
      if (len <= 0) continue;
      yield return new DefragBlockInfo(offset, len, DefragBlockKind.Used, e.Name);
    }
  }
}
