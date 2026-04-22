#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.S3m;

namespace Compression.Tests.S3m;

[TestFixture]
public class S3mTests {

  // Builds a minimal synthetic S3M with 1 instrument (PCM, 8 samples of 8-bit data)
  // and 1 pattern, laid out across 16-byte paragraphs.
  private static byte[] MakeSyntheticS3m() {
    // Layout (paragraph = 16 bytes):
    //  0..95    → 96-byte main header (song title + metadata).
    //  96       → order table (1 byte).
    //  97..98   → instrument parapointer (1 × 2 bytes).
    //  99..100  → pattern parapointer (1 × 2 bytes).
    //  para 7   → instrument header (80 bytes, 5 paragraphs).
    //  para 12  → pattern data (first 2 bytes = length, rest = packed pattern body).
    //  para 13  → sample PCM data.
    const int sampleLen = 8;
    var instrumentOff = 7 * 16;   // 112
    var patternOff = 12 * 16;     // 192
    var sampleOff = 13 * 16;      // 208
    var buf = new byte[sampleOff + sampleLen];

    // Title at offset 0.
    var title = Encoding.ASCII.GetBytes("SyntheticS3M");
    Buffer.BlockCopy(title, 0, buf, 0, title.Length);
    // 0x1A at offset 28.
    buf[28] = 0x1A;
    // Type byte at 29 (0x10 = module).
    buf[29] = 0x10;
    // Song length = 1, num_instruments = 1, num_patterns = 1.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36, 2), 1);
    // SCRM magic at offset 44.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("SCRM"), 0, buf, 44, 4);

    // Order table at offset 96 (songLen = 1 byte).
    buf[96] = 0;

    // Instrument parapointers at offset 96 + songLen = 97.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(97, 2), (ushort)(instrumentOff / 16));
    // Pattern parapointers at offset 99.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(99, 2), (ushort)(patternOff / 16));

    // Instrument header at para 7 = offset 112. 80 bytes.
    var insOff = instrumentOff;
    buf[insOff] = 1; // type = PCM sample
    var dosName = Encoding.ASCII.GetBytes("TEST.SMP");
    Buffer.BlockCopy(dosName, 0, buf, insOff + 1, dosName.Length);
    // Sample data parapointer: high byte at +13, low word at +14 LE.
    buf[insOff + 13] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(insOff + 14, 2), (ushort)(sampleOff / 16));
    // Length.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(insOff + 16, 4), (uint)sampleLen);
    // flags = 0 (8-bit, mono, uncompressed).
    buf[insOff + 31] = 0;
    var sampleName = Encoding.ASCII.GetBytes("Piano");
    Buffer.BlockCopy(sampleName, 0, buf, insOff + 35, sampleName.Length);
    // "SCRS" at offset +76.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("SCRS"), 0, buf, insOff + 76, 4);

    // Pattern data at para 12 = offset 192. 16 bytes: 2-byte length + 14 bytes content.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(patternOff, 2), 16);

    // Sample PCM at para 13 = offset 208.
    for (var i = 0; i < sampleLen; ++i) buf[sampleOff + i] = (byte)(i * 8);

    return buf;
  }

  [Test]
  public void List_ReturnsFullS3mAndMetadataAndPatternAndSample() {
    var blob = MakeSyntheticS3m();
    using var ms = new MemoryStream(blob);
    var entries = new S3mFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.s3m"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var blob = MakeSyntheticS3m();
    var tmp = Path.Combine(Path.GetTempPath(), "s3m_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new S3mFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.s3m")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
      var sampleDir = Path.Combine(tmp, "samples");
      Assert.That(Directory.Exists(sampleDir), Is.True);
      Assert.That(Directory.GetFiles(sampleDir).Length, Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
