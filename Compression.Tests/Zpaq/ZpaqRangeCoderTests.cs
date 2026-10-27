using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// Pins the safety invariant of the ZPAQ binary range coder: on entry to every bit
/// the range lies in [2^24, 2^32) and the probability in [1, 65535], and both
/// subranges are then at least 256 wide, so the interval can never collapse to
/// zero width and leave the coder unable to distinguish the two bits.
/// </summary>
[TestFixture]
public class ZpaqRangeCoderTests {

  private const uint RangeMin = ZpaqRangeCoder.RangeMinimum;
  private const uint MinimumSubrangeWidth = 256;

  /// <summary>Ranges that bracket every interesting case of the split arithmetic.</summary>
  private static IEnumerable<uint> InterestingRanges() {
    yield return RangeMin;              // smallest range the invariant allows
    yield return RangeMin + 1;
    yield return RangeMin * 2;
    yield return 0x7FFFFFFFu;
    yield return 0x80000000u;
    yield return 0xFFFF0000u;
    yield return 0xFFFFFFFFu;           // largest range, the initial one
    var rng = new Random(0x5EED);
    for (var i = 0; i < 512; ++i)
      yield return RangeMin + (uint)rng.NextInt64(0, 0xFFFFFFFFL - RangeMin + 1);
  }

  /// <summary>Probabilities that bracket every interesting case of the split arithmetic.</summary>
  private static IEnumerable<int> InterestingProbabilities() {
    yield return ZpaqRangeCoder.MinimumProbability;
    yield return 2;
    yield return 255;
    yield return 256;
    yield return 32768;
    yield return 65534;
    yield return ZpaqRangeCoder.MaximumProbability;
    var rng = new Random(0xC0FFEE);
    for (var i = 0; i < 64; ++i)
      yield return rng.Next(1, 65536);
  }

  // ── The invariant itself ─────────────────────────────────────────────────

  [Test, Category("BoundaryCase")]
  public void Split_LeavesBothSubrangesAtLeast256Wide() {
    foreach (var range in InterestingRanges())
      foreach (var probability in InterestingProbabilities()) {
        var bound = ZpaqRangeCoder.Split(range, probability);
        Assert.That(
          bound,
          Is.GreaterThanOrEqualTo(MinimumSubrangeWidth),
          $"the 1 subrange collapsed at range={range}, p={probability}");
        Assert.That(
          range - bound,
          Is.GreaterThanOrEqualTo(MinimumSubrangeWidth),
          $"the 0 subrange collapsed at range={range}, p={probability}");
        Assert.That(bound, Is.LessThan(range));
      }
  }

  [Test, Category("EdgeCase")]
  public void Split_ClampsProbabilitiesOutsideTheSafeBand() {
    // A model that produced 0 or 65536 would otherwise put the split on an end of
    // the interval. The clamp is what makes the invariant hold unconditionally.
    foreach (var range in new uint[] { RangeMin, 0xFFFFFFFFu }) {
      Assert.That(ZpaqRangeCoder.Split(range, 0), Is.EqualTo(ZpaqRangeCoder.Split(range, 1)));
      Assert.That(ZpaqRangeCoder.Split(range, -1000), Is.EqualTo(ZpaqRangeCoder.Split(range, 1)));
      Assert.That(ZpaqRangeCoder.Split(range, 65536), Is.EqualTo(ZpaqRangeCoder.Split(range, 65535)));
      Assert.That(ZpaqRangeCoder.Split(range, int.MaxValue), Is.EqualTo(ZpaqRangeCoder.Split(range, 65535)));
    }
  }

  [Test, Category("BoundaryCase")]
  public void Encoder_KeepsTheRangeNormalisedAfterEveryBit() {
    // Coding the least likely bit every time is the fastest way to shrink the
    // range, so it is the case that would expose a renormalisation that does not
    // actually restore the invariant.
    using var output = new MemoryStream();
    var coder = new ZpaqRangeEncoder(output);
    for (var i = 0; i < 20000; ++i) {
      var probability = i % 2 == 0 ? 1 : 65535;
      var bit = i % 2 == 0 ? 1 : 0; // always the improbable one
      coder.EncodeBit(bit, probability);
      Assert.That(coder.Range, Is.GreaterThanOrEqualTo(RangeMin), $"range collapsed after bit {i}");
    }
    coder.Flush();
  }

  [Test, Category("BoundaryCase")]
  public void Decoder_KeepsTheRangeNormalisedAndReproducesEveryBit() {
    var bits = new int[20000];
    var probabilities = new int[bits.Length];
    var rng = new Random(0xBEEF);
    for (var i = 0; i < bits.Length; ++i) {
      // Weight heavily towards the extremes, where an interval collapse would show.
      probabilities[i] = (i % 3) switch {
        0 => 1,
        1 => 65535,
        _ => rng.Next(1, 65536),
      };
      bits[i] = rng.Next(2);
    }

    using var output = new MemoryStream();
    var encoder = new ZpaqRangeEncoder(output);
    for (var i = 0; i < bits.Length; ++i)
      encoder.EncodeBit(bits[i], probabilities[i]);
    encoder.Flush();

    var decoder = new ZpaqRangeDecoder(output.ToArray(), 0);
    for (var i = 0; i < bits.Length; ++i) {
      var bit = decoder.DecodeBit(probabilities[i]);
      Assert.That(bit, Is.EqualTo(bits[i]), $"bit {i} decoded wrongly");
      Assert.That(decoder.Range, Is.GreaterThanOrEqualTo(RangeMin), $"range collapsed after bit {i}");
    }
  }

  // ── Coding behaviour ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ConfidentPredictionsCostAlmostNothing() {
    // The lower subrange belongs to a 1 bit, so a confident 1 must be cheap. If the
    // convention were reversed the stream would grow instead of shrinking.
    using var output = new MemoryStream();
    var coder = new ZpaqRangeEncoder(output);
    for (var i = 0; i < 100000; ++i)
      coder.EncodeBit(1, 65535);
    coder.Flush();
    Assert.That(output.Length, Is.LessThan(100000 / 8));
  }

  [Test, Category("HappyPath")]
  public void RandomBitsAtOneHalfCostAboutOneBitEach() {
    const int Count = 80000;
    var rng = new Random(7);
    var bits = new int[Count];
    for (var i = 0; i < Count; ++i)
      bits[i] = rng.Next(2);

    using var output = new MemoryStream();
    var encoder = new ZpaqRangeEncoder(output);
    foreach (var bit in bits)
      encoder.EncodeBit(bit, 32768);
    encoder.Flush();

    Assert.That(output.Length, Is.InRange(Count / 8, Count / 8 + 32));

    var decoder = new ZpaqRangeDecoder(output.ToArray(), 0);
    foreach (var bit in bits)
      Assert.That(decoder.DecodeBit(32768), Is.EqualTo(bit));
  }

  [Test, Category("BoundaryCase")]
  public void Flush_ResolvesEveryDeferredByteAcrossAllTailStates() {
    // The flush is the subtlest part of a carry-counting coder: when the last bytes
    // of low are 0xFF they are only counted, not written, and they must still all
    // come out. Short messages with extreme probabilities are what put the coder
    // into those tail states, so sweep many of them rather than reasoning about one.
    var rng = new Random(0xF1005);
    for (var trial = 0; trial < 5000; ++trial) {
      var count = rng.Next(1, 65);
      var bits = new int[count];
      var probabilities = new int[count];
      for (var i = 0; i < count; ++i) {
        probabilities[i] = rng.Next(4) switch {
          0 => 1,
          1 => 65535,
          2 => 32768,
          _ => rng.Next(1, 65536),
        };
        bits[i] = rng.Next(2);
      }

      using var output = new MemoryStream();
      var encoder = new ZpaqRangeEncoder(output);
      for (var i = 0; i < count; ++i)
        encoder.EncodeBit(bits[i], probabilities[i]);
      encoder.Flush();

      var decoder = new ZpaqRangeDecoder(output.ToArray(), 0);
      for (var i = 0; i < count; ++i)
        Assert.That(
          decoder.DecodeBit(probabilities[i]),
          Is.EqualTo(bits[i]),
          $"trial {trial}, bit {i} of {count} did not survive the flush");
    }
  }

  [Test, Category("EdgeCase")]
  public void EmptyStream_FlushesFiveBytesAndTheFirstIsTheEmptyCache() {
    using var output = new MemoryStream();
    var coder = new ZpaqRangeEncoder(output);
    coder.Flush();
    var bytes = output.ToArray();
    Assert.That(bytes, Has.Length.EqualTo(ZpaqRangeCoder.FlushBytes));
    Assert.That(bytes[0], Is.Zero);
  }

  // ── Exceptional input ────────────────────────────────────────────────────

  [Test, Category("Exception")]
  public void Encoder_NullStream_Throws() =>
    Assert.That(() => new ZpaqRangeEncoder(null!), Throws.ArgumentNullException);

  [Test, Category("Exception")]
  public void Decoder_NullBuffer_Throws() =>
    Assert.That(() => new ZpaqRangeDecoder(null!, 0), Throws.ArgumentNullException);

  [Test, Category("EdgeCase")]
  public void Decoder_TruncatedStream_PadsWithZeroesInsteadOfFailing() {
    // A decoder that ran off the end must stay deterministic rather than throw,
    // because the last few bits of a message are pinned by the flush.
    var decoder = new ZpaqRangeDecoder([], 0);
    Assert.That(() => {
      for (var i = 0; i < 1000; ++i)
        decoder.DecodeBit(32768);
    }, Throws.Nothing);
    Assert.That(decoder.Range, Is.GreaterThanOrEqualTo(RangeMin));
  }
}
