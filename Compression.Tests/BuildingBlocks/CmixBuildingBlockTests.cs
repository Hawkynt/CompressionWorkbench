using System.Text;
using Compression.Core.Entropy.ContextMixing.Cmix;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class CmixBuildingBlockTests {

  private static readonly CmixBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x13];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0x55);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x1357);
    var data = new byte[400];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedWords_RoundTripsAndCompresses() {
    // Exercises both the word model and the match model.
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox ", 20)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Cmix"));
      Assert.That(Bb.DisplayName, Is.EqualTo("CMIX (reduced model set)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.ContextMixing));
    });
  }
}
