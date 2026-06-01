using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Cromemco;

[TestFixture]
public class CromemcoDetectionTests {

  // Build a minimal Cromemco RDOS image with one directory entry.
  private static byte[] BuildMinimalImage() {
    var img = new byte[1024];
    // Bootblock: JP instruction (0xC3) at offset 0.
    img[0] = 0xC3;
    img[1] = 0x00; img[2] = 0x01;
    // Signature "CROMEMCO" at offset 0x0B.
    Encoding.ASCII.GetBytes("CROMEMCO").CopyTo(img.AsSpan(0x0B));

    // Directory entry at offset 0x100 (sector 2).
    var entryOff = 0x100;
    img[entryOff + 0] = 0x00; // user code (not deleted)
    Encoding.ASCII.GetBytes("HELLO   ").CopyTo(img.AsSpan(entryOff + 1, 8));
    Encoding.ASCII.GetBytes("BIN").CopyTo(img.AsSpan(entryOff + 9, 3));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOff + 12, 2), 4); // start block
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOff + 14, 2), 2); // 2 records
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Cromemco.CromemcoFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cromemco"));
    Assert.That(d.DisplayName, Is.EqualTo("Cromemco RDOS"));
    Assert.That(d.Extensions, Does.Contain(".rdos"));
    Assert.That(d.Extensions, Does.Contain(".crom"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("CROMEMCO"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Detect_CromemcoSignature() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.Cromemco.CromemcoReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.SignatureOffset, Is.EqualTo(0x0B));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.BIN"));
    Assert.That(r.Entries[0].StartBlock, Is.EqualTo(4));
  }

  [Test, Category("Sad")]
  public void Detect_NotCromemco_HasNoValidVolume() {
    var img = new byte[1024];
    using var r = new FileSystem.Cromemco.CromemcoReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
