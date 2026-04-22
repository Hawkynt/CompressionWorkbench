#pragma warning disable CS1591
using System.Text;
using Compression.Core.Image;

namespace Compression.Tests.Image;

[TestFixture]
public class PlaneSplitterTests {

  [Test]
  public void SplitsRgbIntoThreePlanes() {
    // 2×1 RGB: pixel 0 = (10,20,30), pixel 1 = (40,50,60)
    var pixels = new byte[] { 10, 20, 30, 40, 50, 60 };
    var planes = PlaneSplitter.Split(pixels, 2, 1, PlaneSplitter.PixelLayout.Rgb);
    Assert.That(planes, Has.Count.EqualTo(3));
    Assert.That(planes[0].Name, Is.EqualTo("R.pgm"));
    // Header + 2 bytes of data
    var r = planes[0].Pgm;
    var (header, data) = SplitPgm(r);
    Assert.That(header, Does.StartWith("P5"));
    Assert.That(data, Is.EqualTo(new byte[] { 10, 40 }));
    var g = SplitPgm(planes[1].Pgm).data;
    Assert.That(g, Is.EqualTo(new byte[] { 20, 50 }));
    var b = SplitPgm(planes[2].Pgm).data;
    Assert.That(b, Is.EqualTo(new byte[] { 30, 60 }));
  }

  [Test]
  public void SplitsRgbaIntoFourPlanes() {
    var pixels = new byte[] { 1, 2, 3, 255, 4, 5, 6, 128 };
    var planes = PlaneSplitter.Split(pixels, 2, 1, PlaneSplitter.PixelLayout.Rgba);
    Assert.That(planes.Select(p => p.Name), Is.EqualTo(new[] { "R.pgm", "G.pgm", "B.pgm", "A.pgm" }));
    var a = SplitPgm(planes[3].Pgm).data;
    Assert.That(a, Is.EqualTo(new byte[] { 255, 128 }));
  }

  [Test]
  public void GrayscalePassThrough() {
    var pixels = new byte[] { 42, 99, 200 };
    var planes = PlaneSplitter.Split(pixels, 3, 1, PlaneSplitter.PixelLayout.Grayscale);
    Assert.That(planes, Has.Count.EqualTo(1));
    Assert.That(planes[0].Name, Is.EqualTo("L.pgm"));
    Assert.That(SplitPgm(planes[0].Pgm).data, Is.EqualTo(pixels));
  }

  [Test]
  public void PgmHeaderAdvertisesDimensions() {
    var pixels = new byte[12];
    var planes = PlaneSplitter.Split(pixels, 2, 2, PlaneSplitter.PixelLayout.Rgb);
    var header = SplitPgm(planes[0].Pgm).header;
    Assert.That(header, Does.Contain("2 2"));
    Assert.That(header, Does.Contain("255"));
  }

  private static (string header, byte[] data) SplitPgm(byte[] pgm) {
    // PGM header: "P5\n<w> <h>\n<max>\n"
    var nlIndices = new List<int>();
    for (var i = 0; i < pgm.Length && nlIndices.Count < 3; ++i)
      if (pgm[i] == (byte)'\n') nlIndices.Add(i);
    if (nlIndices.Count < 3) return (Encoding.ASCII.GetString(pgm), []);
    var headerEnd = nlIndices[2] + 1;
    return (Encoding.ASCII.GetString(pgm, 0, headerEnd), pgm[headerEnd..]);
  }
}
