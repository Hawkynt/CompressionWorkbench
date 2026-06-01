#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Trsdos;

/// <summary>
/// Enumerates the on-disk byte layout of a TRSDOS / LDOS disk image.
/// Track 17 (GAT + HIT + directory records) is emitted as
/// <see cref="DefragBlockKind.MetadataReserved"/>; every file's sector
/// run is one <see cref="DefragBlockKind.Used"/> extent; unattributed
/// sectors are left for the caller to fill as
/// <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class TrsdosExtentMap {

  private const int SectorSize = TrsdosReader.SectorSize;
  private const int DirectoryTrack = TrsdosReader.DirectoryTrack;

  /// <summary>Walks the image and yields one extent per file + the directory track.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    var imageLen = data.Length;

    using var reader = new TrsdosReader(new MemoryStream(data));
    if (!reader.ValidVolume) yield break;

    // Directory track region.
    var trackBytes = reader.SectorsPerTrack * SectorSize;
    var trackOff = reader.DirectoryTrackOffset;
    if (trackOff + trackBytes <= imageLen)
      yield return new DefragBlockInfo(trackOff, trackBytes, DefragBlockKind.MetadataReserved, "directory_track");

    foreach (var e in reader.Entries) {
      var off = (long)e.FirstSector * SectorSize;
      var len = (long)e.SectorCount * SectorSize;
      if (off < 0 || off >= imageLen) continue;
      if (off + len > imageLen) len = imageLen - off;
      if (len <= 0) continue;
      yield return new DefragBlockInfo(off, len, DefragBlockKind.Used, e.Name);
    }
  }
}
