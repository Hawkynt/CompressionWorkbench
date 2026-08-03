using System.Text;
using Compression.Core.Dictionary.Lzfse;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzfseBuildingBlockTests {

  private static readonly LzfseBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x11 };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TwoBytes_RoundTrips() {
    var data = new byte[] { 0x11, 0x22 };
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
    var rng = new Random(0x5EED);
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
  public void LongDistanceMatch_RoundTrips() {
    var prefix = new byte[40000];
    new Random(3).NextBytes(prefix);
    var pattern = "REPEATED-PATTERN-0123456789"u8.ToArray();
    var data = new byte[prefix.Length + pattern.Length * 2];
    prefix.CopyTo(data, 0);
    pattern.CopyTo(data, prefix.Length);
    pattern.CopyTo(data, prefix.Length + pattern.Length);

    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
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
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void AllMatchesNoTrailingLiterals_RoundTrips() {
    var pattern = "0123456789"u8.ToArray();
    var data = new byte[pattern.Length * 500];
    for (var i = 0; i < data.Length; i += pattern.Length)
      pattern.CopyTo(data, i);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void BbRegistry_ExposesMetadata() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Lzfse"));
    Assert.That(Bb.DisplayName, Is.EqualTo("LZFSE"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
  }
}
