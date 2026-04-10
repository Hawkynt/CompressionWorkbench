using System.Buffers.Binary;

namespace Compression.Tests.F2fs;

[TestFixture]
public class F2fsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.F2fs.F2fsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("F2fs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".f2fs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.F2fs.F2fsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[4096];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.F2fs.F2fsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ValidMagic_EmptyFs() {
    var data = new byte[1024 * 1024]; // 1MB
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024), 0xF2F52010);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 12), 12); // log_blocksize = 12 => 4096
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 72), 10); // nat_blkaddr
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 80), 50); // main_blkaddr
    using var ms = new MemoryStream(data);
    var r = new FileFormat.F2fs.F2fsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
