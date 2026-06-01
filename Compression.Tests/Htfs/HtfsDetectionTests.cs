#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;

namespace Compression.Tests.Htfs;

[TestFixture]
public class HtfsDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // s_magic = 0x012FD15D (LE u32) at byte offset 0 of the SB at sector 1 (offset 512).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512, 4), 0x012FD15Du);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesHtfs_ByMagic() {
    var image = BuildMinimal();
    // Need at least offset+4=516 bytes for magic recognition.
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 1024));
    Assert.That(fmt.ToString(), Is.EqualTo("Htfs").IgnoreCase,
      $"FormatDetector must recognise HTFS via 0x012FD15D at offset 512. Got: {fmt}");
  }
}
