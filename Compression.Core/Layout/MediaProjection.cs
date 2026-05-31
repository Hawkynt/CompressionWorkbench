namespace Compression.Core.Layout;

/// <summary>
/// How a block-map view projects a logical byte offset / LBA onto screen space.
/// </summary>
public enum BlockMapView {
  /// <summary>Linear tile grid (row-major by byte offset) — the classic view.</summary>
  LinearBlocks,
  /// <summary>Linear by logical block address (one cell per sector run, LBA order).</summary>
  LinearLba,
  /// <summary>2-D circular platter: angle = sector-in-track, radius = cylinder
  /// (outer rim is cylinder 0), as data sits on a spinning disk.</summary>
  CircularPlatter,
  /// <summary>3-D cylinder stack: angle = sector, radius = cylinder, height = head
  /// (platter surface), to show the head/cylinder layering of the medium.</summary>
  CylinderStack,
}

/// <summary>
/// Cylinder/head/sector (CHS) geometry of a storage medium and the conversions
/// between a flat logical block address (LBA) and its physical (cylinder, head,
/// sector) coordinate. Used by the block-map visualiser to place each block
/// where it would physically reside on the medium.
/// </summary>
/// <remarks>Sectors and heads are 0-based here (geometry math), unlike the
/// 1-based sector numbering of classic INT 13h CHS addressing.</remarks>
public readonly record struct MediaGeometry(int BytesPerSector, int SectorsPerTrack, int Heads, long TotalSectors) {

  /// <summary>Standard hard-disk/USB geometry (63 sectors/track, 255 heads) for an
  /// image of <paramref name="totalBytes"/>, rounded up to whole sectors.</summary>
  public static MediaGeometry Standard(long totalBytes, int bytesPerSector = 512)
    => new(bytesPerSector, 63, 255, totalBytes <= 0 ? 0 : (totalBytes + bytesPerSector - 1) / bytesPerSector);

  /// <summary>Geometry from an explicit BPB-style description.</summary>
  public static MediaGeometry FromGeometry(long totalBytes, int bytesPerSector, int sectorsPerTrack, int heads)
    => new(bytesPerSector, Math.Max(1, sectorsPerTrack), Math.Max(1, heads),
        totalBytes <= 0 ? 0 : (totalBytes + bytesPerSector - 1) / bytesPerSector);

  /// <summary>Sectors per cylinder = sectorsPerTrack × heads.</summary>
  public long SectorsPerCylinder => (long)Math.Max(1, this.SectorsPerTrack) * Math.Max(1, this.Heads);

  /// <summary>Number of cylinders needed to hold <see cref="TotalSectors"/>.</summary>
  public long Cylinders {
    get {
      var spc = this.SectorsPerCylinder;
      return spc <= 0 ? 0 : (this.TotalSectors + spc - 1) / spc;
    }
  }

  /// <summary>Splits a logical block address into (cylinder, head, sector).</summary>
  public (long Cylinder, int Head, int Sector) ChsFromLba(long lba) {
    if (lba < 0) lba = 0;
    var spt = Math.Max(1, this.SectorsPerTrack);
    var spc = this.SectorsPerCylinder;
    var cylinder = lba / spc;
    var withinCylinder = lba % spc;
    var head = (int)(withinCylinder / spt);
    var sector = (int)(withinCylinder % spt);
    return (cylinder, head, sector);
  }

  /// <summary>Reassembles a logical block address from (cylinder, head, sector).</summary>
  public long LbaFromChs(long cylinder, int head, int sector)
    => (cylinder * Math.Max(1, this.Heads) + head) * Math.Max(1, this.SectorsPerTrack) + sector;

  /// <summary>The LBA a byte offset falls in.</summary>
  public long LbaOfByte(long byteOffset) => byteOffset < 0 ? 0 : byteOffset / Math.Max(1, this.BytesPerSector);
}

/// <summary>
/// Pure projections of an LBA onto normalised coordinates for each
/// <see cref="BlockMapView"/>. The UI maps these unit values onto its canvas.
/// </summary>
public static class MediaProjection {

  /// <summary>Fraction (0..1) of the way through the medium for a linear/LBA view.</summary>
  public static double LinearFraction(MediaGeometry g, long lba)
    => g.TotalSectors <= 0 ? 0.0 : Math.Clamp((double)lba / g.TotalSectors, 0.0, 1.0);

  /// <summary>2-D platter coordinate: angle in radians [0, 2π) from the sector
  /// position within its track, and radius (0..1) from the cylinder — cylinder 0
  /// is the outer rim (radius 1), the innermost cylinder maps to
  /// <paramref name="innerRadiusFraction"/>.</summary>
  public static (double Angle, double Radius) CircularPlatter(MediaGeometry g, long lba, double innerRadiusFraction = 0.25) {
    var (cyl, _, sector) = g.ChsFromLba(lba);
    var angle = TrackAngle(g, sector);
    var radius = 1.0 - CylinderFraction(g, cyl) * (1.0 - innerRadiusFraction);
    return (angle, radius);
  }

  /// <summary>3-D cylinder-stack coordinate: angle (sector), radius (cylinder,
  /// 0..1 inner→outer), and height z (head/platter surface, 0..1).</summary>
  public static (double Angle, double Radius, double Z) CylinderStack(MediaGeometry g, long lba) {
    var (cyl, head, sector) = g.ChsFromLba(lba);
    var angle = TrackAngle(g, sector);
    var radius = CylinderFraction(g, cyl);
    var z = g.Heads <= 1 ? 0.0 : (double)head / (g.Heads - 1);
    return (angle, radius, z);
  }

  private static double TrackAngle(MediaGeometry g, int sector) {
    var spt = Math.Max(1, g.SectorsPerTrack);
    return 2.0 * Math.PI * sector / spt;
  }

  private static double CylinderFraction(MediaGeometry g, long cyl) {
    var cyls = g.Cylinders;
    return cyls <= 1 ? 0.0 : Math.Clamp((double)cyl / (cyls - 1), 0.0, 1.0);
  }
}
