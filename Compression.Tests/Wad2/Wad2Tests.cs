namespace Compression.Tests.Wad2;

[TestFixture]
public class Wad2Tests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleEntry() {
    var data = "Quake texture data"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad2.Wad2Writer(ms, leaveOpen: true))
      w.AddEntry("STONE1", data);
    ms.Position = 0;

    var r = new FileFormat.Wad2.Wad2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("STONE1"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleEntries() {
    var data1 = new byte[256];
    var data2 = new byte[128];
    Array.Fill(data1, (byte)0xAA);
    Array.Fill(data2, (byte)0xBB);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad2.Wad2Writer(ms, leaveOpen: true)) {
      w.AddEntry("TEXTURE1", data1);
      w.AddEntry("TEXTURE2", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Wad2.Wad2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEXTURE1"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("TEXTURE2"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Wad2Magic() {
    var data = "WAD2 entry"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad2.Wad2Writer(ms, leaveOpen: true, isWad3: false))
      w.AddEntry("BRICK1", data);
    ms.Position = 0;

    var r = new FileFormat.Wad2.Wad2Reader(ms);
    Assert.That(r.IsWad3, Is.False);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Wad2.Wad2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Wad2"));
    Assert.That(d.Extensions, Contains.Item(".wad"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("WAD2"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("WAD3"u8.ToArray()));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".wad"));
  }

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    // 12 bytes minimum with bad magic
    var buf = new byte[12];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Wad2.Wad2Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    using var ms = new MemoryStream([0x57, 0x41, 0x44]); // "WAD" — only 3 bytes
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Wad2.Wad2Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void EntryType_Preserved() {
    var data = new byte[64];
    Array.Fill(data, (byte)0xCC);
    const byte paletteType = 0x40; // '@' = palette

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad2.Wad2Writer(ms, leaveOpen: true))
      w.AddEntry("PAL", data, paletteType);
    ms.Position = 0;

    var r = new FileFormat.Wad2.Wad2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Type, Is.EqualTo(paletteType));
    Assert.That(r.Entries[0].Compression, Is.EqualTo((byte)0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
