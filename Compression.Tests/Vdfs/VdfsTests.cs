using System.Text;

namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello VDFS!"u8.ToArray();
    var w = new FileFormat.Vdfs.VdfsWriter();
    w.AddFile("TEST.TXT", data);
    var archive = w.Build();

    using var ms = new MemoryStream(archive);
    var r = new FileFormat.Vdfs.VdfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.Contain("TEST"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.Vdfs.VdfsWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    var archive = w.Build();

    using var ms = new MemoryStream(archive);
    var r = new FileFormat.Vdfs.VdfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyArchive() {
    var w = new FileFormat.Vdfs.VdfsWriter();
    var archive = w.Build();

    // Verify header magic
    Assert.That(Encoding.ASCII.GetString(archive, 0, 12), Is.EqualTo("PSVDSC_V2.00"));

    using var ms = new MemoryStream(archive);
    var r = new FileFormat.Vdfs.VdfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vdfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vdf"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PSVDSC_V2.00"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vdfs.VdfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vdfs.VdfsReader(ms));
  }
}
