using System.Text;
using Compression.Core.Dictionary.Zx0;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class Zx0Tests {

  private static readonly Zx0BuildingBlock Bb = new();

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
    var data = Encoding.ASCII.GetBytes("Hello, ZX0 world!");
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
    // Greedy LZ77 over a 16-byte period compresses well; expect meaningful
    // reduction. If our greedy heuristic regresses vs ZX0's optimal parser we
    // relax this, but 1 KB of length-16-period data is a layup.
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LongRun_OfSameByte_RoundTrips() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0xAA);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    // 4 KB constant compresses to a single long rep-match — should be << input.
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
    // Random data may expand slightly — just verify round trip.
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    var rng = new Random(42);
    var parts = new List<byte>();
    for (var i = 0; i < 10; i++) {
      var block = new byte[256];
      if (i % 2 == 0) Array.Fill(block, (byte)i);
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
  public void BbRegistry_Enumerates_Zx0() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Zx0"));
    Assert.That(Bb.DisplayName, Is.EqualTo("ZX0"));
    Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void DecompressRaw_BareStream_RoundTrips() {
    // The bare (unprefixed) stream is what UPX-style embedders consume.
    var data = Encoding.ASCII.GetBytes("ZX0 bare-stream round-trip sentinel — exercise DecompressRaw path.");
    var prefixed = Bb.Compress(data);
    var bare = prefixed.AsSpan(4).ToArray();
    var round = Zx0BuildingBlock.DecompressRaw(bare, data.Length);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }
}
