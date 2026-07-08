using System.Text;
using Compression.Core.Dictionary.Aplib;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class AplibTests {

  private static readonly AplibBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x42 };
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ShortLiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("Hello, aPLib world!");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingPattern_IsCompressedAndRoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 16);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LongConstantRun_RoundTrips() {
    var data = new byte[8192];
    Array.Fill<byte>(data, 0xAA);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FarOffsetMatches_RoundTrip() {
    // A block repeated after > 32 KB and after > 1280 bytes exercises the
    // offset-dependent length bumps (offset ≥ 32000 and ≥ 1280 paths).
    var rng = new Random(0xA9);
    var block = new byte[4096];
    rng.NextBytes(block);
    var data = new byte[block.Length * 10];
    for (var i = 0; i < 10; i++) block.CopyTo(data.AsSpan(i * block.Length));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RandomData_RoundTrips() {
    var rng = new Random(0xC0FFEE);
    var data = new byte[8192];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    var rng = new Random(7);
    var parts = new List<byte>();
    for (var i = 0; i < 12; i++) {
      var block = new byte[300];
      if (i % 2 == 0) Array.Fill(block, (byte)i);
      else rng.NextBytes(block);
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decompress_TooSmallHeader_Throws() {
    Assert.That(() => Bb.Decompress([0x01]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Aplib"));
      Assert.That(Bb.DisplayName, Is.EqualTo("aPLib"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }

  // ── Decoder-only grammar paths ──────────────────────────────────────────────
  //
  // Our greedy encoder emits only literals + normal ("10") matches + the end
  // marker, so these hand-assembled reference streams are the only coverage for
  // the "111" single-byte / literal-zero and "110" short-match token paths a real
  // aPLib packer routinely emits. Each stream is hand-traced against aP_depack.

  [Test, Category("HappyPath")]
  public void DecodeToEndMarker_WithOversizedCap_Terminates() {
    // The size-prefixed round-trip stops at the exact size and never reads the
    // end marker; packer handlers decode with an oversized cap and rely on the
    // marker. Exercise that path explicitly.
    var data = new byte[4096];
    data[0] = (byte)'M'; data[1] = (byte)'Z';
    for (var i = 2; i < data.Length; i++) data[i] = (byte)(i % 37);
    var bare = AplibBuildingBlock.CompressBare(data);
    var round = AplibBuildingBlock.DecompressRaw(bare, bare.Length * 64 + 0x10000, out var endMarkerHit, out var consumed);
    Assert.Multiple(() => {
      Assert.That(round, Is.EqualTo(data).AsCollection);
      Assert.That(endMarkerHit, Is.True);
      Assert.That(consumed, Is.EqualTo(bare.Length));
    });
  }

  [Test, Category("EdgeCase")]
  public void Decode_Token111_LiteralZero() {
    // 0x41 verbatim; "111" + 4-bit offset 0000 ⇒ emit 0x00; "110"+0x00 ⇒ end.
    var stream = new byte[] { 0x41, 0xE1, 0x80, 0x00 };
    var round = AplibBuildingBlock.DecompressRaw(stream, 8);
    Assert.That(round, Is.EqualTo(new byte[] { 0x41, 0x00 }).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decode_Token111_SingleByteBackReference() {
    // 0x41 verbatim; "111" + 4-bit offset 0001 ⇒ copy byte at op-1 (0x41); end.
    var stream = new byte[] { 0x41, 0xE3, 0x80, 0x00 };
    var round = AplibBuildingBlock.DecompressRaw(stream, 8);
    Assert.That(round, Is.EqualTo(new byte[] { 0x41, 0x41 }).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decode_Token110_ShortMatch() {
    // 0x41 verbatim; literal 0x42; "110"+0x04 ⇒ offset 2, len 2 ⇒ "AB"; end.
    var stream = new byte[] { 0x41, 0x6C, 0x42, 0x04, 0x00 };
    var round = AplibBuildingBlock.DecompressRaw(stream, 16);
    Assert.That(round, Is.EqualTo(new byte[] { 0x41, 0x42, 0x41, 0x42 }).AsCollection);
  }
}
