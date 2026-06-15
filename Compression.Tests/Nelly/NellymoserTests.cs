#pragma warning disable CS1591
using Codec.Nellymoser;

namespace Compression.Tests.Nelly;

/// <summary>
/// Structure/determinism tests for the Nellymoser decoder. There are no public test
/// vectors, so these pin the documented invariants of the FFmpeg port: 256 samples
/// per 64-byte block, deterministic output for a fixed seed (the noise sign comes
/// from the seeded lagged-Fibonacci RNG), bounded 16-bit amplitude, and tolerance of
/// truncated/ragged input.
/// </summary>
[TestFixture]
public class NellymoserTests {

  private const int BlockLen = 64;
  private const int SamplesPerBlock = 256;

  [Test]
  public void Decode_OneBlock_Produces256Samples() {
    var samples = NellymoserCodec.Decode(new byte[BlockLen], 22050);
    Assert.That(samples.Length, Is.EqualTo(SamplesPerBlock));
  }

  [Test]
  public void Decode_ThreeBlocks_Produces768Samples() {
    var samples = NellymoserCodec.Decode(new byte[BlockLen * 3], 22050);
    Assert.That(samples.Length, Is.EqualTo(SamplesPerBlock * 3));
  }

  [Test]
  public void Decode_EmptyInput_ReturnsEmpty() {
    Assert.That(NellymoserCodec.Decode([], 22050).Length, Is.EqualTo(0));
  }

  [Test]
  public void Decode_RaggedTail_DecodesOnlyWholeBlocks() {
    // 64 + 10 bytes → one whole block decoded, the 10 trailing bytes ignored.
    var samples = NellymoserCodec.Decode(new byte[BlockLen + 10], 22050);
    Assert.That(samples.Length, Is.EqualTo(SamplesPerBlock));
  }

  [Test]
  public void Decode_ZeroBlock_IsDeterministicAcrossRuns() {
    // The seeded RNG (seed 0) drives the noise sign, so two decodes of the same input
    // must agree bit-for-bit.
    var first = NellymoserCodec.Decode(new byte[BlockLen], 22050);
    var second = NellymoserCodec.Decode(new byte[BlockLen], 22050);
    Assert.That(second, Is.EqualTo(first));
  }

  [Test]
  public void Decode_AmplitudeStaysWithin16Bit() {
    var data = Enumerable.Range(0, BlockLen * 2).Select(i => (byte)(i * 53 + 7)).ToArray();
    var samples = NellymoserCodec.Decode(data, 22050);
    Assert.That(samples, Is.All.InRange((short)short.MinValue, (short)short.MaxValue));
    // The clip-to-int16 path can never overflow; assert it is also not stuck at silence
    // for a non-trivial input.
    Assert.That(samples.Any(x => x != 0), Is.True);
  }

  [Test]
  public void SampleRate_OnlyLabelsOutput_DoesNotChangeSamples() {
    var data = new byte[BlockLen];
    var at8k = NellymoserCodec.Decode(data, 8000);
    var at44k = NellymoserCodec.Decode(data, 44100);
    Assert.That(at44k, Is.EqualTo(at8k));
  }

  // ── table invariants ─────────────────────────────────────────────────────────

  [Test]
  public void Tables_HaveDocumentedSizes() {
    Assert.Multiple(() => {
      Assert.That(NellymoserTables.InitTable.Length, Is.EqualTo(64));
      Assert.That(NellymoserTables.DeltaTable.Length, Is.EqualTo(32));
      Assert.That(NellymoserTables.BandSizes.Length, Is.EqualTo(23));
      Assert.That(NellymoserTables.DequantizationTable.Length, Is.EqualTo(127));
    });
  }

  [Test]
  public void BandSizes_SumTo124_TheFillLength() {
    Assert.That(NellymoserTables.BandSizes.Sum(), Is.EqualTo(124));
  }
}
