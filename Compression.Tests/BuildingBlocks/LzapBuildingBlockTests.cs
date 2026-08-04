using System.Text;
using Compression.Core.Dictionary.Lzap;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzapBuildingBlockTests {

  private static readonly LzapBuildingBlock Bb = new();

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
  /// on a raw next character the decoder has not seen yet. Every LZAP entry is the previous match
  /// concatenated with a PREFIX of the current match — and the current match's bytes are always
  /// already fully resolved by the time a prefix of it is used — so no such ambiguity is possible
  /// here. This test exercises exactly the self-overlapping pattern that would trigger it in LZW.
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
      "LZAP adds every prefix of the current match. LZAP adds every prefix of the current match.");
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
  /// LZAP adds one new entry per prefix of the current match — often dozens or hundreds per
  /// step on repetitive input — so its dictionary fills, and must reset via a clear code, far
  /// sooner than LZW's or even LZMW's for the same input. Pinning <c>maxBits</c> to its floor (9)
  /// forces several resets within a modest input, directly exercising the clear-code path,
  /// including the case where a batch of prefix-insertions is cut short mid-batch by the reset.
  /// </summary>
  [Test, Category("Boundary"), Category("RoundTrip")]
  public void LargeRepetitiveInput_ForcesDictionaryResetAndRoundTrips() {
    var unit = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. ";
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(unit, 400)));

    using var ms = new MemoryStream();
    new LzapEncoder(ms, minBits: 9, maxBits: 9).Encode(data);
    var compressed = ms.ToArray();

    using var input = new MemoryStream(compressed);
    var round = new LzapDecoder(input, minBits: 9, maxBits: 9).Decode(data.Length);

    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// A large, highly self-similar input (all zero bytes) drives LZAP's per-step entry count into
  /// the hundreds, exercising the amortized O(1)-per-prefix trie-extension path and the mid-batch
  /// reset path together at the building block's real 9..12-bit default configuration.
  /// </summary>
  [Test, Category("Boundary"), Category("RoundTrip")]
  public void LargeHighlySimilarInput_RoundTrips() {
    var data = new byte[256 * 1024];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Lzap"));
      Assert.That(Bb.DisplayName, Is.EqualTo("LZAP"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
