#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // s_magic = 0x00072959 (BE u32) at byte offset 0x18 of the SB at sector 0.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18, 4), 0x00072959u);
    // s_size = 200000 BB
    BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(0x00, 4), 200000);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesEfs_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("Efs").IgnoreCase,
      $"FormatDetector must recognise EFS via 0x00072959 at offset 0x18. Got: {fmt}");
  }
}
