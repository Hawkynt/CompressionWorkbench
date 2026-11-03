using System.Text;
using Compression.Core.Entropy.ContextMixing.SelectedWeights;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ContextMixingSelectedWeightsBuildingBlockTests {

  private static readonly ContextMixingSelectedWeightsBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0xE1];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0x66);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x2468);
    var data = new byte[400];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      "Context mixing blends several fixed-order contexts with weight sets selected by context. " +
      "Context mixing blends several fixed-order contexts with weight sets selected by context.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_ContextMixingSelectedWeights"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Context mixing with context-selected weights"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.ContextMixing));
    });
  }
}
