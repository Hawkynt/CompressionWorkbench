#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Spc;

namespace Compression.Tests.Spc;

[TestFixture]
public class SpcTests {

  // Builds a minimal text-format SPC: 33-byte magic + header + ID666 text tag +
  // 64KB RAM + 128-byte DSP registers.
  private static byte[] MakeSyntheticSpc(bool binaryTag = false) {
    var buf = new byte[0x10100 + 128];
    var magic = "SNES-SPC700 Sound File Data v0.30"u8.ToArray();
    Buffer.BlockCopy(magic, 0, buf, 0, magic.Length);
    buf[0x21] = 26; buf[0x22] = 26; // header 0x1A1A
    buf[0x23] = 26; // has ID666
    // Registers.
    buf[0x25] = 0x00; buf[0x26] = 0x02; // PC = 0x0200
    buf[0x27] = 0x12; // A
    buf[0x28] = 0x34; // X
    buf[0x29] = 0x56; // Y
    buf[0x2A] = 0x02; // PSW
    buf[0x2B] = 0xFF; // SP

    void Ascii(int off, string s) {
      var a = Encoding.ASCII.GetBytes(s);
      Buffer.BlockCopy(a, 0, buf, off, a.Length);
    }
    Ascii(0x2E, "SpcSongTitle");
    Ascii(0x4E, "SpcGameTitle");
    Ascii(0x6E, "Dumper");
    Ascii(0x7E, "A comment");
    Ascii(0xB1, "SpcArtist");
    if (binaryTag) {
      // Put a non-date byte in the date field → binary detection.
      buf[0x9E] = 0x07;
    } else {
      Ascii(0x9E, "06/11/2026");
      Ascii(0xA9, "120");
    }

    // RAM ramp + DSP register pattern.
    for (var i = 0; i < 256; ++i) buf[0x100 + i] = (byte)i;
    for (var i = 0; i < 128; ++i) buf[0x10100 + i] = (byte)(0x80 + (i & 0x7F));
    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataRamAndDsp() {
    using var ms = new MemoryStream(MakeSyntheticSpc());
    var entries = new SpcFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.spc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "ram.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "dsp_registers.bin"), Is.True);
    Assert.That(entries.First(e => e.Name == "ram.bin").OriginalSize, Is.EqualTo(0x10000));
  }

  [Test]
  public void Extract_FullByteIdentical_TextId666Parsed() {
    var blob = MakeSyntheticSpc();
    var tmp = Path.Combine(Path.GetTempPath(), "spc_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new SpcFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.spc")), Is.EqualTo(blob));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "ram.bin")).Length, Is.EqualTo(0x10000));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("id666_format = text"));
      Assert.That(meta, Does.Contain("song_title = SpcSongTitle"));
      Assert.That(meta, Does.Contain("artist = SpcArtist"));
      Assert.That(meta, Does.Contain("reg_pc = 0x0200"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void BinaryId666_Detected() {
    using var ms = new MemoryStream(MakeSyntheticSpc(binaryTag: true));
    var entries = new SpcFormatDescriptor().List(ms, null);
    var tmp = Path.Combine(Path.GetTempPath(), "spcb_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms2 = new MemoryStream(MakeSyntheticSpc(binaryTag: true));
      new SpcFormatDescriptor().Extract(ms2, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("id666_format = binary"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
    Assert.That(entries.Any(e => e.Name == "FULL.spc"), Is.True);
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xCC);
    using var ms = new MemoryStream(garbage);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new SpcFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.spc"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_Magic() {
    var d = new SpcFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("SNES-SPC700 Sound File Data"u8.ToArray()));
    Assert.That(d.Extensions, Does.Contain(".spc"));
  }
}
