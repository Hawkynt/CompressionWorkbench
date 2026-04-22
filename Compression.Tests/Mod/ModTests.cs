#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mod;

namespace Compression.Tests.Mod;

[TestFixture]
public class ModTests {

  // Build a minimal synthetic 4-channel MOD with two samples (one with data, one empty)
  // and exactly one pattern (the minimum required).
  private static byte[] MakeSyntheticMod() {
    const int patternBytes = 64 * 4 * 4; // 1024 bytes for 4 channels
    const int sample1Words = 8;
    const int sample1Bytes = sample1Words * 2;
    var total = 1084 + patternBytes + sample1Bytes;
    var buf = new byte[total];

    // 20-byte title.
    var title = Encoding.ASCII.GetBytes("SyntheticMod");
    Buffer.BlockCopy(title, 0, buf, 0, title.Length);

    // 31 sample headers starting at offset 20, 30 bytes each.
    // Sample 1: name "HelloSample", length in words, rest zero.
    var s1Name = Encoding.ASCII.GetBytes("HelloSample");
    Buffer.BlockCopy(s1Name, 0, buf, 20, s1Name.Length);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 22, 2), (ushort)sample1Words);
    // finetune=0, volume=64
    buf[20 + 22 + 2] = 0;
    buf[20 + 22 + 3] = 64;
    // loop start = 0, loop length = 1 (standard "no loop" convention)
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 26, 2), 0);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 28, 2), 1);

    // songlen = 1, one pattern referenced (pattern 0).
    buf[950] = 1;
    buf[951] = 127;
    buf[952] = 0; // order[0] = pattern 0

    // Signature at offset 1080.
    var sig = Encoding.ASCII.GetBytes("M.K.");
    Buffer.BlockCopy(sig, 0, buf, 1080, 4);

    // Pattern 0 (all zero — empty).
    // Sample 1 data (just a bytewise ramp).
    for (var i = 0; i < sample1Bytes; ++i) buf[1084 + patternBytes + i] = (byte)(i - 8);

    return buf;
  }

  [Test]
  public void List_ReturnsFullModAndMetadataAndPatternAndSample() {
    var blob = MakeSyntheticMod();
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mod"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
    // Only 1 sample has data → no sample 02 entry.
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/02_")), Is.False);
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var blob = MakeSyntheticMod();
    var tmp = Path.Combine(Path.GetTempPath(), "mod_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new ModFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.mod")), Is.True);
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
