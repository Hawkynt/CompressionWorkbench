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
}
