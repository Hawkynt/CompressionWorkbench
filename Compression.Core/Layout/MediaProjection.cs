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

  /// <summary>
  /// Picks plausible real-world CHS geometry for an image of
  /// <paramref name="totalBytes"/>. Returns a known standard format
  /// (5¼" / 3½" / Zip / Jaz / typical HDD) when the size matches one;
  /// otherwise scales heads + cylinders proportionally to keep the cylinder
  /// count in [128, 4096] and sectors-per-track at 63. Use this instead of
  /// <see cref="Standard(long, int)"/> when you want the visualization to
  /// reflect realistic geometry rather than the 255-head/63-spt HDD default.
  /// </summary>
  public static MediaGeometry Heuristic(long totalBytes, int bytesPerSector = 512) {
    if (totalBytes <= 0) return new(bytesPerSector, 1, 1, 0);
    var total = (totalBytes + bytesPerSector - 1) / bytesPerSector;

    // Common floppies + retro removable media. Picked so that
    // Cylinders × Heads × SectorsPerTrack covers `total` exactly (or with
    // padding) and matches the medium's documented physical CHS.
    if (total <= 360)    return new(bytesPerSector, 9, 1, total);   // 180 KB SSSD 5¼
    if (total <= 720)    return new(bytesPerSector, 9, 2, total);   // 360 KB DSSD 5¼
    if (total <= 1440)   return new(bytesPerSector, 9, 2, total);   // 720 KB DSDD 3½
    if (total <= 2400)   return new(bytesPerSector, 15, 2, total);  // 1.2 MB DSHD 5¼
    if (total <= 2880)   return new(bytesPerSector, 18, 2, total);  // 1.44 MB DSHD 3½
    if (total <= 5760)   return new(bytesPerSector, 36, 2, total);  // 2.88 MB ED 3½
    if (total <= 21437)  return new(bytesPerSector, 44, 2, total);  // 11 MB Floptical
    if (total <= 196608) return new(bytesPerSector, 32, 64, total); // 100 MB Zip — 96 cylinders × 64 heads × 32 spt
    if (total <= 491520) return new(bytesPerSector, 32, 64, total); // 250 MB Zip — 240 cyl × 64 head × 32 spt
    if (total <= 1968128) return new(bytesPerSector, 32, 64, total); // 1 GB Jaz — 962 cyl × 64 head × 32 spt

    // HDD-style: pick heads so cylinder count lands in [128, 4096], keep
    // sectors-per-track = 63 (BIOS-style classic geometry).
    const int spt = 63;
    var heads = 1;
    while (total / (heads * (long)spt) > 4096 && heads < 256) heads *= 2;
    return new(bytesPerSector, spt, heads, total);
  }

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
