using Compression.Core.Layout;

namespace Compression.Tests.Layout;

/// <summary>
/// The media-geometry / projection core behind the block-map visualiser:
/// LBA↔CHS must be a bijection, and every projection must produce coordinates
/// in the documented ranges so the circular-platter and cylinder-stack views
/// place blocks consistently.
/// </summary>
[TestFixture]
public class MediaProjectionTests {

  [Test, Category("Spec")]
  public void ChsFromLba_AndBack_IsBijection() {
    var g = new MediaGeometry(512, 63, 16, 63L * 16 * 200);
    for (long lba = 0; lba < g.TotalSectors; lba += 137) {
      var (c, h, s) = g.ChsFromLba(lba);
      Assert.That(h, Is.InRange(0, g.Heads - 1));
      Assert.That(s, Is.InRange(0, g.SectorsPerTrack - 1));
      Assert.That(g.LbaFromChs(c, h, s), Is.EqualTo(lba), $"round-trip at LBA {lba}");
    }
  }

  [Test, Category("Spec")]
  public void Cylinders_CoverAllSectors() {
    var g = new MediaGeometry(512, 63, 255, 63L * 255 * 10 + 5); // 10 full cylinders + a bit
    Assert.That(g.SectorsPerCylinder, Is.EqualTo(63L * 255));
    Assert.That(g.Cylinders, Is.EqualTo(11), "partial cylinder rounds up");
  }

  [Test, Category("Spec")]
  public void CircularPlatter_AngleAndRadiusInRange_Cylinder0IsOuterRim() {
    var g = MediaGeometry.Standard(64L * 1024 * 1024); // 64 MB
    // Sector 0 of track 0 → angle 0, outer rim (radius 1).
    var (a0, r0) = MediaProjection.CircularPlatter(g, 0);
    Assert.That(a0, Is.EqualTo(0.0).Within(1e-9));
    Assert.That(r0, Is.EqualTo(1.0).Within(1e-9), "cylinder 0 is the outer rim");

    for (long lba = 0; lba < g.TotalSectors; lba += 4096) {
      var (angle, radius) = MediaProjection.CircularPlatter(g, lba);
      Assert.That(angle, Is.InRange(0.0, 2 * Math.PI));
      Assert.That(radius, Is.InRange(0.25, 1.0), "radius between inner fraction and outer rim");
    }
  }

  [Test, Category("Spec")]
  public void CircularPlatter_AngleIncreasesAcrossOneTrack() {
    var g = new MediaGeometry(512, 64, 4, 64L * 4 * 50);
    double prev = -1;
    for (var sector = 0; sector < g.SectorsPerTrack; sector++) {
      var (angle, _) = MediaProjection.CircularPlatter(g, sector); // track 0, head 0
      Assert.That(angle, Is.GreaterThan(prev), "angle strictly increases along a track");
      prev = angle;
    }
  }

  [Test, Category("Spec")]
  public void CylinderStack_HeadMapsToHeight() {
    var g = new MediaGeometry(512, 63, 8, 63L * 8 * 100);
    // head 0 → z 0, last head → z 1.
    Assert.That(MediaProjection.CylinderStack(g, g.LbaFromChs(5, 0, 0)).Z, Is.EqualTo(0.0).Within(1e-9));
    Assert.That(MediaProjection.CylinderStack(g, g.LbaFromChs(5, g.Heads - 1, 0)).Z, Is.EqualTo(1.0).Within(1e-9));
    foreach (var lba in new long[] { 0, 1000, 50_000, g.TotalSectors - 1 }) {
      var (angle, radius, z) = MediaProjection.CylinderStack(g, lba);
      Assert.That(angle, Is.InRange(0.0, 2 * Math.PI));
      Assert.That(radius, Is.InRange(0.0, 1.0));
      Assert.That(z, Is.InRange(0.0, 1.0));
    }
  }

  [Test, Category("Spec")]
  public void LinearFraction_SpansZeroToOne() {
    var g = MediaGeometry.Standard(10L * 1024 * 1024);
    Assert.That(MediaProjection.LinearFraction(g, 0), Is.EqualTo(0.0).Within(1e-9));
    Assert.That(MediaProjection.LinearFraction(g, g.TotalSectors - 1), Is.LessThan(1.0));
    Assert.That(MediaProjection.LinearFraction(g, g.TotalSectors), Is.EqualTo(1.0).Within(1e-9));
  }

  [Test, Category("Spec")]
  public void LbaOfByte_RoundsDownToSector() {
    var g = MediaGeometry.Standard(1024 * 1024);
    Assert.That(g.LbaOfByte(0), Is.EqualTo(0));
    Assert.That(g.LbaOfByte(511), Is.EqualTo(0));
    Assert.That(g.LbaOfByte(512), Is.EqualTo(1));
    Assert.That(g.LbaOfByte(1025), Is.EqualTo(2));
  }

  [Test, Category("Spec")]
  public void DegenerateGeometry_DoesNotThrow() {
    var g = new MediaGeometry(512, 0, 0, 0); // clamps to 1/1
    Assert.That(g.Cylinders, Is.GreaterThanOrEqualTo(0));
    Assert.DoesNotThrow(() => MediaProjection.CircularPlatter(g, 0));
    Assert.DoesNotThrow(() => MediaProjection.CylinderStack(g, 0));
  }

  [Test, Category("Spec")]
  public void Heuristic_FloppySized_PicksFloppyGeometry() {
    // 1.44 MB DSHD 3½ — should produce 80 × 2 × 18.
    var g = MediaGeometry.Heuristic(1474560);
    Assert.That(g.SectorsPerTrack, Is.EqualTo(18));
    Assert.That(g.Heads, Is.EqualTo(2));
    Assert.That(g.Cylinders, Is.EqualTo(80));
    Assert.That(g.TotalSectors, Is.EqualTo(2880));
  }

  [Test, Category("Spec")]
  public void Heuristic_720KbFloppy_Picks720KbGeometry() {
    // 720 KB DSDD 3½ — should produce 80 × 2 × 9.
    var g = MediaGeometry.Heuristic(720 * 1024);
    Assert.That(g.SectorsPerTrack, Is.EqualTo(9));
    Assert.That(g.Heads, Is.EqualTo(2));
    Assert.That(g.Cylinders, Is.EqualTo(80));
  }

  [Test, Category("Spec")]
  public void Heuristic_Empty_DoesNotThrow() {
    Assert.DoesNotThrow(() => MediaGeometry.Heuristic(0));
    var g = MediaGeometry.Heuristic(0);
    Assert.That(g.TotalSectors, Is.EqualTo(0));
  }

  [Test, Category("Spec")]
  public void Heuristic_LargeHdd_KeepsCylindersUnder4096() {
    // 1 GB image — heads should scale so cylinder count stays in [128, 4096].
    var g = MediaGeometry.Heuristic(1L * 1024 * 1024 * 1024);
    Assert.That(g.SectorsPerTrack, Is.EqualTo(63));
    Assert.That(g.Cylinders, Is.LessThanOrEqualTo(4096));
    Assert.That(g.Cylinders, Is.GreaterThan(0));
    Assert.That(g.Heads, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("Spec")]
  public void Heuristic_DegradesToOneRing_NeverHappens_ForRealisticSizes() {
    // Regression: MediaGeometry.Standard returned 1 cylinder × 255 heads
    // for a 1.44 MB floppy, collapsing the platter view to a single ring.
    // Heuristic must give multiple cylinders for any sensible image size.
    foreach (var sz in new long[] { 360 * 1024, 720 * 1024, 1474560, 10L * 1024 * 1024, 100L * 1024 * 1024 }) {
      var g = MediaGeometry.Heuristic(sz);
      Assert.That(g.Cylinders, Is.GreaterThan(1),
        $"Heuristic({sz}) returned only {g.Cylinders} cylinder(s) — would collapse to a single ring");
    }
  }
}
