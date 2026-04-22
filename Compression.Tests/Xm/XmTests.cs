#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Xm;

namespace Compression.Tests.Xm;

[TestFixture]
public class XmTests {

  // Minimal synthetic XM: header + 1 pattern (empty) + 1 instrument with 1 sample (8 bytes).
  private static byte[] MakeSyntheticXm() {
    const int headerSize = 276; // XM 1.04 standard header size
    const int patHeaderSize = 9;
    const int patPackedSize = 0;
    const int insHeaderSize = 263;  // minimal instrument header with sample-header-size at offset 29
    const int sampleHeaderSize = 40;
    const int sampleLen = 8;

    var total = 60 + headerSize + patHeaderSize + patPackedSize + insHeaderSize + sampleHeaderSize + sampleLen;
    var buf = new byte[total];

    // ID "Extended Module: " (17 bytes) + 20-byte song name + 0x1A + 20-byte tracker name + version.
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("Extended Module: "), 0, buf, 0, 17);
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("SynthXM"), 0, buf, 17, 7);
    buf[37] = 0x1A;
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("TestTracker"), 0, buf, 38, 11);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(58, 2), 0x0104);

    // Header proper at offset 60.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60, 4), (uint)headerSize);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(64, 2), 1); // songLen
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(66, 2), 0); // restartPos
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(68, 2), 4); // numChannels
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(70, 2), 1); // numPatterns
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(72, 2), 1); // numInstruments
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(74, 2), 0); // flags
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(76, 2), 6); // tempo
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(78, 2), 125); // bpm
    // Order table at offset 80 (fills to end of headerSize). order[0] = 0.

    // Pattern.
    var cursor = 60 + headerSize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)patHeaderSize);
    buf[cursor + 4] = 0; // packing type
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor + 5, 2), 64); // rows
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor + 7, 2), (ushort)patPackedSize);
    cursor += patHeaderSize + patPackedSize;

    // Instrument header: first 29 bytes are generic; at +27 lives num_samples (u16 LE).
    var insStart = cursor;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(insStart, 4), (uint)insHeaderSize);
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("PianoIns"), 0, buf, insStart + 4, 8);
    buf[insStart + 26] = 0; // type
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(insStart + 27, 2), 1); // numSamples
    // The rest of the instrument header is unused by our descriptor; leave zero.
    cursor += insHeaderSize;

    // Sample header (40 bytes): length, loopStart, loopLen, volume, finetune, type, pan, relNote, reserved, name[22].
    var shStart = cursor;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(shStart, 4), (uint)sampleLen);
    buf[shStart + 12] = 64; // volume
    buf[shStart + 13] = 0;  // finetune
    buf[shStart + 14] = 0;  // flags (8-bit, no loop)
    buf[shStart + 15] = 128; // pan
    buf[shStart + 16] = 0;  // rel note
    Buffer.BlockCopy(Encoding.ASCII.GetBytes("SynthSmp"), 0, buf, shStart + 18, 8);
    cursor += sampleHeaderSize;

    // Sample data.
    for (var i = 0; i < sampleLen; ++i) buf[cursor + i] = (byte)(i * 4);

    return buf;
  }

  [Test]
  public void List_ReturnsFullXmAndMetadataAndPatternAndInstrumentSample() {
    var blob = MakeSyntheticXm();
    using var ms = new MemoryStream(blob);
    var entries = new XmFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.xm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    // Instrument dir has samples.
    Assert.That(entries.Any(e => e.Name.StartsWith("instruments/01_") && e.Name.Contains("/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var blob = MakeSyntheticXm();
    var tmp = Path.Combine(Path.GetTempPath(), "xm_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new XmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.xm")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
      var instrumentDirs = Directory.GetDirectories(Path.Combine(tmp, "instruments"));
      Assert.That(instrumentDirs.Length, Is.EqualTo(1));
      var sampleFiles = Directory.GetFiles(instrumentDirs[0]);
      Assert.That(sampleFiles.Length, Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
