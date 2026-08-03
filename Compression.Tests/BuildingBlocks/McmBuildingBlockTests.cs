using System.Text;
using Compression.Core.Entropy.ContextMixing.Mcm;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class McmBuildingBlockTests {

  private static readonly McmBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x5A];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0x44);
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
      "MCM mixes several specialised context maps through a network of mixers. " +
      "MCM mixes several specialised context maps through a network of mixers.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void PeriodicColumnarData_RoundTrips() {
    // Exercises the sparse (skip-1) context in the wide model group.
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 3 == 0 ? 0xAA : (i % 3 == 1 ? 0xBB : 0xCC));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Mcm"));
      Assert.That(Bb.DisplayName, Is.EqualTo("MCM (reduced)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.ContextMixing));
    });
  }
}
