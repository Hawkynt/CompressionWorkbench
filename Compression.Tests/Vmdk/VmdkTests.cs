namespace Compression.Tests.Vmdk;

[TestFixture]
public class VmdkTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_SparseVmdk() {
    var data = new byte[512 * 10];
    new Random(42).NextBytes(data);
    var w = new FileFormat.Vmdk.VmdkWriter();
    w.SetDiskData(data);
    var vmdk = w.Build();

    using var ms = new MemoryStream(vmdk);
    var r = new FileFormat.Vmdk.VmdkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.img"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.GreaterThanOrEqualTo(data.Length));
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileFormat.Vmdk.VmdkWriter();
    w.SetDiskData([]);
    var vmdk = w.Build();

    using var ms = new MemoryStream(vmdk);
    var r = new FileFormat.Vmdk.VmdkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void SparseHeader_HasKdmvMagic() {
    var w = new FileFormat.Vmdk.VmdkWriter();
    w.SetDiskData(new byte[1024]);
    var vmdk = w.Build();
    Assert.That(vmdk[0], Is.EqualTo(0x4B));
    Assert.That(vmdk[1], Is.EqualTo(0x44));
    Assert.That(vmdk[2], Is.EqualTo(0x4D));
    Assert.That(vmdk[3], Is.EqualTo(0x56));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vmdk"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vmdk"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vmdk.VmdkReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vmdk.VmdkReader(ms));
  }
}
