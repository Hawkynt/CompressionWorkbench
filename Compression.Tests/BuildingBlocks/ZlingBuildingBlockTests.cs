using System.Text;
using Compression.Core.Dictionary.Zling;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ZlingBuildingBlockTests {

  private static readonly ZlingBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
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
  public void Repetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x37);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    // Short inputs don't shrink: the canonical Huffman header alone (256 code-length
    // bytes + two 4-byte length fields) outweighs a ~130-byte payload. Compression
    // headroom is exercised by Repetitive_RoundTripsAndCompresses instead.
    var data = Encoding.ASCII.GetBytes(
      "Zling pairs an LZ77 dictionary stage with Huffman entropy coding. Zling pairs an LZ77 dictionary stage with Huffman entropy coding.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void RepeatingBlock_RoundTrips() {
    var rng = new Random(0x9);
    var block = new byte[1500];
    rng.NextBytes(block);
    var data = new byte[block.Length * 6];
    for (var i = 0; i < 6; ++i)
      block.CopyTo(data, i * block.Length);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Zling"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Zling"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
