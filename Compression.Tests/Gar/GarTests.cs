namespace Compression.Tests.Gar;

[TestFixture]
public class GarTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "asset payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gar.GarWriter(ms, leaveOpen: true))
      w.AddEntry("test.bclim", data);
    ms.Position = 0;

    var r = new FileFormat.Gar.GarReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.bclim"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleTypes() {
    var a = "first bclim"u8.ToArray();
    var b = "second bclim"u8.ToArray();
    var c = "lone bcfnt"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gar.GarWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bclim", a);
      w.AddEntry("b.bclim", b);
      w.AddEntry("c.bcfnt", c);
    }
    ms.Position = 0;

    var r = new FileFormat.Gar.GarReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extensions, Has.Count.EqualTo(2));
    Assert.That(r.Extensions, Contains.Item("bclim"));
    Assert.That(r.Extensions, Contains.Item("bcfnt"));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("a.bclim"), Is.True);
    Assert.That(byName.ContainsKey("b.bclim"), Is.True);
    Assert.That(byName.ContainsKey("c.bcfnt"), Is.True);
    Assert.That(r.Extract(byName["a.bclim"]), Is.EqualTo(a));
    Assert.That(r.Extract(byName["b.bclim"]), Is.EqualTo(b));
    Assert.That(r.Extract(byName["c.bcfnt"]), Is.EqualTo(c));

    // Both .bclim files must share the same TypeIndex — that's the bug-prone path.
    Assert.That(byName["a.bclim"].TypeIndex, Is.EqualTo(byName["b.bclim"].TypeIndex));
    Assert.That(byName["a.bclim"].TypeIndex, Is.Not.EqualTo(byName["c.bcfnt"].TypeIndex));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_NoExtension() {
    var data = "extensionless"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gar.GarWriter(ms, leaveOpen: true))
      w.AddEntry("rawfile", data);
    ms.Position = 0;

    var r = new FileFormat.Gar.GarReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("rawfile"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gar.GarWriter(ms, leaveOpen: true))
      w.AddEntry("big.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Gar.GarReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[28];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Gar.GarReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadVersion() {
    // Handcraft a header with magic "GAR\x99" — same prefix, wrong version.
    var buf = new byte[28];
    buf[0] = 0x47;
    buf[1] = 0x41;
    buf[2] = 0x52;
    buf[3] = 0x99;
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Gar.GarReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsGarV5() {
    var d = new FileFormat.Gar.GarFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x47, 0x41, 0x52, 0x05 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Gar.GarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gar"));
    Assert.That(d.DisplayName, Is.EqualTo("Nintendo 3DS GAR"));
    Assert.That(d.Extensions, Contains.Item(".gar"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".gar"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("gar-v5"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
