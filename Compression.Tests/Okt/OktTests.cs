#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Okt;

namespace Compression.Tests.Okt;

[TestFixture]
public class OktTests {

  private static void AddChunk(List<byte> b, string id, byte[] body) {
    b.AddRange(Encoding.ASCII.GetBytes(id));
    var len = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
    b.AddRange(len);
    b.AddRange(body);
  }

  private static byte[] MakeSyntheticOkt() {
    var b = new List<byte>();
    b.AddRange("OKTASONG"u8.ToArray());

    // CMOD: 8 u16 BE; first 4 channels mode 1 (mono).
    var cmod = new byte[16];
    for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteUInt16BigEndian(cmod.AsSpan(i * 2, 2), 1);
    AddChunk(b, "CMOD", cmod);

    // SAMP: one 32-byte sample header. name(20) + len u32 BE.
    var samp = new byte[32];
    Encoding.ASCII.GetBytes("OktSample").CopyTo(samp, 0);
    BinaryPrimitives.WriteUInt32BigEndian(samp.AsSpan(20, 4), 8);
    AddChunk(b, "SAMP", samp);

    // SLEN: pattern count = 1.
    var slen = new byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(slen, 1);
    AddChunk(b, "SLEN", slen);

    // PBOD: pattern body.
    var pbod = new byte[10];
    for (var i = 0; i < pbod.Length; ++i) pbod[i] = (byte)(0xC0 + i);
    AddChunk(b, "PBOD", pbod);

    // SBOD: sample data.
    var sbod = new byte[8];
    for (var i = 0; i < sbod.Length; ++i) sbod[i] = (byte)(i + 1);
    AddChunk(b, "SBOD", sbod);

    return b.ToArray();
  }

  [Test]
  public void List_ExposesFullMetadataPatternAndSample() {
    using var ms = new MemoryStream(MakeSyntheticOkt());
    var entries = new OktFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.okt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticOkt();
    var tmp = Path.Combine(Path.GetTempPath(), "okt_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new OktFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.okt")), Is.EqualTo(blob));
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream("OKTASONG"u8.ToArray());
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new OktFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicMatches() {
    var blob = MakeSyntheticOkt();
    var sig = new OktFormatDescriptor().MagicSignatures[0];
    Assert.That(blob.AsSpan(0, 8).SequenceEqual(sig.Bytes), Is.True);
  }
}
