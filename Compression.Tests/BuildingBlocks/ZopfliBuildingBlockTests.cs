using System.IO.Compression;
using System.Text;
using Compression.Core.Deflate;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ZopfliBuildingBlockTests {

  private static readonly ZopfliBuildingBlock Bb = new();
  private static readonly DeflateBuildingBlock DeflateBb = new();

  /// <summary>
  /// Decompresses with .NET's own <see cref="DeflateStream"/> as an implementation-
  /// independent check that Zopfli's output is genuinely standard DEFLATE.
  /// </summary>
  private static byte[] DecompressWithSystem(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
    using var output = new MemoryStream();
    ds.CopyTo(output);
    return output.ToArray();
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
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
    var data = new byte[4096];
    Array.Fill(data, (byte)0x2B);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 8));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x707A6C69);
    var data = new byte[800];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "Zopfli searches many candidate parses before committing to a DEFLATE block. ", 20)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void Output_DecodesWithSystemDeflateStream() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. ", 50)));
    var compressed = Bb.Compress(data);
    var result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void SmallerThanOrEqualToPlainDeflate_OnTextSample() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "Pack my box with five dozen liquor jugs. The five boxing wizards jump quickly. ", 40)));

    var zopfliCompressed = Bb.Compress(data);
    var deflateCompressed = DeflateBb.Compress(data);

    Assert.That(zopfliCompressed.Length, Is.LessThanOrEqualTo(deflateCompressed.Length),
      $"Zopfli ({zopfliCompressed.Length} bytes) should be <= plain DEFLATE ({deflateCompressed.Length} bytes) " +
      $"on a {data.Length}-byte text sample.");

    var round = Bb.Decompress(zopfliCompressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Zopfli"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Zopfli"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
