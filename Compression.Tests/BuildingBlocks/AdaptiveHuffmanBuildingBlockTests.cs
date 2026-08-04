using System.Text;
using Compression.Core.Entropy.AdaptiveHuffman;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class AdaptiveHuffmanBuildingBlockTests {

  private static readonly AdaptiveHuffmanBuildingBlock Bb = new();

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x99];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TwoDistinctBytes_RoundTrips() {
    byte[] data = [0x01, 0x02];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[2048];
    Array.Fill(data, (byte)0x42);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0x10 : 0x20);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ThreeSymbolCycle_RoundTrips() {
    var data = new byte[900];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 3);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x46474B31);
    var data = new byte[700];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      "Adaptive Huffman coding rebuilds its tree as symbols arrive, so no table is sent. " +
      "Adaptive Huffman coding rebuilds its tree as symbols arrive, so no table is sent.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void AllByteValuesRepeatedTwice_RoundTrips() {
    // Forces every symbol through both the "first sighting" (NYT escape) path and
    // the "already seen" path, and exercises the full tree-swap machinery once
    // every symbol has a real leaf.
    var data = new byte[512];
    for (var i = 0; i < 512; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_AdaptiveHuffman"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Adaptive Huffman (FGK)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
    });
  }
}
