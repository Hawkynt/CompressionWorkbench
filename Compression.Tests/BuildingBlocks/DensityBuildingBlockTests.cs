using System.Text;
using Compression.Core.Dictionary.Density;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class DensityBuildingBlockTests {

  private static readonly DensityBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x37 };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NonMultipleOfFourLength_RoundTrips() {
    var data = "Hello, Density!"u8.ToArray(); // 15 bytes, not a multiple of 4
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill<byte>(data, 0x42);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingFourByteChunks_RoundTripsAndCompresses() {
    var pattern = "ABCD"u8.ToArray();
    var data = new byte[pattern.Length * 2048];
    for (var i = 0; i < data.Length; i += pattern.Length)
      pattern.CopyTo(data, i);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
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
    var rng = new Random(0xBEEF);
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
  public void MixedPattern_RoundTrips() {
    var rng = new Random(7);
    var parts = new List<byte>();
    for (var i = 0; i < 12; i++) {
      var block = new byte[301]; // deliberately not a multiple of 4
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
    Assert.That(Bb.Id, Is.EqualTo("BB_Density"));
    Assert.That(Bb.DisplayName, Is.EqualTo("Density (Chameleon)"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
  }
}
