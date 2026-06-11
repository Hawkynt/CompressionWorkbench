#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Stm;

namespace Compression.Tests.Stm;

[TestFixture]
public class StmTests {

  private const int PatternBytes = 64 * 4 * 4; // 1024

  // 1 pattern, sample 0 with 8 bytes of data.
  private static byte[] MakeSyntheticStm() {
    const int numPatterns = 1;
    const int sampleLen = 8;
    var headerEnd = 48 + 31 * 32 + 128; // header + sample hdrs + order table
    var size = headerEnd + numPatterns * PatternBytes + sampleLen;
    var buf = new byte[size];

    var title = Encoding.ASCII.GetBytes("SynthSTM");
    Buffer.BlockCopy(title, 0, buf, 0, title.Length);
    var tracker = Encoding.ASCII.GetBytes("!Scream!");
    Buffer.BlockCopy(tracker, 0, buf, 20, 8);
    buf[28] = 0x1A; // type
    buf[29] = 2;    // module
    buf[30] = 2; buf[31] = 21; // version 2.21
    buf[32] = 96;   // tempo
    buf[33] = numPatterns;
    buf[34] = 64;   // global vol

    // Sample 0 header at offset 48: name(12), len u16 at +16.
    var sName = Encoding.ASCII.GetBytes("StmSample");
    Buffer.BlockCopy(sName, 0, buf, 48, sName.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48 + 16, 2), sampleLen);

    // Sample data ramp at end.
    for (var i = 0; i < sampleLen; ++i) buf[size - sampleLen + i] = (byte)(i + 1);
    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSyntheticStm());
    var entries = new StmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.stm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticStm();
    var tmp = Path.Combine(Path.GetTempPath(), "stm_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new StmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.stm")), Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    var buf = new byte[40];
    Encoding.ASCII.GetBytes("!Scream!").CopyTo(buf, 20);
    using var ms = new MemoryStream(buf);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new StmFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_ScreamSignatureAtOffset20_DistinctFromS3m() {
    var blob = MakeSyntheticStm();
    var sig = new StmFormatDescriptor().MagicSignatures[0];
    Assert.That(sig.Offset, Is.EqualTo(20));
    Assert.That(blob.AsSpan(20, 8).SequenceEqual(sig.Bytes), Is.True);
    // S3M's "SCRM" sits at offset 44 — ensure our synthetic STM does not carry it.
    Assert.That(blob.AsSpan(44, 4).SequenceEqual("SCRM"u8), Is.False);
  }
}
