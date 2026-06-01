#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Lib;

namespace Compression.Tests.Gemdos;

[TestFixture]
public class GemdosDetectionTests {

  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // GEMDOS jump byte = 0x60 (BRA.S) at offset 0.
    image[0] = 0x60;
    image[1] = 0x12;  // branch displacement (arbitrary)
    // bytes-per-sector = 512 at offset 0x0B (LE u16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0B, 2), 512);
    // sectors-per-cluster
    image[0x0D] = 2;
    // reserved sectors
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0E, 2), 1);
    // num FATs
    image[0x10] = 2;
    // root entries
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x11, 2), 112);
    // total sectors
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x13, 2), 720);
    image[0x15] = 0xF9;  // media descriptor
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x16, 2), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x18, 2), 9);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1A, 2), 2);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesGemdos_ByMagic() {
    var image = BuildMinimal();
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("Gemdos").IgnoreCase,
      $"FormatDetector must recognise GEMDOS via 0x60 BRA.S jump byte at offset 0. Got: {fmt}");
  }
}
