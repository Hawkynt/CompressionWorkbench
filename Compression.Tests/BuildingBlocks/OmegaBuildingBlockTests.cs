using System.Text;
using Compression.Core.Entropy;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class OmegaBuildingBlockTests {

  private static readonly OmegaBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    foreach (byte b in new byte[] { 0x00, 0x01, 0x41, 0x7F, 0xFF }) {
      var round = Bb.Decompress(Bb.Compress([b]));
      Assert.That(round, Is.EqualTo(new[] { b }).AsCollection);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTrips() {
    var data = new byte[500];
    Array.Fill(data, (byte)0x37);
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
  public void AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Omega"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Omega Coding"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Entropy));
    });
  }
}
