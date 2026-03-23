using Compression.Analysis.Statistics;

namespace Compression.Tests.Analysis;

[TestFixture]
public class EntropyMapTests {

  [Test, Category("HappyPath")]
  public void Profile_UniformData_MergesIntoSingleRegion() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);
    var profiles = EntropyMap.Profile(data, 256);
    // All 4 windows are random → same classification → merged into 1
    Assert.That(profiles.Count, Is.EqualTo(1));
    Assert.That(profiles[0].Entropy, Is.GreaterThan(5.0));
    Assert.That(profiles[0].Length, Is.EqualTo(1024));
    Assert.That(profiles[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Profile_MixedData_DetectsTransition() {
    var data = new byte[512];
    // First 256: all zeros (low entropy)
    // Last 256: random (high entropy)
    new Random(42).NextBytes(data.AsSpan(256));
    var profiles = EntropyMap.Profile(data, 256);
    Assert.That(profiles.Count, Is.EqualTo(2));
    Assert.That(profiles[0].Entropy, Is.LessThan(1.0));
    Assert.That(profiles[1].Entropy, Is.GreaterThan(5.0));
  }

  [Test, Category("HappyPath")]
  public void Profile_SmallWindow_MergesConstantRegions() {
    var data = new byte[256];
    var profiles = EntropyMap.Profile(data, 64);
    // All 4 probes are zero → same classification → merged into 1
    Assert.That(profiles.Count, Is.EqualTo(1));
    Assert.That(profiles[0].Length, Is.EqualTo(256));
  }

  [Test, Category("HappyPath")]
  public void Profile_AlternatingData_PreservesTransitions() {
    // Create data with clear transitions: 256 zeros, 256 random, 256 zeros
    var data = new byte[768];
    new Random(42).NextBytes(data.AsSpan(256, 256));
    var profiles = EntropyMap.Profile(data, 256);
    Assert.That(profiles.Count, Is.EqualTo(3));
    Assert.That(profiles[0].Classification, Does.Contain("Constant"));
    Assert.That(profiles[1].Classification, Does.Not.Contain("Constant"));
    Assert.That(profiles[2].Classification, Does.Contain("Constant"));
  }

  [Test, Category("HappyPath")]
  public void Classify_ReturnsExpectedLabels() {
    Assert.That(EntropyMap.Classify(0.5), Does.Contain("Constant"));
    Assert.That(EntropyMap.Classify(3.0), Does.Contain("Plaintext"));
    Assert.That(EntropyMap.Classify(5.5), Does.Contain("Structured"));
    Assert.That(EntropyMap.Classify(7.0), Does.Contain("Dictionary"));
    Assert.That(EntropyMap.Classify(7.8), Does.Contain("Strong"));
    Assert.That(EntropyMap.Classify(7.99), Does.Contain("Encrypted"));
  }

  [Test, Category("EdgeCase")]
  public void Profile_EmptyData_ReturnsEmpty() {
    var profiles = EntropyMap.Profile(ReadOnlySpan<byte>.Empty);
    Assert.That(profiles, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Profile_CoversTotalLength() {
    var rng = new Random(99);
    var data = new byte[1000]; // Not aligned to window size
    rng.NextBytes(data);
    var profiles = EntropyMap.Profile(data, 256);
    var totalCovered = profiles.Sum(p => p.Length);
    Assert.That(totalCovered, Is.EqualTo(data.Length));
  }
}
