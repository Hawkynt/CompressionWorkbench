#pragma warning disable CS1591
using Compression.Registry;
using static FileSystem.CpcDsk.CpcDskAmsdos;

namespace FileSystem.CpcDsk;

/// <summary>
/// Describes what occupies each stretch of a CPC DSK image: the container's own
/// headers, the AMSDOS directory, each file's blocks, and the blocks nothing has
/// been given.
/// </summary>
/// <remarks>
/// <para>What the filesystem allocates is a kilobyte block, but what the image
/// stores is a 512-byte sector, and a track of nine sectors is four and a half
/// blocks — so a block's two sectors are not always next to each other: every
/// other one has a 256-byte Track-Info block in the middle of it.</para>
///
/// <para>The map is therefore drawn in sectors and the runs coalesced only where
/// the bytes really do run on. Describing a straddling block as one span claimed
/// the first half and left the second unclaimed, and free-space wiping zeroes
/// whatever the map does not claim — so eight of fourteen files came back with
/// holes in them from a verb that is supposed to touch nothing that is in use.</para>
/// </remarks>
public static class CpcDskExtentMap {

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    var reader = new CpcDskReader(image);
    var geometry = reader.Layout;
    if (geometry == null) yield break;

    // The container's own furniture, which belongs to no file and never moves.
    yield return new DefragBlockInfo(0, DiskInfoSize,
      DefragBlockKind.MetadataReserved, FileName: "CPC DSK disk info header");

    for (var t = 0; t < geometry.Tracks; ++t)
      for (var s = 0; s < geometry.Sides; ++s) {
        var at = geometry.TrackOffsets[t * geometry.Sides + s];
        if (at < 0) continue;
        yield return new DefragBlockInfo(at, TrackInfoSize,
          DefragBlockKind.MetadataReserved, FileName: $"CPC DSK Track-Info T{t:D2}S{s}");
      }

    // Give every sector to whatever holds it, block by block.
    var totalSectors = geometry.Tracks * geometry.SectorsPerCylinder;
    var owner = new string?[totalSectors];
    var kind = new DefragBlockKind[totalSectors];
    for (var i = 0; i < totalSectors; ++i) kind[i] = DefragBlockKind.Free;

    void Claim(int block, string? name, DefragBlockKind blockKind) {
      foreach (var sector in geometry.SectorsOfBlock(block)) {
        if (sector < 0 || sector >= totalSectors) continue;
        owner[sector] = name;
        kind[sector] = blockKind;
      }
    }

    for (var block = 0; block < DirectoryBlocks; ++block)
      Claim(block, "AMSDOS directory", DefragBlockKind.MetadataReserved);

    foreach (var entry in reader.Entries)
      foreach (var block in entry.Blocks)
        Claim(block, entry.Name, DefragBlockKind.Used);

    // Coalesce, but only across sectors whose bytes actually run on: a run stops
    // at a track boundary because a Track-Info block sits in the way.
    var runStart = -1;
    long runOffset = 0;
    var runLength = 0L;
    string? runOwner = null;
    var runKind = DefragBlockKind.Free;

    for (var sector = 0; sector <= totalSectors; ++sector) {
      var offset = sector < totalSectors ? geometry.SectorOffset(sector) : -1;
      var continues = runStart >= 0
        && sector < totalSectors
        && offset == runOffset + runLength
        && kind[sector] == runKind
        && string.Equals(owner[sector], runOwner, StringComparison.Ordinal);

      if (continues) {
        runLength += geometry.SectorBytes;
        continue;
      }

      if (runStart >= 0)
        yield return new DefragBlockInfo(runOffset, runLength, runKind, FileName: runOwner);

      if (sector >= totalSectors || offset < 0) { runStart = -1; continue; }

      runStart = sector;
      runOffset = offset;
      runLength = geometry.SectorBytes;
      runOwner = owner[sector];
      runKind = kind[sector];
    }
  }
}
