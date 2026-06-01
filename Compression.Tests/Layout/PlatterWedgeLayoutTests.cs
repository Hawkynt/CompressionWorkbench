using Compression.Core.Layout;

namespace Compression.Tests.Layout;

/// <summary>
/// Wedge-geometry helpers for the platter / cylinder-stack block-map views.
/// Validates that a tile's byte range produces a correctly-sized annular
/// wedge so the UltimateDefrag-style doughnut and the 3-D platter stack
/// render with the right angular and radial extents.
/// </summary>
[TestFixture]
public class PlatterWedgeLayoutTests {

  [Test, Category("Spec")]
  public void ComputeWedge_SingleSectorInTrack_HasOneSectorSweepAtOuterRim() {
    var g = new MediaGeometry(512, 64, 4, 64L * 4 * 100); // 64 spt, 4 heads, 100 cyls
    // Byte range covering exactly sector 0 of cylinder 0.
    var w = PlatterWedgeLayout.ComputeWedge(g, 0, 512);

    var expectedSweep = 2.0 * Math.PI / 64;
    Assert.That(w.StartAngle, Is.EqualTo(0.0).Within(1e-9), "sector 0 starts at 12 o'clock");
    Assert.That(w.SweepAngle, Is.EqualTo(expectedSweep).Within(1e-9), "single sector → 1/64 turn");
    Assert.That(w.OuterRadius, Is.EqualTo(1.0).Within(1e-9), "cylinder 0 outer edge is the rim");
    Assert.That(w.OuterRadius - w.InnerRadius, Is.GreaterThan(0), "wedge has positive radial thickness");
  }

  [Test, Category("Spec")]
  public void ComputeWedge_KnownSectorRange_HasMatchingAngularExtent() {
    // 8 spt geometry → each sector spans 45°. Sectors 2..3 → start at 90°, sweep 90°.
    var g = new MediaGeometry(512, 8, 1, 8L * 100);
    var w = PlatterWedgeLayout.ComputeWedge(g, 2 * 512, 4 * 512); // sectors 2,3

    Assert.That(w.StartAngle, Is.EqualTo(2.0 * Math.PI * 2 / 8).Within(1e-9));
    Assert.That(w.SweepAngle, Is.EqualTo(2.0 * Math.PI * 2 / 8).Within(1e-9));
  }

  [Test, Category("Spec")]
  public void ComputeWedge_RangeSpanningMultipleTracks_BecomesFullRing() {
    var g = new MediaGeometry(512, 16, 1, 16L * 50);
    // Span 3 full tracks (48 sectors) — should clamp to a full ring at this band.
    var w = PlatterWedgeLayout.ComputeWedge(g, 0, 48L * 512);

    Assert.That(w.IsFullRing, Is.True, "multi-track range → full annular ring");
    Assert.That(w.SweepAngle, Is.EqualTo(2.0 * Math.PI).Within(1e-9));
  }

  [Test, Category("Spec")]
  public void ComputeWedge_DeeperCylinder_HasSmallerOuterRadius() {
    var g = new MediaGeometry(512, 64, 1, 64L * 100); // 100 cylinders
    // Cylinder 0 vs cylinder 50 — outer cylinder should sit further out.
    var outer = PlatterWedgeLayout.ComputeWedge(g, 0, 512);
    var inner = PlatterWedgeLayout.ComputeWedge(g, 50L * 64 * 512, 50L * 64 * 512 + 512);

    Assert.That(outer.OuterRadius, Is.GreaterThan(inner.OuterRadius), "cyl 0 sits at outer rim");
    Assert.That(inner.OuterRadius, Is.GreaterThan(0.25), "still inside the platter");
  }

  [Test, Category("Spec")]
  public void ComputeStackedWedge_StraightHeads_ProduceDistinctZ() {
    var g = new MediaGeometry(512, 8, 4, 8L * 4 * 50);
    // First sector of each head's track 0 → distinct head indices.
    var heads = new HashSet<int>();
    for (var head = 0; head < g.Heads; head++) {
      var lba = g.LbaFromChs(0, head, 0);
      var byteOffset = lba * g.BytesPerSector;
      var sw = PlatterWedgeLayout.ComputeStackedWedge(g, byteOffset, byteOffset + 512);
      heads.Add(sw.Head);
    }
    Assert.That(heads.Count, Is.EqualTo(g.Heads), "each platter surface produces a distinct Head value");
    Assert.That(heads, Is.EquivalentTo(Enumerable.Range(0, g.Heads)));
  }

  [Test, Category("Spec")]
  public void RadiusForCylinder_Cyl0_IsOuterRim() {
    var g = new MediaGeometry(512, 63, 16, 63L * 16 * 100);
    var r0 = PlatterWedgeLayout.RadiusForCylinder(g, 0);
    var rLast = PlatterWedgeLayout.RadiusForCylinder(g, g.Cylinders - 1);

    Assert.That(r0, Is.EqualTo(1.0).Within(1e-9), "cylinder 0 sits at the rim");
    Assert.That(rLast, Is.LessThan(r0), "deeper cylinders sit further in");
    Assert.That(rLast, Is.GreaterThanOrEqualTo(0.25), "innermost cylinder above the spindle hole");
  }

  [Test, Category("Spec")]
  public void CylinderThickness_AccumulatesToFillTrackBand() {
    var g = new MediaGeometry(512, 63, 16, 63L * 16 * 50);
    var thickness = PlatterWedgeLayout.CylinderThickness(g, 0.25);
    // Sum across all cylinders should equal (1 - innerFraction) = 0.75
    var total = thickness * g.Cylinders;
    Assert.That(total, Is.EqualTo(0.75).Within(1e-9));
  }

  [Test, Category("Spec")]
  public void ComputeWedge_RangesProduceAllValidWedges() {
    var g = MediaGeometry.Standard(16L * 1024 * 1024); // 16 MB
    // Walk a representative sample of byte ranges; every wedge must be
    // within the platter and have non-negative sweep.
    for (long off = 0; off < g.TotalSectors * g.BytesPerSector; off += 64 * 1024) {
      var w = PlatterWedgeLayout.ComputeWedge(g, off, off + 4096);
      Assert.That(w.SweepAngle, Is.GreaterThan(0));
      Assert.That(w.SweepAngle, Is.LessThanOrEqualTo(2.0 * Math.PI + 1e-9));
      Assert.That(w.InnerRadius, Is.InRange(0.0, 1.0));
      Assert.That(w.OuterRadius, Is.InRange(0.0, 1.0));
      Assert.That(w.OuterRadius, Is.GreaterThanOrEqualTo(w.InnerRadius));
    }
  }
}
