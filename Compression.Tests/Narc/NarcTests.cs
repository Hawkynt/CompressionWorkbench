namespace Compression.Tests.Narc;

[TestFixture]
public class NarcTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "hello narc"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Narc.NarcWriter(ms, leaveOpen: true))
      w.AddEntry("test.dat", data);
    ms.Position = 0;

    var r = new FileFormat.Narc.NarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.dat"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = new byte[100];
    var d2 = new byte[37];
    var d3 = new byte[512];
    Array.Fill(d1, (byte)0x11);
    Array.Fill(d2, (byte)0x22);
    Array.Fill(d3, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Narc.NarcWriter(ms, leaveOpen: true)) {
      w.AddEntry("file1", d1);
      w.AddEntry("file2", d2);
      w.AddEntry("file3", d3);
    }
    ms.Position = 0;

    var r = new FileFormat.Narc.NarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("file1"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("file2"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("file3"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[32 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Narc.NarcWriter(ms, leaveOpen: true))
      w.AddEntry("big.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Narc.NarcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Narc.NarcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadBom() {
    // Hand-crafted NITRO header with the right magic but BOM = 0x1234 instead of 0xFFFE.
    var buf = new byte[FileFormat.Narc.NarcConstants.NitroHeaderSize];
    buf[0] = (byte)'N'; buf[1] = (byte)'A'; buf[2] = (byte)'R'; buf[3] = (byte)'C';
    buf[4] = 0x34; buf[5] = 0x12;        // bad BOM
    buf[6] = 0x00; buf[7] = 0x01;        // version
    buf[8] = 0x10; buf[9] = 0; buf[10] = 0; buf[11] = 0; // file size
    buf[12] = 0x10; buf[13] = 0;         // header size
    buf[14] = 0x03; buf[15] = 0;         // section count
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Narc.NarcReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Narc.NarcWriter(ms, leaveOpen: true);
    var longName = new string('a', 128); // 128 > 127
    Assert.Throws<ArgumentException>(() => w.AddEntry(longName, [0x00]));
  }

  [Test, Category("HappyPath")]
  public void Header_NitroSizeIsExactly16() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Narc.NarcWriter(ms, leaveOpen: true))
      w.AddEntry("x", [0x42]);
    var bytes = ms.ToArray();

    // BTAF magic must start exactly 16 bytes in (right after the NITRO header).
    Assert.That(bytes[16], Is.EqualTo((byte)'B'));
    Assert.That(bytes[17], Is.EqualTo((byte)'T'));
    Assert.That(bytes[18], Is.EqualTo((byte)'A'));
    Assert.That(bytes[19], Is.EqualTo((byte)'F'));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Narc.NarcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Narc"));
    Assert.That(d.DisplayName, Is.EqualTo("Nintendo NARC"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".narc"));
    Assert.That(d.Extensions, Contains.Item(".narc"));
    Assert.That(d.Extensions, Contains.Item(".carc"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("NARC"u8.ToArray()));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("narc"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }
}
