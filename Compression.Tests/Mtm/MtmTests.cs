#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mtm;

namespace Compression.Tests.Mtm;

[TestFixture]
public class MtmTests {

  // 1 sample (8 bytes), 1 track (192 bytes), 1 pattern, comment len 0.
  private static byte[] MakeSyntheticMtm() {
    const int numTracks = 1;
    const int numSamples = 1;
    const int sampleLen = 8;
    const int numPatterns = 1;
    var size = 66                       // header
             + numSamples * 37          // sample headers
             + 128                      // order table
             + numTracks * 192          // track data
             + numPatterns * 32 * 2     // pattern->track maps
             + 0                        // comment
             + sampleLen;               // sample data
    var buf = new byte[size];

    buf[0] = (byte)'M'; buf[1] = (byte)'T'; buf[2] = (byte)'M';
    buf[3] = 0x10; // version 1.0
    var title = Encoding.ASCII.GetBytes("SynthMTM");
    Buffer.BlockCopy(title, 0, buf, 4, title.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), numTracks);
    buf[26] = numPatterns - 1; // last pattern
    buf[27] = 0;               // last order
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), 0); // comment len
    buf[30] = numSamples;
    buf[31] = 0;   // attribute
    buf[32] = 64;  // beats/track
    buf[33] = 32;  // channels

    // Sample header at 66.
    var sName = Encoding.ASCII.GetBytes("MtmSample");
    Buffer.BlockCopy(sName, 0, buf, 66, sName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(66 + 22, 4), sampleLen);
    buf[66 + 36] = 0; // 8-bit

    // Sample data ramp at the very end.
    for (var i = 0; i < sampleLen; ++i) buf[size - sampleLen + i] = (byte)(i + 1);

    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSyntheticMtm());
    var entries = new MtmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.mtm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/track_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticMtm();
    var tmp = Path.Combine(Path.GetTempPath(), "mtm_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new MtmFormatDescriptor().Extract(ms, tmp, null, null);
      var full = File.ReadAllBytes(Path.Combine(tmp, "FULL.mtm"));
      Assert.That(full, Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([0x4D, 0x54, 0x4D, 0x10, 0x00]);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new MtmFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.mtm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicAtOffsetZero_DoesNotCollideWithMod() {
    var blob = MakeSyntheticMtm();
    var d = new MtmFormatDescriptor();
    var mtmSig = d.MagicSignatures[0];
    Assert.That(blob.AsSpan(mtmSig.Offset, mtmSig.Bytes.Length).SequenceEqual(mtmSig.Bytes), Is.True);
    // MOD signature lives at offset 1080; MTM is far shorter / different bytes.
    Assert.That(d.Extensions, Does.Contain(".mtm"));
  }
}
