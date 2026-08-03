using System.Text;
using Compression.Core.Dictionary.Lzav;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzavBuildingBlockTests {

  private static readonly LzavBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x5C];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x44);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xACE1);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(
      "LZAV is Aleksey Vaneev's fast in-memory LZ77 codec. LZAV is Aleksey Vaneev's fast in-memory LZ77 codec.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void LongLiteralRun_RoundTrips() {
    // Exceeds the 6-bit length field (63) to exercise the continuation-byte path.
    var rng = new Random(3);
    var data = new byte[400];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FarOffsetMatch_RoundTrips() {
    var rng = new Random(0x77);
    var block = new byte[5000];
    rng.NextBytes(block);
    var data = new byte[block.Length * 4];
    for (var i = 0; i < 4; ++i)
      block.CopyTo(data, i * block.Length);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Lzav"));
      Assert.That(Bb.DisplayName, Is.EqualTo("LZAV"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
