namespace Compression.Tests.Gob;

[TestFixture]
public class GobTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Jedi Knight payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true))
      w.AddEntry("data\\test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Gob.GobReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("data\\test.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[64];
    var data2 = new byte[128];
    var data3 = new byte[32];
    Array.Fill(data1, (byte)0x11);
    Array.Fill(data2, (byte)0x22);
    Array.Fill(data3, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true)) {
      w.AddEntry("levels\\01.lvl", data1);
      w.AddEntry("textures\\wall\\brick.mat", data2);
      w.AddEntry("sound\\door.wav", data3);
    }
    ms.Position = 0;

    var r = new FileFormat.Gob.GobReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("levels\\01.lvl"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("textures\\wall\\brick.mat"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("sound\\door.wav"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true))
      w.AddEntry("big\\payload.dat", data);
    ms.Position = 0;

    var r = new FileFormat.Gob.GobReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Gob.GobReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    var name = new string('a', 200);
    using var ms = new MemoryStream();
    using var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry(name, [0x01, 0x02]));
  }

  [Test, Category("HappyPath")]
  public void Magic_HasTrailingSpace() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true))
      w.AddEntry("x", [0x00]);

    var bytes = ms.ToArray();
    // Must be 'G','O','B',' ' (0x20) — NOT 'G','O','B',0x00. The trailing space
    // is the spec-mandated discriminator vs GOB v1 (Dark Forces).
    Assert.That(bytes[0], Is.EqualTo((byte)0x47));
    Assert.That(bytes[1], Is.EqualTo((byte)0x4F));
    Assert.That(bytes[2], Is.EqualTo((byte)0x42));
    Assert.That(bytes[3], Is.EqualTo((byte)0x20));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Gob.GobFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gob"));
    Assert.That(d.DisplayName, Is.EqualTo("Lucasarts GOB"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".gob"));
    Assert.That(d.Extensions, Contains.Item(".gob"));
    Assert.That(d.Extensions, Contains.Item(".goo"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x47, 0x4F, 0x42, 0x20 }));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    // FormatMethodInfo exposes Name (not Id) and DisplayName.
    Assert.That(d.Methods[0].Name, Is.EqualTo("gob2"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("GOB v2"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AcceptsAlternateVersion() {
    var data = "Outlaws data"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Gob.GobWriter(ms, leaveOpen: true, version: 0x20))
      w.AddEntry("misc\\file.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Gob.GobReader(ms);
    Assert.That(r.Version, Is.EqualTo((uint)0x20));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
