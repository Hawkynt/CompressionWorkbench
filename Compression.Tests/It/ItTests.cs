#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.It;

namespace Compression.Tests.It;

[TestFixture]
public class ItTests {

  // Minimal synthetic IT: IMPM header, 1 order, 1 instrument (old-style 64-byte), 1 sample,
  // 1 pattern. Absolute offsets are baked into the offset tables.
  private static byte[] MakeSyntheticIt() {
    const int ordNum = 1;
    const int insNum = 1;
    const int smpNum = 1;
    const int patNum = 1;
    const int oldInstrumentSize = 64;
    const int sampleHdrSize = 80;
    const int patHdrSize = 8;
    const int patPacked = 0;
    const int sampleLen = 8;

    // Layout (absolute offsets):
    //  0..192                    fixed header
    //  192..192+1                order list (1 byte)
    //  193..197                  instrument offset (4 bytes)
    //  197..201                  sample offset (4 bytes)
    //  201..205                  pattern offset (4 bytes)
    //  aligned: instrument / sample header / pattern / sample data (concatenated).
    var insOff = 205;
    var smpHdrOff = insOff + oldInstrumentSize;
    var patOff = smpHdrOff + sampleHdrSize;
    var sampleDataOff = patOff + patHdrSize + patPacked;
    var total = sampleDataOff + sampleLen;

    var buf = new byte[total];

    // IMPM magic.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("IMPM"), 0, buf, 0, 4);
    // Song name (26 bytes).
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("SynthIT"), 0, buf, 4, 7);
    // Counts.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), ordNum);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), insNum);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36, 2), smpNum);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(38, 2), patNum);

    // Order list.
    buf[192] = 0;
    // Instrument offset table.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(193, 4), (uint)insOff);
    // Sample offset table.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(197, 4), (uint)smpHdrOff);
    // Pattern offset table.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(201, 4), (uint)patOff);

    // Old-style instrument header — no IMPI magic; name at offset +20 (26 bytes).
    // Do NOT write "IMPI" → descriptor picks 64-byte size.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("Piano"), 0, buf, insOff + 20, 5);

    // Sample header.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("IMPS"), 0, buf, smpHdrOff, 4);
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("TEST.RAW"), 0, buf, smpHdrOff + 4, 8);
    buf[smpHdrOff + 17] = 64; // global volume (field position chosen freely; tolerated)
    buf[smpHdrOff + 18] = 0x01; // flags: hasData, 8-bit, uncompressed
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("BrightLead"), 0, buf, smpHdrOff + 20, 10);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpHdrOff + 48, 4), (uint)sampleLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(smpHdrOff + 72, 4), (uint)sampleDataOff);

    // Pattern header.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(patOff, 2), (ushort)patPacked);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(patOff + 2, 2), 64); // rows

    // Sample data.
    for (var i = 0; i < sampleLen; ++i) buf[sampleDataOff + i] = (byte)(i * 3);

    return buf;
  }

  [Test]
  public void List_ReturnsFullItAndMetadataAndPatternInstrumentSample() {
    var blob = MakeSyntheticIt();
    using var ms = new MemoryStream(blob);
    var entries = new ItFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.it"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/pattern_00_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("instruments/01_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var blob = MakeSyntheticIt();
    var tmp = Path.Combine(Path.GetTempPath(), "it_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new ItFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.it")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(Directory.GetFiles(Path.Combine(tmp, "patterns")).Length, Is.EqualTo(1));
      Assert.That(Directory.GetFiles(Path.Combine(tmp, "instruments")).Length, Is.EqualTo(1));
      Assert.That(Directory.GetFiles(Path.Combine(tmp, "samples")).Length, Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
