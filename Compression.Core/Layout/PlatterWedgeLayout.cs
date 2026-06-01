namespace Compression.Core.Layout;

/// <summary>
/// One filled arc-segment (annular wedge) of a circular platter — the
/// minimal primitive a defrag-style visualiser draws per LBA range. Angles
/// are measured CW from the 12-o'clock position (matching how a real
/// platter is read), radii are normalised 0..1 from the spindle outward.
/// </summary>
/// <param name="StartAngle">Start angle in radians (CW from 12-o'clock).</param>
/// <param name="SweepAngle">Angular extent in radians, always positive.</param>
/// <param name="InnerRadius">Inner edge radius (0..1, 0 = spindle).</param>
/// <param name="OuterRadius">Outer edge radius (0..1, 1 = rim).</param>
public readonly record struct PlatterWedge(double StartAngle, double SweepAngle, double InnerRadius, double OuterRadius) {

  /// <summary>True iff this wedge spans the entire ring (used to short-circuit
  /// arc-segment construction in favour of a plain annulus).</summary>
  public bool IsFullRing => this.SweepAngle >= 2.0 * System.Math.PI - 1e-6;
}

/// <summary>
/// 3-D extension of <see cref="PlatterWedge"/> for the cylinder-stack view:
/// the wedge plus the head index it sits on. The 2-D wedge is interpreted
/// on the platter for that head.
/// </summary>
/// <param name="Head">0-based head index (which platter surface).</param>
/// <param name="Wedge">2-D wedge geometry for this head.</param>
public readonly record struct StackedPlatterWedge(int Head, PlatterWedge Wedge);

/// <summary>
/// Pure-geometry helpers that turn a byte range into the platter wedge a
/// real defrag tool would draw for it. Independent of any UI framework so
/// the layout maths can be unit-tested without WPF/GDI. The wedge concept:
/// each tile renders as a filled annular segment whose angular extent
/// matches the tile's sector range within a track and whose radial extent
/// matches the cylinder range, exactly like UltimateDefrag's doughnut view.
/// </summary>
public static class PlatterWedgeLayout {

  /// <summary>
  /// Computes the 2-D annular wedge for a byte range on a single platter
  /// (head-agnostic view: the byte range may straddle heads, the wedge
  /// summarises the full radial sweep across cylinders).
  /// </summary>
  /// <remarks>
  /// Angles are CW from the 12-o'clock position so the sector spiral reads
  /// like an actual platter. The wedge spans:
  /// <list type="bullet">
  /// <item>Angle: from the start LBA's sector through the end LBA's sector
  /// in the SAME track; if the byte range crosses tracks, the angular sweep
  /// clamps to the full 2π (the wedge becomes a full ring at that radius
  /// band).</item>
  /// <item>Radius: from the OUTER edge of the start cylinder's track in
  /// to the INNER edge of the end cylinder's track. Cylinder 0 sits at the
  /// rim (radius 1), the last cylinder at <paramref name="innerRadiusFraction"/>.</item>
  /// </list>
  /// </remarks>
  /// <param name="g">Media geometry to drive the projection.</param>
  /// <param name="startByte">Inclusive start of the byte range.</param>
  /// <param name="endByteExclusive">Exclusive end of the byte range.</param>
  /// <param name="innerRadiusFraction">Spindle-hole radius as a fraction (0..1).</param>
  /// <returns>A wedge in normalised platter coordinates.</returns>
  public static PlatterWedge ComputeWedge(MediaGeometry g, long startByte, long endByteExclusive, double innerRadiusFraction = 0.25) {
    if (endByteExclusive <= startByte) endByteExclusive = startByte + 1;
    var startLba = g.LbaOfByte(startByte);
    var endLba = g.LbaOfByte(endByteExclusive - 1);
    if (endLba < startLba) endLba = startLba;

    var (startCyl, _, startSec) = g.ChsFromLba(startLba);
    var (endCyl, _, endSec) = g.ChsFromLba(endLba);

    var spt = System.Math.Max(1, g.SectorsPerTrack);
    var rOuter = RadiusForCylinder(g, startCyl, innerRadiusFraction);
    var rInnerEdge = RadiusForCylinder(g, endCyl, innerRadiusFraction);
    // Bottom of the cylinder band: subtract one cylinder-thickness so the
    // wedge fills the full track band, not just the inner-edge line.
    var cylThickness = CylinderThickness(g, innerRadiusFraction);
    var rInner = System.Math.Max(0.0, rInnerEdge - cylThickness);
    if (rInner > rOuter) (rInner, rOuter) = (rOuter, rInner);

    double startAngle;
    double sweep;
    if (startCyl == endCyl && endLba - startLba < spt) {
      startAngle = 2.0 * System.Math.PI * startSec / spt;
      var endAngleExclusive = 2.0 * System.Math.PI * (endSec + 1) / spt;
      sweep = endAngleExclusive - startAngle;
      if (sweep <= 0) sweep += 2.0 * System.Math.PI;
    } else {
      // Multi-track / wraparound: collapse to a full ring at this radial band.
      startAngle = 0.0;
      sweep = 2.0 * System.Math.PI;
    }

    return new PlatterWedge(startAngle, sweep, rInner, rOuter);
  }

  /// <summary>
  /// 3-D variant: computes the head index plus the 2-D wedge on that head's
  /// platter. For ranges that straddle multiple heads, the wedge is returned
  /// for the START head — callers wanting per-head accuracy should slice
  /// their input range by head boundaries first.
  /// </summary>
  /// <param name="g">Media geometry to drive the projection.</param>
  /// <param name="startByte">Inclusive start of the byte range.</param>
  /// <param name="endByteExclusive">Exclusive end of the byte range.</param>
  /// <param name="innerRadiusFraction">Spindle-hole radius as a fraction (0..1).</param>
  /// <returns>The stacked wedge with head index plus 2-D wedge.</returns>
  public static StackedPlatterWedge ComputeStackedWedge(MediaGeometry g, long startByte, long endByteExclusive, double innerRadiusFraction = 0.25) {
    var startLba = g.LbaOfByte(startByte);
    var (_, head, _) = g.ChsFromLba(startLba);
    var wedge = ComputeWedge(g, startByte, endByteExclusive, innerRadiusFraction);
    return new StackedPlatterWedge(head, wedge);
  }

  /// <summary>Per-cylinder track thickness in normalised radius units.</summary>
  /// <param name="g">Media geometry to drive the projection.</param>
  /// <param name="innerRadiusFraction">Spindle-hole radius as a fraction (0..1).</param>
  /// <returns>Thickness of one cylinder's track band.</returns>
  public static double CylinderThickness(MediaGeometry g, double innerRadiusFraction = 0.25) {
    var cyls = System.Math.Max(1, g.Cylinders);
    return (1.0 - innerRadiusFraction) / cyls;
  }

  /// <summary>Outer-edge radius of a track at the given cylinder.</summary>
  /// <param name="g">Media geometry to drive the projection.</param>
  /// <param name="cylinder">Cylinder index.</param>
  /// <param name="innerRadiusFraction">Spindle-hole radius as a fraction (0..1).</param>
  /// <returns>Normalised radius (0..1).</returns>
  public static double RadiusForCylinder(MediaGeometry g, long cylinder, double innerRadiusFraction = 0.25) {
    var cyls = g.Cylinders;
    if (cyls <= 0) return 1.0;
    if (cylinder < 0) cylinder = 0;
    if (cylinder >= cyls) cylinder = cyls - 1;
    // Cylinder 0 -> radius 1 (outer rim). Last cylinder -> inner edge.
    var thickness = CylinderThickness(g, innerRadiusFraction);
    return 1.0 - cylinder * thickness;
  }
}
