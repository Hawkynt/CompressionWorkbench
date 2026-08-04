using System.Text;
using Compression.Core.Dictionary.Lzmw;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzmwBuildingBlockTests {

  private static readonly LzmwBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x7F];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x5A);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  /// <summary>
  /// A strictly-alternating "abab..." stream is where LZW's own dictionary-update rule hits its
  /// classic "code not yet in the dictionary" (KwKwK) ambiguity, because LZW's new entry depends
  /// on a raw next character the decoder has not seen yet. LZMW's new entry is always the
  /// concatenation of two matches the decoder has ALREADY resolved, so no such ambiguity is
  /// possible here — this test exercises exactly the self-overlapping pattern that would trigger
  /// it in LZW, to demonstrate LZMW's decoder handles it via ordinary dictionary lookups alone.
  /// </summary>
  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AlternatingPattern_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ab", 2000)));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xBA1E);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(
      "LZMW is a Miller-Wegman variant of LZW. LZMW is a Miller-Wegman variant of LZW.");
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedRepeatsAndRandom_RoundTrips() {
    var rng = new Random(11);
    var parts = new List<byte>();
    for (var i = 0; i < 10; ++i) {
      var block = new byte[400];
      if (i % 2 == 0) Array.Fill(block, (byte)i);
      else rng.NextBytes(block);
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// LZMW's dictionary grows by whole matches per step (not one byte, like LZW), so it fills —
  /// and must reset via a clear code — far sooner than LZW's for the same input. Pinning
  /// <c>maxBits</c> to its floor (9, i.e. only 254 usable entries beyond the 256 singles) forces
  /// several resets within a modest input, directly exercising the clear-code path.
  /// </summary>
  [Test, Category("Boundary"), Category("RoundTrip")]
  public void LargeRepetitiveInput_ForcesDictionaryResetAndRoundTrips() {
    var unit = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. ";
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(unit, 400)));

    using var ms = new MemoryStream();
    new LzmwEncoder(ms, minBits: 9, maxBits: 9).Encode(data);
    var compressed = ms.ToArray();

    using var input = new MemoryStream(compressed);
    var round = new LzmwDecoder(input, minBits: 9, maxBits: 9).Decode(data.Length);

    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Lzmw"));
      Assert.That(Bb.DisplayName, Is.EqualTo("LZMW"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
