using System.Text;
using Compression.Core.Dictionary.Quantum;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class QuantumBuildingBlockTests {

  private static readonly QuantumBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x41];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedSentence_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 4)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[256];
    Array.Fill(data, (byte)0x61);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("BoundaryCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x0A17);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedRepeatsAndRandom_RoundTrips() {
    var rng = new Random(7);
    var parts = new List<byte>();
    for (var i = 0; i < 10; ++i) {
      var block = new byte[400];
      if (i % 2 == 0) Array.Fill(block, (byte)i);
      else rng.NextBytes(block);
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("BoundaryCase"), Category("RoundTrip")]
  public void OneMegabyte_RoundTrips() {
    var data = new byte[1 << 20];
    var text = "The quick brown fox jumps over the lazy dog. "u8;
    var rng = new Random(0x51ED);
    for (var i = 0; i < data.Length; ++i)
      data[i] = i % 3 == 0 ? text[i % text.Length] : (byte)rng.Next(256);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Quantum"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Quantum"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
