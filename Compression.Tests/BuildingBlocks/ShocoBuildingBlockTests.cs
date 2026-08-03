using System.Text;
using Compression.Core.Dictionary.Shoco;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ShocoBuildingBlockTests {

  private static readonly ShocoBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    foreach (byte b in new byte[] { 0x00, (byte)'e', (byte)'Z', 0xFF }) {
      var round = Bb.Decompress(Bb.Compress([b]));
      Assert.That(round, Is.EqualTo(new[] { b }).AsCollection);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(new string('e', 500));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[2048];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void MixedCaseAndPunctuation_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("Hello, World! 123 - Test.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void PlainAsciiText_DoesNotGrowMuch() {
    var data = Encoding.ASCII.GetBytes("the quick brown fox and the lazy dog");
    var compressed = Bb.Compress(data);
    // 4-byte length header plus payload should still be competitive for common text.
    Assert.That(compressed.Length, Is.LessThanOrEqualTo(data.Length + 4));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Shoco"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Shoco"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
