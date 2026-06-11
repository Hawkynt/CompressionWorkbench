#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ptm;

namespace Compression.Tests.Ptm;

[TestFixture]
public class PtmTests {

  // 1 instrument (8-byte sample), 1 pattern. Sample placed before the pattern so
  // the pattern (last block) runs to EOF.
  private static byte[] MakeSyntheticPtm() {
    const int sampleOff = 448;   // 16-aligned, para 28
    const int sampleLen = 8;
    const int patternOff = 464;  // 16-aligned, para 29
    const int patternLen = 24;
    var size = patternOff + patternLen;
    var buf = new byte[size];

    var title = Encoding.ASCII.GetBytes("SynthPTM");
    Buffer.BlockCopy(title, 0, buf, 0, title.Length);
    buf[28] = 0x1A;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(30, 2), 0x0203); // version
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), 1); // numOrders
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36, 2), 1); // numInstruments
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(38, 2), 1); // numPatterns
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(40, 2), 4); // numChannels
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(42, 2), 0); // flags
    var magic = Encoding.ASCII.GetBytes("PTMF");
    Buffer.BlockCopy(magic, 0, buf, 44, 4);

    // Instrument header 0 at offset 352.
    const int ih = 352;
    buf[ih] = 1; // type = sample
    var iName = Encoding.ASCII.GetBytes("inst");
    Buffer.BlockCopy(iName, 0, buf, ih + 1, iName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(ih + 18, 4), sampleOff); // fileOffset
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(ih + 22, 4), sampleLen); // length
    var sName = Encoding.ASCII.GetBytes("PtmSample");
    Buffer.BlockCopy(sName, 0, buf, ih + 47, sName.Length);

    // Pattern parapointer table at 352 + 80 = 432: pattern 0 -> para 29 (464).
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(432, 2), (ushort)(patternOff / 16));

    // Sample data at sampleOff.
    for (var i = 0; i < sampleLen; ++i) buf[sampleOff + i] = (byte)(i + 1);

    // Pattern data at patternOff.
    for (var i = 0; i < patternLen; ++i) buf[patternOff + i] = (byte)(0x80 + i);

    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSyntheticPtm());
    var entries = new PtmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ptm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticPtm();
    var tmp = Path.Combine(Path.GetTempPath(), "ptm_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new PtmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ptm")), Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    var buf = new byte[48];
    Encoding.ASCII.GetBytes("PTMF").CopyTo(buf, 44);
    using var ms = new MemoryStream(buf);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new PtmFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_PtmfMagicAtOffset44() {
    var blob = MakeSyntheticPtm();
    var sig = new PtmFormatDescriptor().MagicSignatures[0];
    Assert.That(sig.Offset, Is.EqualTo(44));
    Assert.That(blob.AsSpan(44, 4).SequenceEqual(sig.Bytes), Is.True);
  }
}
