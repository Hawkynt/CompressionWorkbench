using System.Text;
using Compression.Core.Entropy.ContextMixing.Bsc;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class BscBuildingBlockTests {

  private static readonly BscBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x77];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[2048];
    Array.Fill(data, (byte)0x22);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x5678);
    var data = new byte[512];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      "BSC sorts blocks and recodes them with Move-to-Front before a lightweight adaptive coder. " +
      "BSC sorts blocks and recodes them with Move-to-Front before a lightweight adaptive coder.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Bsc"));
      Assert.That(Bb.DisplayName, Is.EqualTo("BSC (reduced)"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.ContextMixing));
    });
  }
}
