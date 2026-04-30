namespace Compression.Tests.Pbp;

[TestFixture]
public class PbpTests {

  private static readonly string[] AllSectionNames = [
    "PARAM.SFO", "ICON0.PNG", "ICON1.PMF", "PIC0.PNG",
    "PIC1.PNG", "SND0.AT3", "DATA.PSP", "DATA.PSAR",
  ];

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllSectionsPresent() {
    var payloads = new Dictionary<string, byte[]>();
    for (var i = 0; i < AllSectionNames.Length; ++i) {
      var data = new byte[16 + i];
      Array.Fill(data, (byte)(0x10 + i));
      payloads[AllSectionNames[i]] = data;
    }

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pbp.PbpWriter(ms, leaveOpen: true)) {
      foreach (var name in AllSectionNames)
        w.AddEntry(name, payloads[name]);
    }
    ms.Position = 0;

    using var r = new FileFormat.Pbp.PbpReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(AllSectionNames.Length));
    for (var i = 0; i < AllSectionNames.Length; ++i) {
      var entry = r.Entries[i];
      Assert.That(entry.Name, Is.EqualTo(AllSectionNames[i]));
      Assert.That(entry.Size, Is.EqualTo(payloads[entry.Name].Length));
      Assert.That(r.Extract(entry), Is.EqualTo(payloads[entry.Name]));
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_PartialSections() {
    var paramSfo = new byte[32];
    var icon0 = new byte[64];
    var dataPsp = new byte[128];
    Array.Fill(paramSfo, (byte)0x11);
    Array.Fill(icon0, (byte)0x22);
    Array.Fill(dataPsp, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pbp.PbpWriter(ms, leaveOpen: true)) {
      w.AddEntry("PARAM.SFO", paramSfo);
      w.AddEntry("ICON0.PNG", icon0);
      w.AddEntry("DATA.PSP", dataPsp);
    }
    ms.Position = 0;

    // Verify the on-disk header offsets directly: empty middle/trailing offsets must equal
    // the next non-empty offset (or EOF for trailing missing sections).
    var bytes = ms.ToArray();
    Assert.That(bytes[0], Is.EqualTo((byte)0x00));
    Assert.That(bytes[1], Is.EqualTo((byte)0x50));
    Assert.That(bytes[2], Is.EqualTo((byte)0x42));
    Assert.That(bytes[3], Is.EqualTo((byte)0x50));

    var offsets = new uint[8];
    for (var i = 0; i < 8; ++i)
      offsets[i] = BitConverter.ToUInt32(bytes, 8 + i * 4);

    Assert.That(offsets[0], Is.EqualTo((uint)40));                                            // PARAM.SFO
    Assert.That(offsets[1], Is.EqualTo((uint)(40 + paramSfo.Length)));                        // ICON0.PNG
    var afterIcon0 = (uint)(40 + paramSfo.Length + icon0.Length);
    Assert.That(offsets[2], Is.EqualTo(afterIcon0));                                          // ICON1.PMF (empty -> next)
    Assert.That(offsets[3], Is.EqualTo(afterIcon0));                                          // PIC0.PNG  (empty)
    Assert.That(offsets[4], Is.EqualTo(afterIcon0));                                          // PIC1.PNG  (empty)
    Assert.That(offsets[5], Is.EqualTo(afterIcon0));                                          // SND0.AT3  (empty)
    Assert.That(offsets[6], Is.EqualTo(afterIcon0));                                          // DATA.PSP starts here
    var afterDataPsp = (uint)(afterIcon0 + dataPsp.Length);
    Assert.That(offsets[7], Is.EqualTo(afterDataPsp));                                        // DATA.PSAR trailing empty -> EOF
    Assert.That(bytes.Length, Is.EqualTo((int)afterDataPsp));

    using var r = new FileFormat.Pbp.PbpReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("PARAM.SFO"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("ICON0.PNG"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("DATA.PSP"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(paramSfo));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(icon0));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(dataPsp));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeDataPsar() {
    var paramSfo = new byte[64];
    var dataPsar = new byte[1 * 1024 * 1024];
    Array.Fill(paramSfo, (byte)0x77);
    for (var i = 0; i < dataPsar.Length; ++i)
      dataPsar[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pbp.PbpWriter(ms, leaveOpen: true)) {
      w.AddEntry("PARAM.SFO", paramSfo);
      w.AddEntry("DATA.PSAR", dataPsar);
    }
    ms.Position = 0;

    using var r = new FileFormat.Pbp.PbpReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("PARAM.SFO"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("DATA.PSAR"));
    Assert.That(r.Entries[1].Size, Is.EqualTo(dataPsar.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(paramSfo));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(dataPsar));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xAB);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Pbp.PbpReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsUnknownSection() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Pbp.PbpWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry("FOO.BIN", [1, 2, 3]));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsDuplicateSection() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Pbp.PbpWriter(ms, leaveOpen: true);
    w.AddEntry("PARAM.SFO", [1, 2, 3]);
    Assert.Throws<ArgumentException>(() => w.AddEntry("PARAM.SFO", [4, 5, 6]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Pbp.PbpFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Pbp"));
    Assert.That(d.Extensions, Contains.Item(".pbp"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".pbp"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x00, 0x50, 0x42, 0x50 }));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("pbp"));
  }
}
