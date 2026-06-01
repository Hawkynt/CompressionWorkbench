#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ti99;

/// <summary>
/// Walks a TI-99 sector-dump (.dsk) image and emits the on-disk layout:
/// sector 0 (VIB) + sector 1 (FDIR) + the FDR sector range as metadata-reserved,
/// each file's contiguous data run as Used. TIFiles single-file wrappers
/// expose the 128-byte header as metadata-reserved + the payload as Used.
/// </summary>
public static class Ti99ExtentMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var r = new Ti99Reader(image);
    if (!r.ValidVolume) yield break;
    if (r.IsTifilesWrapper) {
      yield return new DefragBlockInfo(0, Ti99Reader.TifilesHeaderSize, DefragBlockKind.MetadataReserved);
      // Single file occupies the rest.
      var entry = r.Entries.First();
      yield return new DefragBlockInfo(
        Ti99Reader.TifilesHeaderSize,
        Math.Max(0, image.Length - Ti99Reader.TifilesHeaderSize),
        DefragBlockKind.Used,
        entry.Name);
      yield break;
    }
    // Sector-dump: VIB + FDIR + per-FDR sectors are metadata.
    yield return new DefragBlockInfo(0, 2 * Ti99Reader.SectorSize, DefragBlockKind.MetadataReserved);
    // FDR sectors are 2..(2+FileCount-1) — represent them as one contiguous
    // metadata-reserved run.
    if (r.Entries.Count > 0)
      yield return new DefragBlockInfo(
        2L * Ti99Reader.SectorSize,
        (long)r.Entries.Count * Ti99Reader.SectorSize,
        DefragBlockKind.MetadataReserved);
    foreach (var e in r.Entries) {
      var startOff = (long)e.FirstSector * Ti99Reader.SectorSize;
      var len = (long)Math.Max(1, e.SectorCount) * Ti99Reader.SectorSize;
      if (startOff > 0 && startOff + len <= image.Length)
        yield return new DefragBlockInfo(startOff, len, DefragBlockKind.Used, e.Name);
    }
  }
}
