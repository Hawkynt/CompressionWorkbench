#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;

namespace Compression.Tests.Gfs1;

[TestFixture]
public class Gfs1DetectionTests {

  private static byte[] BuildMinimal() {
    // Superblock at offset 65536. We anchor magic at SB+0x40 = 65600 to avoid
    // the FileSystem.Gfs2 detector that owns the same magic at SB+0x00 = 65536.
    var image = new byte[65536 + 4096];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(65536 + 0x40, 4), 0x01161970u);
    // sb_multihost_format = 1900 (GFS1, not 1901 = GFS2). Layout-positional;
    // not actually consulted by detection but kept for metadata sanity.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(65536 + 0x1C, 4), 1900u);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesGfs1_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, image.Length));
    Assert.That(fmt.ToString(), Is.EqualTo("Gfs1").IgnoreCase,
      $"FormatDetector must recognise GFS1 via 0x01161970 at offset 65600. Got: {fmt}");
  }
}
