namespace Compression.Tests.PackDisk;

[TestFixture]
public class PackDiskTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PackDisk_Properties() {
    var desc = new FileFormat.PackDisk.PackDiskFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("PackDisk"));
    Assert.That(desc.Extensions, Does.Contain(".pdsk"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PDSK"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_XMash_Properties() {
    var desc = new FileFormat.PackDisk.XMashFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("xMash"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XMSH"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_XDisk_Properties() {
    var desc = new FileFormat.PackDisk.XDiskFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("xDisk"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XDSK"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Dcs_Properties() {
    var desc = new FileFormat.PackDisk.DcsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Dcs"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.PackDisk.PackDiskReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    data[0] = (byte)'X';
    data[1] = (byte)'X';
    data[2] = (byte)'X';
    data[3] = (byte)'X';
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.PackDisk.PackDiskReader(ms));
  }
}
