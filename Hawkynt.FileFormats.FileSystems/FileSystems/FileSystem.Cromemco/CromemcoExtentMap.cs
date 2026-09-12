#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Cromemco;

/// <summary>
/// Enumerates the on-disk byte layout of a Cromemco RDOS image:
/// the boot block (sector 0) and directory area (sectors 2..17) are
/// emitted as <see cref="DefragBlockKind.MetadataReserved"/>, each
/// file is one contiguous <see cref="DefragBlockKind.Used"/> run, and
/// any sector not covered is left for the caller to fill as
/// <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class CromemcoExtentMap {

  private const int SectorSize = CromemcoReader.SectorSize;
  private const int DirectoryOffset = CromemcoReader.DirectoryOffset;
  private const int DirectoryBytes = CromemcoWriter.DirectorySectors * SectorSize;
  private const int FirstDataSector = CromemcoWriter.FirstDataSector;

  /// <summary>Walks the image and yields one extent per file plus the metadata blocks.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    var imageLen = data.Length;

    // Boot block (sector 0).
    yield return new DefragBlockInfo(0, SectorSize, DefragBlockKind.MetadataReserved, "boot");

    // Directory area (sectors 2..17 = 16 sectors).
    if (DirectoryOffset + DirectoryBytes <= imageLen)
      yield return new DefragBlockInfo(DirectoryOffset, DirectoryBytes, DefragBlockKind.MetadataReserved, "directory");

    // File extents.
    using var reader = new CromemcoReader(new MemoryStream(data));
    if (!reader.ValidVolume) yield break;
    foreach (var e in reader.Entries) {
      var off = (long)e.StartBlock * SectorSize;
      var len = (long)e.BlockCount * SectorSize;
      if (off < 0 || off >= imageLen) continue;
      if (off + len > imageLen) len = imageLen - off;
      if (len <= 0) continue;
      yield return new DefragBlockInfo(off, len, DefragBlockKind.Used, e.Name);
    }
  }
}
