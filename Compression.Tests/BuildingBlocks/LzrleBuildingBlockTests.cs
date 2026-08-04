using System.Text;
using Compression.Core.Dictionary.Lzrle;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzrleBuildingBlockTests {

  private static readonly LzrleBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x2A };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill<byte>(data, 0x00);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 8));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepetitiveNonZero_RoundTripsAndCompresses() {
    var data = new byte[4096];
    Array.Fill<byte>(data, (byte)0x7E);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 8));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[4097];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xFA57);
    var data = new byte[4096];
    rng.NextBytes(data);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TextSample_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. ", 20)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedRunsMatchesAndLiterals_RoundTrips() {
    var rng = new Random(7);
    var parts = new List<byte>();
    for (var i = 0; i < 12; i++) {
      var block = new byte[300];
      switch (i % 3) {
        case 0: Array.Fill(block, (byte)i); break;
        case 1: rng.NextBytes(block); break;
        default:
          for (var j = 0; j < block.Length; ++j)
            block[j] = (byte)(j % 5);
          break;
      }
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void BbRegistry_ExposesMetadata() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Lzrle"));
    Assert.That(Bb.DisplayName, Is.EqualTo("LZRLE"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
  }
}
