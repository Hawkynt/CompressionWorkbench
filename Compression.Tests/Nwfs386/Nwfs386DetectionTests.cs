#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Nwfs386;

[TestFixture]
public class Nwfs386DetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // "NetW" ASCII at offset 0.
    image[0] = 0x4E; image[1] = 0x65; image[2] = 0x74; image[3] = 0x57;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesNwfs386_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("Nwfs386").IgnoreCase,
      $"FormatDetector must recognise NWFS386 via 'NetW' at offset 0. Got: {fmt}");
  }
}
