namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsTests {
  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vdfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vdf"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PSVDSC_V2.00"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsReadOnly() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.False);
    Assert.That(desc, Is.Not.InstanceOf<Compression.Registry.IArchiveCreatable>());
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Vdfs.VdfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Vdfs.VdfsReader(ms));
  }
}
