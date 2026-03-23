using Compression.Analysis.Statistics;

namespace Compression.Tests.Analysis;

[TestFixture]
public class BoundaryDetectorTests {

  [Test, Category("HappyPath")]
  public void UniformData_NoBoundaries() {
    var rng = new Random(42);
    var data = new byte[8192];
    rng.NextBytes(data);
    var regions = BoundaryDetector.DetectBoundaries(data);
    Assert.That(regions.Count, Is.EqualTo(1));
    Assert.That(regions[0].Length, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void ClearTransition_FindsBoundary() {
    var data = new byte[8192];
    // First half: zeros. Second half: random.
    new Random(42).NextBytes(data.AsSpan(4096));
    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 256);
    Assert.That(regions.Count, Is.GreaterThanOrEqualTo(2));
    // First region should be low entropy
    Assert.That(regions[0].Entropy, Is.LessThan(2.0));
    // Last region should be high entropy
    Assert.That(regions[^1].Entropy, Is.GreaterThan(5.0));
  }

  [Test, Category("HappyPath")]
  public void MultipleTransitions_FindsAll() {
    // 0-2K: zeros, 2K-4K: random, 4K-6K: text-like, 6K-8K: random
    var data = new byte[8192];
    var rng = new Random(42);
    rng.NextBytes(data.AsSpan(2048, 2048));
    for (var i = 4096; i < 6144; i++) data[i] = (byte)('A' + (i % 26));
    rng.NextBytes(data.AsSpan(6144, 2048));

    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 256);
    Assert.That(regions.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void GradualTransition_DetectsApproximately() {
    var data = new byte[8192];
    var rng = new Random(42);
    // Gradient: first half increasing randomness
    for (var i = 0; i < data.Length; i++) {
      if (rng.NextDouble() < (double)i / data.Length)
        data[i] = (byte)rng.Next(256);
    }
    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 256);
    Assert.That(regions.Count, Is.GreaterThanOrEqualTo(1));
    // Should cover entire data
    Assert.That(regions.Sum(r => r.Length), Is.EqualTo(data.Length));
  }

  [Test, Category("EdgeCase")]
  public void SmallFile_FallsBackToFixedWindow() {
    var data = new byte[2048]; // < 4096
    new Random(42).NextBytes(data);
    var regions = BoundaryDetector.DetectBoundaries(data);
    Assert.That(regions.Count, Is.GreaterThanOrEqualTo(1));
    Assert.That(regions.Sum(r => r.Length), Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void TextThenCompressed_FindsEdge() {
    var data = new byte[8192];
    // First half: zeros (very low entropy)
    // Second half: high-entropy random
    new Random(42).NextBytes(data.AsSpan(4096));
    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 256);
    Assert.That(regions.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void LargeRandom_SingleRegion() {
    var rng = new Random(99);
    var data = new byte[16384];
    rng.NextBytes(data);
    var regions = BoundaryDetector.DetectBoundaries(data);
    // All random → should be 1 region (no significant boundaries)
    Assert.That(regions.Count, Is.LessThanOrEqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void CoversTotalLength() {
    var data = new byte[10000];
    new Random(42).NextBytes(data.AsSpan(5000));
    var regions = BoundaryDetector.DetectBoundaries(data, minRegionSize: 256);
    var totalCovered = regions.Sum(r => (long)r.Length);
    Assert.That(totalCovered, Is.EqualTo(data.Length));
  }

  // KL divergence tests
  [Test, Category("HappyPath")]
  public void KL_Identical_ReturnsZero() {
    var freq = new long[256];
    for (var i = 0; i < 256; i++) freq[i] = 100;
    var kl = BoundaryDetector.KullbackLeiblerDivergence(freq, 25600, freq, 25600);
    Assert.That(kl, Is.LessThan(0.001));
  }

  [Test, Category("HappyPath")]
  public void KL_Different_ReturnsPositive() {
    var freqP = new long[256];
    var freqQ = new long[256];
    // P: concentrated on low bytes
    for (var i = 0; i < 32; i++) freqP[i] = 800;
    // Q: concentrated on high bytes
    for (var i = 224; i < 256; i++) freqQ[i] = 800;
    var kl = BoundaryDetector.KullbackLeiblerDivergence(freqP, 25600, freqQ, 25600);
    Assert.That(kl, Is.GreaterThan(0.1));
  }

  // Chi-square tests
  [Test, Category("HappyPath")]
  public void ChiSq_Same_LowValue() {
    var rng = new Random(42);
    var data = new byte[2048];
    rng.NextBytes(data);
    var chiSq = BoundaryDetector.TwoSampleChiSquare(
      data.AsSpan(0, 1024), data.AsSpan(1024, 1024));
    Assert.That(chiSq, Is.LessThan(500));
  }

  [Test, Category("HappyPath")]
  public void ChiSq_Different_HighValue() {
    var sample1 = new byte[1024]; // All zeros
    var sample2 = new byte[1024];
    new Random(42).NextBytes(sample2);
    var chiSq = BoundaryDetector.TwoSampleChiSquare(sample1, sample2);
    Assert.That(chiSq, Is.GreaterThan(100));
  }
}
