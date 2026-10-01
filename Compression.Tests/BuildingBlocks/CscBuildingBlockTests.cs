using System.Text;
using Compression.Core.Dictionary.Csc;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class CscBuildingBlockTests {

  private static readonly CscBuildingBlock Bb = new();

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
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[2048];
    Array.Fill(data, (byte)0x33);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void OverlappingMatch_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("abababababababababababababababab");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xABCD);
    var data = new byte[512];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      "CSC combines LZ77 parsing with an LZMA-style range coder for its literal stream. " +
      "CSC combines LZ77 parsing with an LZMA-style range coder for its literal stream.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Csc"));
      Assert.That(Bb.DisplayName, Is.EqualTo("CSC"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }

  [Test, Category("Spec")]
  public void Payload_IsACompleteLibcscStream() {
    var data = Encoding.ASCII.GetBytes("building blocks carry the same libcsc stream the csc tool writes");
    var payload = Bb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(payload, Is.EqualTo(CscCodec.Compress(data)));
      Assert.That(CscStreamProperties.Read(payload).DictionarySize, Is.EqualTo(32u * 1024));
    });
  }
}
