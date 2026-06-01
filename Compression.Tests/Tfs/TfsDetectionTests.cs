#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Tfs;

[TestFixture]
public class TfsDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // "TFS\x01" magic at offset 0.
    image[0] = 0x54; image[1] = 0x46; image[2] = 0x53; image[3] = 0x01;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesTfs_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("Tfs").IgnoreCase,
      $"FormatDetector must recognise TFS via 0x54465301 at offset 0. Got: {fmt}");
  }
}
