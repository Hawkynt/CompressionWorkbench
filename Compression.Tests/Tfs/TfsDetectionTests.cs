#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Tfs;

[TestFixture]
public class TfsDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // Historical repository heuristic; no located source establishes it as a TFS signature.
    image[0] = 0x54; image[1] = 0x46; image[2] = 0x53; image[3] = 0x01;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_DoesNotIdentifyTfs_ByUnverifiedRepositoryHeuristic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.Not.EqualTo("Tfs").IgnoreCase,
      $"FormatDetector must not recognise TFS from the unverified 0x54465301 repository heuristic. Got: {fmt}");
  }
}
