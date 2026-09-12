#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Mdf;

/// <summary>
/// Projects ISO-9660 logical extents back onto the physical MDF sector stream.
/// Cooked 2 048-byte MDFs can expose genuinely free runs. Raw sectors are kept
/// whole in the map because their sync/header/EDC/ECC bytes are live framing;
/// a sector whose payload is unused is therefore conservatively metadata-reserved.
/// Wipe uses the finer logical view directly and can still scrub that payload.
/// </summary>
public static class MdfLayoutMap {
  private const int LogicalSectorSize = MdfInPlaceModifier.Iso9660SectorSize;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream physical) {
    ArgumentNullException.ThrowIfNull(physical);
    if (!physical.CanRead || !physical.CanSeek) yield break;

    MdfCookedStream cooked;
    try {
      cooked = new MdfCookedStream(physical, leaveOpen: true);
    } catch {
      yield break;
    }

    using (cooked) {
      List<DefragBlockInfo> logical;
      try {
        cooked.Position = 0;
        logical = FileSystem.Iso.IsoExtentMap.Enumerate(cooked)
          .Where(e => e.Length > 0 && e.Offset >= 0)
          .ToList();
      } catch {
        yield break;
      }

      if (logical.Count == 0) yield break;

      var sectorCount = cooked.SectorCount;
      var runs = logical
        .Select(e => new SectorRun(
          Start: Math.Clamp(e.Offset / LogicalSectorSize, 0, sectorCount),
          End: Math.Clamp((e.Offset + e.Length + LogicalSectorSize - 1) / LogicalSectorSize, 0, sectorCount),
          e.Kind,
          e.FileName,
          e.Classification))
        .Where(r => r.End > r.Start)
        .ToArray();

      var boundaries = runs.SelectMany(r => new[] { r.Start, r.End })
        .Append(0)
        .Append(sectorCount)
        .Distinct()
        .Order()
        .ToArray();

      DefragBlockInfo? pending = null;
      for (var i = 0; i + 1 < boundaries.Length; ++i) {
        var start = boundaries[i];
        var end = boundaries[i + 1];
        if (end <= start) continue;

        var active = runs.Where(r => r.Start < end && r.End > start).ToArray();
        var strongest = active
          .OrderByDescending(r => Priority(r.Kind))
          .ThenBy(r => r.Start)
          .FirstOrDefault();

        var kind = strongest is null ? DefragBlockKind.Free : strongest.Kind;
        var name = strongest?.FileName;
        var classification = strongest?.Classification;

        if (cooked.Geometry.IsRaw && kind == DefragBlockKind.Free) {
          kind = DefragBlockKind.MetadataReserved;
          name = "raw-sector framing; ISO payload unused";
          classification = null;
        }

        var block = new DefragBlockInfo(
          checked(start * cooked.Geometry.SectorSize),
          checked((end - start) * cooked.Geometry.SectorSize),
          kind,
          name,
          classification);

        if (pending is not null &&
            pending.Offset + pending.Length == block.Offset &&
            pending.Kind == block.Kind &&
            pending.FileName == block.FileName &&
            pending.Classification == block.Classification) {
          pending = pending with { Length = pending.Length + block.Length };
          continue;
        }

        if (pending is not null) yield return pending;
        pending = block;
      }

      if (pending is not null) yield return pending;

      var covered = sectorCount * cooked.Geometry.SectorSize;
      if (covered < physical.Length)
        yield return new DefragBlockInfo(
          covered,
          physical.Length - covered,
          DefragBlockKind.MetadataReserved,
          "partial physical sector");
    }
  }

  private static int Priority(DefragBlockKind kind) => kind switch {
    DefragBlockKind.Bad => 4,
    DefragBlockKind.MetadataReserved => 3,
    DefragBlockKind.Used => 2,
    DefragBlockKind.InProgress => 1,
    _ => 0,
  };

  private sealed record SectorRun(
    long Start,
    long End,
    DefragBlockKind Kind,
    string? FileName,
    DefragBlockClass? Classification);
}
