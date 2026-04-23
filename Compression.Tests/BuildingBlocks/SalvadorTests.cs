using System.Text;
using Compression.Core.Dictionary.Salvador;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class SalvadorTests {

  private static readonly SalvadorBuildingBlock Bb = new();

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
    var data = Encoding.ASCII.GetBytes("Hello, Salvador world!");
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
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LongRun_OfSameByte_RoundTrips() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0xAA);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RandomData_RoundTrips() {
    var rng = new Random(0xDECAF);
    var data = new byte[8192];
    rng.NextBytes(data);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    var rng = new Random(99);
    var parts = new List<byte>();
    for (var i = 0; i < 10; i++) {
      var block = new byte[256];
      if (i % 2 == 0) Array.Fill(block, (byte)(i + 0x30));
      else rng.NextBytes(block);
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
  public void BbRegistry_Enumerates_Salvador() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Salvador"));
    Assert.That(Bb.DisplayName, Is.EqualTo("Salvador"));
    Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void DecompressRaw_BareStream_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("Salvador bare-stream round-trip sentinel — exercise DecompressRaw path.");
    var prefixed = Bb.Compress(data);
    var bare = prefixed.AsSpan(4).ToArray();
    var round = SalvadorBuildingBlock.DecompressRaw(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void SalvadorAndZx0_StreamsDiffer() {
    // Salvador = ZX0 with inverted offset-MSB Elias-gamma. For any input with
    // at least one new-offset match, the two encodings must be structurally
    // distinct. Use a long repeating pattern that forces new-offset matches.
    var data = new byte[512];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 251);
    var sal = Bb.Compress(data);
    var zx0 = new Compression.Core.Dictionary.Zx0.Zx0BuildingBlock().Compress(data);
    Assert.That(sal, Is.Not.EqualTo(zx0).AsCollection,
      "Salvador (inverted offset-MSB) should not produce the same stream as ZX0 (non-inverted).");
  }
}
