using System.Buffers.Binary;

namespace Compression.Tests.Apfs;

[TestFixture]
public class ApfsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Apfs.ApfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Apfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".apfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("NXSB"u8.ToArray()));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(32));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Apfs.ApfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[8192];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Apfs.ApfsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ValidMagic_NoVolume() {
    var data = new byte[1024 * 1024];
    // Write NX superblock magic at offset 32
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0x4253584E);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 4096); // block size
    using var ms = new MemoryStream(data);
    var r = new FileFormat.Apfs.ApfsReader(ms);
    // No volume found, 0 entries
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
