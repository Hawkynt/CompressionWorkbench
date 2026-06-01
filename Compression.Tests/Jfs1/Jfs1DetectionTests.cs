#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Lib;

namespace Compression.Tests.Jfs1;

[TestFixture]
public class Jfs1DetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // "JFS1" magic at offset 0.
    Encoding.ASCII.GetBytes("JFS1").CopyTo(image.AsSpan(0));
    // s_version = 1 (OS/2 original; 2+ would be Linux JFS2).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x04, 4), 1u);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x08, 8), 65536ul);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10, 4), 4096u);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x14, 2), 12);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesJfs1_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("Jfs1").IgnoreCase,
      $"FormatDetector must recognise OS/2 JFS1 via 'JFS1' at offset 0. Got: {fmt}");
  }
}
