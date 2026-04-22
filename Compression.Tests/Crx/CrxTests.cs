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
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Crx.CrxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_ProducesParseableCrx() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllText(tmpFile, "{\"name\":\"ext\"}");
      var desc = new FileFormat.Crx.CrxFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmpFile, "manifest.json", false)],
        new Compression.Registry.FormatCreateOptions());

      // Validate the CRX3 envelope: magic, version=3, header_len=0.
      var bytes = ms.ToArray();
      Assert.That(bytes[..4], Is.EqualTo(new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }));
      Assert.That(BitConverter.ToUInt32(bytes, 4), Is.EqualTo(3u));
      Assert.That(BitConverter.ToUInt32(bytes, 8), Is.EqualTo(0u));

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("manifest.json"));
    } finally {
      File.Delete(tmpFile);
    }
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
