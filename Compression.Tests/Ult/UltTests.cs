#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ult;

namespace Compression.Tests.Ult;

[TestFixture]
public class UltTests {

  // Version 4 ULT: 1 channel, 1 pattern => 1 track of 64 single 5-byte events,
  // 1 sample of 8 bytes.
  private static byte[] MakeSyntheticUlt() {
    var b = new List<byte>();
    b.AddRange("MAS_UTrack_V00"u8.ToArray());
    b.Add((byte)'4'); // version digit
    var title = new byte[32];
    Encoding.ASCII.GetBytes("SynthULT").CopyTo(title, 0);
    b.AddRange(title);

    b.Add(0); // song message: 0 lines.

    const int numSamples = 1;
    b.Add(numSamples);
    // Sample header v4 = 66 bytes.
    var sh = new byte[66];
    Encoding.ASCII.GetBytes("UltSample").CopyTo(sh, 0); // name[32]
    // sizeStart u32 at +52 = 0, sizeEnd u32 at +56 = 8 (8-bit -> 8 bytes), flags +61 = 0.
    BinaryPrimitives.WriteUInt32LittleEndian(sh.AsSpan(56, 4), 8);
    b.AddRange(sh);

    // Order table: 256 bytes.
    b.AddRange(new byte[256]);

    // lastChannel (0 => 1 channel), lastPattern (0 => 1 pattern).
    b.Add(0);
    b.Add(0);

    // Pan positions: 1 byte per channel (v>=3).
    b.Add(8);

    // Track data: 1 track of 64 single 5-byte events = 320 bytes.
    for (var r = 0; r < 64; ++r) {
      b.Add((byte)(r % 60 + 1)); // note (not 0xFC)
      b.Add(0); b.Add(0); b.Add(0); b.Add(0);
    }

    // Sample data: 8 bytes.
    for (var i = 0; i < 8; ++i) b.Add((byte)(i + 1));

    return b.ToArray();
  }

  [Test]
  public void List_ExposesFullMetadataTrackAndSample() {
    using var ms = new MemoryStream(MakeSyntheticUlt());
    var entries = new UltFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.ult"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/track_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticUlt();
    var tmp = Path.Combine(Path.GetTempPath(), "ult_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new UltFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ult")), Is.EqualTo(blob));
      Assert.That(Directory.Exists(Path.Combine(tmp, "patterns")), Is.True);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream("MAS_UTrack_V004"u8.ToArray());
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new UltFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicMatches() {
    var blob = MakeSyntheticUlt();
    var sig = new UltFormatDescriptor().MagicSignatures[0];
    Assert.That(blob.AsSpan(0, sig.Bytes.Length).SequenceEqual(sig.Bytes), Is.True);
  }
}
