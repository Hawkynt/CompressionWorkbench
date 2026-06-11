#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mod669;

namespace Compression.Tests.Mod669;

[TestFixture]
public class Mod669Tests {

  private const int PatternBytes = 64 * 8 * 3; // 1536

  // 1 sample, 1 pattern.
  private static byte[] MakeSynthetic669() {
    const int numSamples = 1;
    const int numPatterns = 1;
    const int sampleLen = 16;
    var size = 0x1F1 + numSamples * 25 + numPatterns * PatternBytes + sampleLen;
    var buf = new byte[size];

    buf[0] = (byte)'i'; buf[1] = (byte)'f';
    var msg = Encoding.ASCII.GetBytes("Synthetic 669 module");
    Buffer.BlockCopy(msg, 0, buf, 2, msg.Length);
    buf[0x6E] = numSamples;
    buf[0x6F] = numPatterns;
    buf[0x70] = 0; // restart

    // Sample header at 0x1F1.
    var sName = Encoding.ASCII.GetBytes("smp1");
    Buffer.BlockCopy(sName, 0, buf, 0x1F1, sName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x1F1 + 13, 4), sampleLen);

    // Sample data ramp at the very end.
    for (var i = 0; i < sampleLen; ++i) buf[size - sampleLen + i] = (byte)(i + 1);
    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSynthetic669());
    var entries = new Mod669FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.669"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSynthetic669();
    var tmp = Path.Combine(Path.GetTempPath(), "m669_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new Mod669FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.669")), Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([0x69, 0x66, 0x00, 0x01]);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new Mod669FormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_WeakMagicReliesOnExtension() {
    var d = new Mod669FormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".669"));
    // Confidence kept below 0.9 because the 2-byte "if"/"JN" magic is weak.
    Assert.That(d.MagicSignatures.All(s => s.Confidence < 0.9), Is.True);
  }
}
