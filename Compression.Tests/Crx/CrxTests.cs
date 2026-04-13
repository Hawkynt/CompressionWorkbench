namespace Compression.Tests.Crx;

[TestFixture]
public class CrxTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Crx"));
    Assert.That(d.Extensions, Contains.Item(".crx"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }));
  }

  [Test, Category("HappyPath")]
  public void List_CrxWrappedZip() {
    // Create a ZIP in memory with adjusted offsets by building the CRX first,
    // then writing the ZIP directly at the correct stream position.
    using var crxMs = new MemoryStream();

    // Write CRX3 header: magic "Cr24" + version (3) + header_len (0).
    crxMs.Write([(byte)'C', (byte)'r', (byte)'2', (byte)'4']); // magic
    crxMs.Write(BitConverter.GetBytes((uint)3)); // version
    crxMs.Write(BitConverter.GetBytes((uint)0)); // header length

    // Write ZIP data directly at current position (offset 12).
    using (var w = new FileFormat.Zip.ZipWriter(crxMs, leaveOpen: true))
      w.AddEntry("manifest.json", "{}"u8.ToArray());

    crxMs.Position = 0;
    var desc = new FileFormat.Crx.CrxFormatDescriptor();
    var entries = desc.List(crxMs, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("manifest.json"));
  }
}
