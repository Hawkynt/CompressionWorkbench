using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.GsOs;

[TestFixture]
public class GsOsDetectionTests {

  // Build a minimal 2IMG-wrapped GS/OS image: 64-byte header + tiny data.
  private static byte[] BuildMinimalImage() {
    var content = new byte[512];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)i;
    var img = new byte[64 + content.Length];
    Encoding.ASCII.GetBytes("2IMG").CopyTo(img.AsSpan(0, 4));
    Encoding.ASCII.GetBytes("XGS!").CopyTo(img.AsSpan(4, 4));   // creator (xgs emulator-style)
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(8, 2), 64); // header size
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(10, 2), 1);  // version
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12, 4), 1);  // image_format = ProDOS order
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(16, 4), 0);  // flags
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20, 4), 1);  // data block count
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(24, 4), 64); // data offset
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(28, 4), (uint)content.Length); // data length
    content.CopyTo(img.AsSpan(64));
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.GsOs.GsOsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("GsOs"));
    Assert.That(d.DisplayName, Is.EqualTo("Apple IIgs GS/OS (2IMG)"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".gsdos"));
    // Magic intentionally omitted to avoid conflict with ProDos's "2IMG" magic.
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Detect_2img_HeaderParsesCorrectly() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.GsOs.GsOsReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Creator, Is.EqualTo("XGS!"));
    Assert.That(r.ImageFormat, Is.EqualTo(1));
    Assert.That(r.DataBlockCount, Is.EqualTo(1u));
    Assert.That(r.DataOffset, Is.EqualTo(64u));
    Assert.That(r.DataLength, Is.EqualTo(512u));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("gsos-prodos-volume.po"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(512));
  }

  [Test, Category("Sad")]
  public void Detect_Not2img_HasNoValidHeader() {
    var img = new byte[256];
    using var r = new FileSystem.GsOs.GsOsReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }
}
