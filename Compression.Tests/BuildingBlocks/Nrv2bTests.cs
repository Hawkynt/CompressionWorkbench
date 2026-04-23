using System.Text;
using Compression.Core.Dictionary.Nrv2b;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class Nrv2bTests {

  private static readonly Nrv2bBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x42 };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ShortLiteralRun_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("Hello, NRV2B world!");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingPattern_IsCompressedAndRoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 16);

    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    // Some compression expected on a pattern with length-16 period.
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LongRun_OfSameByte_RoundTrips() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0xAA);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    // Highly compressible: 4KB of constant should be much smaller.
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RandomData_RoundTrips() {
    var rng = new Random(0xC0FFEE);
    var data = new byte[8192];
    rng.NextBytes(data);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    // Random data might expand, just verify round trip.
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    // Alternating repeating blocks and random noise — exercises all encoding paths.
    var rng = new Random(42);
    var parts = new List<byte>();
    for (var i = 0; i < 10; i++) {
      var block = new byte[256];
      if (i % 2 == 0)
        Array.Fill(block, (byte)i);
      else
        rng.NextBytes(block);
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decompress_TooSmallHeader_Throws() {
    Assert.That(() => Bb.Decompress([0x01]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void BbRegistry_Enumerates_Nrv2b() {
    var id = Bb.Id;
    Assert.That(id, Is.EqualTo("BB_Nrv2b"));
    Assert.That(Bb.DisplayName, Is.EqualTo("NRV2B"));
    Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  // ── LE16 width variant (UPX method 4) ───────────────────────────────────
  //
  // The width-matched encoder writes 16-bit little-endian bit-words MSB-first
  // with bytes interleaved at every word boundary. Round-tripping through the
  // matching DecompressRawLe16 helper proves the LE16 RefillWord path executes
  // correctly. The compressed payload is structurally distinct from the LE32
  // form (more, smaller word-boundaries) so this is not just re-running LE32.

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_LiteralRun_RoundTrips() {
    // 17 chars: forces the encoder past the first 16-bit word boundary so
    // the decoder must perform at least one RefillWord at the LE16 width.
    var data = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQ");
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2bBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_ConstantByte_RoundTrips() {
    var data = new byte[1024];
    Array.Fill<byte>(data, 0xC3);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2bBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    // A 1-KB run should compress meaningfully even via the LE16 packing.
    Assert.That(bare.Length, Is.LessThan(data.Length / 2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RepeatingPattern_RoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 19);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2bBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Le16_RandomData_RoundTrips() {
    var rng = new Random(0xCAFE);
    var data = new byte[3000];
    rng.NextBytes(data);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var round = Nrv2bBuildingBlock.DecompressRawLe16(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  // ── 8-bit width variant (UPX method 6) ──────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_LiteralRun_RoundTrips() {
    // 9 chars forces at least one cross-byte boundary in the bit stream.
    var data = Encoding.ASCII.GetBytes("ABCDEFGHI");
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2bBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_ConstantByte_RoundTrips() {
    var data = new byte[512];
    Array.Fill<byte>(data, 0x33);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2bBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RepeatingPattern_RoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 13);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2bBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Byte_RandomData_RoundTrips() {
    var rng = new Random(0xC0DE);
    var data = new byte[1500];
    rng.NextBytes(data);
    var bare = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    var round = Nrv2bBuildingBlock.DecompressRawByte(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Le16AndByte_StreamsAreNotIdenticalToLe32() {
    // Sanity: same input encoded at three widths must yield three distinct
    // payloads (otherwise the width-aware encoder/decoder paths aren't
    // actually exercising the different word-boundary schedules).
    var data = Encoding.ASCII.GetBytes("UPX width sentinel — the rains fall mainly on the plain");
    var le32 = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 4);
    var le16 = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 2);
    var by = Nrv2bBuildingBlock.CompressBare(data, refillWidthBytes: 1);
    Assert.That(le16, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le32).AsCollection);
    Assert.That(by, Is.Not.EqualTo(le16).AsCollection);
  }
}
