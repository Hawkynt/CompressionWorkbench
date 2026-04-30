using System.Text;

namespace Compression.Tests.Afs;

[TestFixture]
public class AfsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Sega Dreamcast lives forever"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Afs.AfsWriter(ms, leaveOpen: true))
      w.AddEntry("test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Afs.AfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = "first"u8.ToArray();
    var d2 = "second second"u8.ToArray();
    var d3 = new byte[300];
    Array.Fill(d3, (byte)0x42);

    var t1 = new DateTime(2001, 11, 27, 10, 15, 30); // Dreamcast end-of-life-ish vibes
    var t2 = new DateTime(2002, 6, 1, 12, 0, 0);
    var t3 = new DateTime(2024, 12, 31, 23, 59, 59);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Afs.AfsWriter(ms, leaveOpen: true)) {
      w.AddEntry("ADX_001.afs", d1, t1);
      w.AddEntry("BGM_005.adx", d2, t2);
      w.AddEntry("event_intro.bin", d3, t3);
    }
    ms.Position = 0;

    var r = new FileFormat.Afs.AfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    Assert.That(r.Entries[0].Name, Is.EqualTo("ADX_001.afs"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Entries[0].LastModified, Is.EqualTo(t1));

    Assert.That(r.Entries[1].Name, Is.EqualTo("BGM_005.adx"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Entries[1].LastModified, Is.EqualTo(t2));

    Assert.That(r.Entries[2].Name, Is.EqualTo("event_intro.bin"));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
    Assert.That(r.Entries[2].LastModified, Is.EqualTo(t3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + 7);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Afs.AfsWriter(ms, leaveOpen: true))
      w.AddEntry("big.dat", data);
    ms.Position = 0;

    var r = new FileFormat.Afs.AfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesNoMetadata() {
    // Hand-craft a minimal AFS with metadata pointer = 0 to verify the reader synthesizes names.
    const int FileCount = 2;
    const int Alignment = 0x800;
    var d1 = "alpha"u8.ToArray();
    var d2 = "beta-payload"u8.ToArray();

    var fixedRegion = 8 + FileCount * 8 + 8; // header + index + metadata pointer
    var off1 = ((fixedRegion + Alignment - 1) / Alignment) * Alignment;
    var off2 = ((off1 + d1.Length + Alignment - 1) / Alignment) * Alignment;
    var totalLen = off2 + d2.Length;

    var buf = new byte[totalLen];

    // Header
    Buffer.BlockCopy(new byte[] { 0x41, 0x46, 0x53, 0x00 }, 0, buf, 0, 4);
    BitConverter.GetBytes((uint)FileCount).CopyTo(buf, 4);

    // Index
    BitConverter.GetBytes((uint)off1).CopyTo(buf, 8);
    BitConverter.GetBytes((uint)d1.Length).CopyTo(buf, 12);
    BitConverter.GetBytes((uint)off2).CopyTo(buf, 16);
    BitConverter.GetBytes((uint)d2.Length).CopyTo(buf, 20);

    // Metadata pointer = 0 / 0
    BitConverter.GetBytes(0u).CopyTo(buf, 24);
    BitConverter.GetBytes(0u).CopyTo(buf, 28);

    // File data
    d1.CopyTo(buf, off1);
    d2.CopyTo(buf, off2);

    using var ms = new MemoryStream(buf);
    var r = new FileFormat.Afs.AfsReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("file_0001.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("file_0002.bin"));
    Assert.That(r.Entries[0].LastModified, Is.Null);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    // Fill with non-AFS garbage (note: 0xFF won't accidentally match "AFS\0").
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Afs.AfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Afs.AfsWriter(ms, leaveOpen: true);
    var longName = new string('x', 32); // 32 ASCII bytes — one over the 31-byte cap
    Assert.Throws<ArgumentException>(() => w.AddEntry(longName, "data"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void FilesAlignedTo0x800() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Afs.AfsWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", [0xAA]);
      w.AddEntry("b.bin", [0xBB]);
    }
    ms.Position = 0;

    var r = new FileFormat.Afs.AfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));

    // First file's offset must be aligned to 0x800.
    Assert.That(r.Entries[0].Offset % 0x800, Is.EqualTo(0));

    // Second file's offset must be aligned and strictly past the first file's payload.
    Assert.That(r.Entries[1].Offset % 0x800, Is.EqualTo(0));
    Assert.That(r.Entries[1].Offset, Is.GreaterThanOrEqualTo(r.Entries[0].Offset + r.Entries[0].Size));

    // Sanity: data still extracts cleanly post-alignment.
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 0xAA }));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(new byte[] { 0xBB }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Afs.AfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Afs"));
    Assert.That(d.DisplayName, Is.EqualTo("Sega AFS"));
    Assert.That(d.Extensions, Contains.Item(".afs"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".afs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x41, 0x46, 0x53, 0x00 }));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods[0].Name, Is.EqualTo("afs"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("AFS"));
  }
}
