namespace Compression.Tests.Vhd;

[TestFixture]
public class VhdTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_FixedVhd() {
    var data = new byte[512 * 10];
    new Random(42).NextBytes(data);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.Build();

    Assert.That(vhd.Length, Is.EqualTo(data.Length + 512));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData([]);
    var vhd = w.Build();
    Assert.That(vhd.Length, Is.EqualTo(512));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Footer_HasConectixMagic() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(new byte[1024]);
    var vhd = w.Build();
    Assert.That(System.Text.Encoding.ASCII.GetString(vhd, vhd.Length - 512, 8), Is.EqualTo("conectix"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vhd"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vhd"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vhd.VhdReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vhd.VhdReader(ms));
  }
}
