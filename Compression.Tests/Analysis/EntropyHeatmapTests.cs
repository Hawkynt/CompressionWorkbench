#pragma warning disable CS1591
using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public class EntropyHeatmapTests {

  [Test]
  public void AllZeroes_IsZeroEntropy() {
    var data = new byte[4096];
    var entropy = EntropyHeatmap.ShannonEntropy(data);
    Assert.That(entropy, Is.EqualTo(0.0).Within(1e-9));
  }

  [Test]
  public void UniformDistribution_IsMaxEntropy() {
    // 256 bytes, each value occurring exactly once → 8 bits per byte.
    var data = new byte[256];
    for (var i = 0; i < 256; ++i) data[i] = (byte)i;
    var entropy = EntropyHeatmap.ShannonEntropy(data);
    Assert.That(entropy, Is.EqualTo(8.0).Within(1e-9));
  }

  [Test]
  public void ComputeProducesPerWindowSamples() {
    var data = new byte[16_384];  // 4 windows of 4096
    var samples = EntropyHeatmap.Compute(data);
    Assert.That(samples, Has.Count.EqualTo(4));
    Assert.That(samples[0].OffsetBytes, Is.EqualTo(0));
    Assert.That(samples[1].OffsetBytes, Is.EqualTo(4096));
  }

  [Test]
  public void FindHighEntropyRegions_FlagsRandomBlob() {
    // Low-entropy region followed by random-bytes region followed by low-entropy.
    var data = new byte[12_288];
    var rng = new Random(42);
    rng.NextBytes(data.AsSpan(4096, 4096));  // high-entropy middle
    var samples = EntropyHeatmap.Compute(data);
    var regions = EntropyHeatmap.FindHighEntropyRegions(samples, threshold: 7.0);
    Assert.That(regions, Has.Count.EqualTo(1));
    Assert.That(regions[0].Start, Is.EqualTo(4096));
  }

  [Test]
  public void RenderAscii_ProducesNonEmptyString() {
    var data = new byte[16_384];
    new Random(1).NextBytes(data);
    var samples = EntropyHeatmap.Compute(data);
    var ascii = EntropyHeatmap.RenderAscii(samples);
    Assert.That(ascii, Is.Not.Empty);
  }
}
