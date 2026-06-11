#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Far;

namespace Compression.Tests.Far;

[TestFixture]
public class FarTests {

  private const int HeaderLen = 0x145 + 256 * 2; // 837: through pattern-size table.

  // 1 pattern (size 18 = 2-byte header + 16 bytes body), 1 sample (8 bytes).
  private static byte[] MakeSyntheticFar() {
    const int patternSize = 18;
    const int sampleLen = 8;
    var size = HeaderLen + patternSize + 8 /*bitmap*/ + 48 /*sample hdr*/ + sampleLen;
    var buf = new byte[size];

    buf[0] = (byte)'F'; buf[1] = (byte)'A'; buf[2] = (byte)'R'; buf[3] = 0xFE;
    var title = Encoding.ASCII.GetBytes("SynthFAR");
    Buffer.BlockCopy(title, 0, buf, 4, title.Length);
    buf[44] = 0x0D; buf[45] = 0x0A; buf[46] = 0x1A; // eof marker
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(47, 2), HeaderLen);
    buf[49] = 0x10; // version
    for (var c = 0; c < 16; ++c) buf[50 + c] = 1; // all channels on

    buf[0x142] = 1; // numPatterns
    buf[0x143] = 1; // songLength/restart

    // Pattern size table at 0x145: pattern 0 -> 18.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x145, 2), patternSize);

    // Pattern data at HeaderLen.
    var po = HeaderLen;
    buf[po] = 63;   // break row
    buf[po + 1] = 6; // tempo
    for (var i = 0; i < 16; ++i) buf[po + 2 + i] = (byte)(0xA0 + i);

    // Sample on/off bitmap (set bit 0).
    var bmp = po + patternSize;
    buf[bmp] = 0x01;

    // Sample header (48 bytes).
    var sh = bmp + 8;
    var sName = Encoding.ASCII.GetBytes("FarSample");
    Buffer.BlockCopy(sName, 0, buf, sh, sName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 32, 4), sampleLen);
    buf[sh + 39] = 0; // 8-bit

    // Sample data.
    var sd = sh + 48;
    for (var i = 0; i < sampleLen; ++i) buf[sd + i] = (byte)(i + 1);
    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSyntheticFar());
    var entries = new FarFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.far"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticFar();
    var tmp = Path.Combine(Path.GetTempPath(), "far_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new FarFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.far")), Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([(byte)'F', (byte)'A', (byte)'R', 0xFE, 0x00]);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new FarFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicMatchesAndIsDistinct() {
    var blob = MakeSyntheticFar();
    var sig = new FarFormatDescriptor().MagicSignatures[0];
    Assert.That(blob.AsSpan(0, 4).SequenceEqual(sig.Bytes), Is.True);
    Assert.That(sig.Bytes[3], Is.EqualTo(0xFE));
  }
}
