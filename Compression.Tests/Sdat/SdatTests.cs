#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Sdat;
using FileFormat.Swav;

namespace Compression.Tests.Sdat;

[TestFixture]
public class SdatTests {

  private static short[] MakeTone(int n, double period, double amp) {
    var s = new short[n];
    for (var i = 0; i < n; ++i)
      s[i] = (short)(Math.Sin(i * 2.0 * Math.PI / period) * amp);
    return s;
  }

  // ── synthetic SDAT builder: NDS header + INFO/FAT/FILE blocks ──
  //
  //   "SDAT" | bom | version | fileSize | headerSize | numBlocks
  //   4 × (u32 off, u32 size)  → SYMB(absent), INFO, FAT, FILE
  //   FAT  : "FAT " size count count×(off,size,u64 pad)
  //   FILE : "FILE" ... embedded files at FAT offsets
  private static byte[] BuildSdat(params byte[][] files) {
    const int headerSize = 0x40;

    // Place FAT right after header; FILE after FAT.
    var fatOff = headerSize;
    var fatHeader = 12;                                  // "FAT " + size + count
    var fatSize = fatHeader + files.Length * 16;
    var fileOff = fatOff + fatSize;
    var fileHeader = 8;                                  // "FILE" + size (simplified)

    // Compute embedded file absolute offsets inside FILE block.
    var fileDataStart = fileOff + fileHeader;
    var offsets = new int[files.Length];
    var cursor = fileDataStart;
    for (var i = 0; i < files.Length; ++i) {
      offsets[i] = cursor;
      cursor += files[i].Length;
    }
    var fileSize = cursor;

    var buf = new byte[fileSize];
    var s = buf.AsSpan();
    "SDAT"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0xFEFF);
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0x0100);
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)fileSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[12..], headerSize);
    BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 4);

    // block table at 0x10: SYMB(0,0), INFO, FAT, FILE.
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], 0);              // SYMB off (absent)
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], 0);              // SYMB size
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x18..], (uint)fatOff);   // INFO off (point somewhere harmless)
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], 0);              // INFO size
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], (uint)fatOff);   // FAT off
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], (uint)fatSize);  // FAT size
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x28..], (uint)fileOff);  // FILE off
    BinaryPrimitives.WriteUInt32LittleEndian(s[0x2C..], (uint)(fileSize - fileOff)); // FILE size

    // FAT block.
    "FAT "u8.CopyTo(s[fatOff..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(fatOff + 4)..], (uint)fatSize);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(fatOff + 8)..], (uint)files.Length);
    for (var i = 0; i < files.Length; ++i) {
      var r = fatOff + 12 + i * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(s[r..], (uint)offsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(r + 4)..], (uint)files[i].Length);
      // u64 pad left zero.
    }

    // FILE block.
    "FILE"u8.CopyTo(s[fileOff..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(fileOff + 4)..], (uint)(fileSize - fileOff));
    for (var i = 0; i < files.Length; ++i)
      files[i].CopyTo(s[offsets[i]..]);

    return buf;
  }

  private static byte[] FakeSseq() {
    // Minimal NDS-style file with SSEQ magic; content is opaque to us.
    var b = new byte[0x20];
    "SSEQ"u8.CopyTo(b);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0xFEFF);
    return b;
  }

  [Test]
  public void Reader_ParsesEmbeddedSwavAndSseq() {
    var swav = new SwavWriter().Write(MakeTone(500, 30, 9000), 22050);
    var sdat = BuildSdat(swav, FakeSseq());

    var parsed = new SdatReader().Read(sdat);
    Assert.That(parsed.Files.Count, Is.EqualTo(2));
    Assert.That(parsed.Files[0].Magic.Trim(), Is.EqualTo("SWAV"));
    Assert.That(parsed.Files[1].Magic.Trim(), Is.EqualTo("SSEQ"));
  }

  [Test]
  public void Descriptor_SurfacesSwavRawAndDecoded_AndSseqRaw() {
    var pcm = MakeTone(400, 25, 8000);
    var swav = new SwavWriter().Write(pcm, 16000);
    var sdat = BuildSdat(swav, FakeSseq());

    using var ms = new MemoryStream(sdat);
    var entries = new SdatFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.sdat" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "files/000.swav" && e.Kind == "Sample"), Is.True);
    Assert.That(entries.Any(e => e.Name == "files/000.wav" && e.Kind == "Sample"), Is.True);
    Assert.That(entries.Any(e => e.Name == "files/001.sseq" && e.Kind == "Stream"), Is.True);

    // The decoded WAV round-trips the original samples (PCM16 SWAV is lossless).
    using var wavOut = new MemoryStream();
    using var ms2 = new MemoryStream(sdat);
    new SdatFormatDescriptor().ExtractEntry(ms2, "files/000.wav", wavOut, null);
    var wav = wavOut.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000u));
  }

  [Test]
  public void Descriptor_Metadata_CountsFileTypes() {
    var swav = new SwavWriter().Write(MakeTone(200, 20, 7000), 16000);
    var sdat = BuildSdat(swav, FakeSseq());
    using var ms = new MemoryStream(sdat);
    using var meta = new MemoryStream();
    new SdatFormatDescriptor().ExtractEntry(ms, "metadata.ini", meta, null);
    var ini = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(ini, Does.Contain("fileCount=2"));
    Assert.That(ini, Does.Contain("swav=1"));
    Assert.That(ini, Does.Contain("sseq=1"));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "SDAT"u8.ToArray().Concat(new byte[0x40]).ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), 0xFEFF);
    using var ms = new MemoryStream(blob);
    var entries = new SdatFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.sdat"));
  }
}
