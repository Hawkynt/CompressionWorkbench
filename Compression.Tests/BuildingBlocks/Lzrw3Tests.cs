using System.Text;
using Compression.Core.Dictionary.Lzrw3;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class Lzrw3Tests {

  private static readonly Lzrw3BuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x81 };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FewBytes_RoundTrips() {
    // Exercises the tail of the stream where a hash window may span a group boundary.
    for (var len = 1; len <= 20; len++) {
      var data = new byte[len];
      for (var i = 0; i < len; i++) data[i] = (byte)(i * 7 + 3);
      var compressed = Bb.Compress(data);
      var round = Bb.Decompress(compressed);
      Assert.That(round, Is.EqualTo(data).AsCollection, $"length={len}");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill<byte>(data, 0x6D);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xABCD);
    var data = new byte[4096];
    rng.NextBytes(data);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TextSample_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The five boxing wizards jump quickly. ", 20)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    var rng = new Random(23);
    var parts = new List<byte>();
    for (var i = 0; i < 12; i++) {
      var block = new byte[300];
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
  public void BbRegistry_ExposesMetadata() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Lzrw3"));
    Assert.That(Bb.DisplayName, Is.EqualTo("LZRW3"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
  }
}
