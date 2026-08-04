#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // fs_magic sits at 0x1C of the superblock, and the superblock is at block
    // 1 — block 0 is the SGI volume header a driver reads first.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x200 + 0x1C, 4), 0x00072959u);
    // fs_size = 200000 blocks, in the superblock rather than in the volume
    // header that precedes it.
    BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(0x200 + 0x00, 4), 200000);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesEfs_ByMagic() {
    var image = BuildMinimal();
    // The magic is past the first block, so the detector has to be given more
    // than one: the superblock does not start until block 1.
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 1024));
    Assert.That(fmt.ToString(), Is.EqualTo("Efs").IgnoreCase,
      $"FormatDetector must recognise EFS via 0x00072959 at offset 0x21C. Got: {fmt}");
  }
}
