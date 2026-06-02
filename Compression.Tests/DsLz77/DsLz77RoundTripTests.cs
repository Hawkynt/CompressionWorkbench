using System.Text;
using Compression.Core.Dictionary.DsLz77;

namespace Compression.Tests.DsLz77;

/// <summary>
/// Round-trip tests for the DS LZ77 building block across the three effort
/// tiers (0 = greedy, 1 = lazy, 2 = iterated). All effort levels must
/// produce a bit stream the canonical DS LZ77 decoder can decode back to
/// the exact input.
/// </summary>
[TestFixture]
public class DsLz77RoundTripTests {

  // =========================================================================
  //                              Round-trip
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void RoundTrip_Random_4KiB_Effort0() {
    var data = new byte[4096];
    new Random(1).NextBytes(data);
    var compressed = DsLz77Compressor.Compress(data, effort: 0);
    Assert.That(DsLz77Decompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_Random_4KiB_Effort1() {
    var data = new byte[4096];
    new Random(2).NextBytes(data);
    var compressed = DsLz77Compressor.Compress(data, effort: 1);
    Assert.That(DsLz77Decompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_Random_4KiB_Effort2() {
    var data = new byte[4096];
    new Random(3).NextBytes(data);
    var compressed = DsLz77Compressor.Compress(data, effort: 2);
    Assert.That(DsLz77Decompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AllZeros_AllEfforts() {
    var data = new byte[4096];
    for (var e = 0; e <= 2; ++e) {
      var c = DsLz77Compressor.Compress(data, effort: e);
      Assert.That(DsLz77Decompressor.Decompress(c), Is.EqualTo(data),
        $"effort {e}: zero-fill round-trip mismatch");
      Assert.That(c.Length, Is.LessThan(data.Length / 4),
        $"effort {e}: all-zero input should compress aggressively");
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AsciiText_AllEfforts() {
    var phrase = "The quick brown fox jumps over the lazy dog. ";
    var sb = new StringBuilder(phrase.Length * 200);
    for (var i = 0; i < 200; ++i) sb.Append(phrase);
    var data = Encoding.ASCII.GetBytes(sb.ToString());

    for (var e = 0; e <= 2; ++e) {
      var c = DsLz77Compressor.Compress(data, effort: e);
      Assert.That(DsLz77Decompressor.Decompress(c), Is.EqualTo(data),
        $"effort {e}: repetitive-text round-trip mismatch");
      Assert.That(c.Length, Is.LessThan(data.Length),
        $"effort {e}: repetitive input must shrink");
    }
  }

  // =========================================================================
  //                              Edge cases
  // =========================================================================

  [Test, Category("EdgeCase")]
  public void Empty_RoundTrip() {
    var c = DsLz77Compressor.Compress([], effort: 1);
    Assert.That(DsLz77Decompressor.Decompress(c), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void SingleByte_RoundTrip() {
    var data = new byte[] { 0x42 };
    var c = DsLz77Compressor.Compress(data, effort: 2);
    Assert.That(DsLz77Decompressor.Decompress(c), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void NegativeEffort_ClampsToZero() {
    // Equivalence class: a negative effort should not throw — it clamps to
    // the floor of the documented range (greedy).
    var data = Encoding.ASCII.GetBytes(new string('A', 1024));
    var c = DsLz77Compressor.Compress(data, effort: -5);
    Assert.That(DsLz77Decompressor.Decompress(c), Is.EqualTo(data));
  }

  // =========================================================================
  //                       Effort tier monotonicity
  // =========================================================================

  [Test, Category("EffortTier")]
  public void Effort1_NotWorseThanEffort0_OnCompressibleInput() {
    var phrase = "DoubleSpace LZ77 lazy parse rules. ";
    var sb = new StringBuilder(phrase.Length * 200);
    for (var i = 0; i < 200; ++i) sb.Append(phrase);
    var data = Encoding.ASCII.GetBytes(sb.ToString());

    var c0 = DsLz77Compressor.Compress(data, effort: 0);
    var c1 = DsLz77Compressor.Compress(data, effort: 1);

    Assert.That(c1.Length, Is.LessThanOrEqualTo(c0.Length),
      "effort 1 (lazy) must not be larger than effort 0 (greedy) on compressible input");
  }

  [Test, Category("EffortTier")]
  public void Effort2_NotWorseThanEffort1() {
    var phrase = "Iterated DS LZ77 keeps the best of several parses. ";
    var sb = new StringBuilder(phrase.Length * 200);
    for (var i = 0; i < 200; ++i) sb.Append(phrase);
    var data = Encoding.ASCII.GetBytes(sb.ToString());

    var c1 = DsLz77Compressor.Compress(data, effort: 1);
    var c2 = DsLz77Compressor.Compress(data, effort: 2);

    Assert.That(c2.Length, Is.LessThanOrEqualTo(c1.Length),
      "effort 2 (iterated) keeps the best result so must not exceed effort 1");
  }

  // =========================================================================
  //                       Building block plumbing
  // =========================================================================

  [Test, Category("HappyPath")]
  public void BuildingBlock_Descriptor() {
    var bb = new BB_DsLz77();
    Assert.That(bb.Id, Is.EqualTo("BB_DsLz77"));
    Assert.That(bb.DisplayName, Does.Contain("DS LZ77"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  [Test, Category("RoundTrip")]
  public void BuildingBlock_RoundTrip() {
    var bb = new BB_DsLz77();
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 100)));
    Assert.That(bb.Decompress(bb.Compress(data)), Is.EqualTo(data));
  }
}
