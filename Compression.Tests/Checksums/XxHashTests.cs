using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class XxHashTests {
  // --- XxHash64 ---

  [Category("ThemVsUs")]
  [Test]
  public void XxHash64_EmptyData() {
    var hash = XxHash64.Compute(ReadOnlySpan<byte>.Empty);
    Assert.That(hash, Is.EqualTo(0xEF46DB3751D8E999UL));
  }

  [Category("EdgeCase")]
  [Test]
  public void XxHash64_SingleByte() {
    // Verify single-byte hashing works (short path, < 32 bytes)
    var hash = XxHash64.Compute([0]);
    Assert.That(hash, Is.Not.EqualTo(0UL));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_DifferentInputs_DifferentHashes() {
    var hash1 = XxHash64.Compute("Hello"u8);
    var hash2 = XxHash64.Compute("World"u8);
    Assert.That(hash1, Is.Not.EqualTo(hash2));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_SameInput_SameHash() {
    var hash1 = XxHash64.Compute("Hello, World!"u8);
    var hash2 = XxHash64.Compute("Hello, World!"u8);
    Assert.That(hash1, Is.EqualTo(hash2));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_LargeInput() {
    // Input >= 32 bytes exercises the 4-accumulator path
    var data = new byte[128];
    new Random(42).NextBytes(data);
    var hash = XxHash64.Compute(data);
    Assert.That(hash, Is.Not.EqualTo(0UL));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_WithSeed() {
    var hash0 = XxHash64.Compute("test"u8, 0);
    var hash1 = XxHash64.Compute("test"u8, 1);
    Assert.That(hash0, Is.Not.EqualTo(hash1));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_Incremental_MatchesBatch() {
    var data = new byte[1000];
    new Random(42).NextBytes(data);

    var batch = XxHash64.Compute(data);

    var hasher = new XxHash64();
    hasher.Update(data.AsSpan(0, 100));
    hasher.Update(data.AsSpan(100, 500));
    hasher.Update(data.AsSpan(600, 400));

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_Incremental_SingleByteUpdates() {
    var data = new byte[64];
    new Random(99).NextBytes(data);

    var batch = XxHash64.Compute(data);

    var hasher = new XxHash64();
    foreach (var b in data)
      hasher.Update([b]);

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("EdgeCase")]
  [Test]
  public void XxHash64_Incremental_EmptyUpdates() {
    var hasher = new XxHash64();
    hasher.Update(ReadOnlySpan<byte>.Empty);
    hasher.Update(ReadOnlySpan<byte>.Empty);

    var batch = XxHash64.Compute(ReadOnlySpan<byte>.Empty);
    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash64_Reset_ProducesSameResult() {
    var data = new byte[200];
    new Random(7).NextBytes(data);

    var hasher = new XxHash64();
    hasher.Update(data);
    var first = hasher.Value;

    hasher.Reset();
    hasher.Update(data);
    var second = hasher.Value;

    Assert.That(second, Is.EqualTo(first));
  }

  [Category("Boundary")]
  [Test]
  public void XxHash64_Incremental_StraddleStripeSize() {
    // Test with 33 bytes: one full 32-byte stripe + 1 remaining
    var data = new byte[33];
    new Random(55).NextBytes(data);

    var batch = XxHash64.Compute(data);

    var hasher = new XxHash64();
    hasher.Update(data.AsSpan(0, 10));
    hasher.Update(data.AsSpan(10, 23));

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  // --- XxHash32 ---

  [Category("ThemVsUs")]
  [Test]
  public void XxHash32_EmptyData() {
    var hash = XxHash32.Compute(ReadOnlySpan<byte>.Empty);
    Assert.That(hash, Is.EqualTo(0x02CC5D05u));
  }

  [Category("EdgeCase")]
  [Test]
  public void XxHash32_SingleByte() {
    var hash = XxHash32.Compute([0]);
    Assert.That(hash, Is.Not.EqualTo(0u));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_DifferentInputs_DifferentHashes() {
    var hash1 = XxHash32.Compute("Hello"u8);
    var hash2 = XxHash32.Compute("World"u8);
    Assert.That(hash1, Is.Not.EqualTo(hash2));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_SameInput_SameHash() {
    var hash1 = XxHash32.Compute("Hello, World!"u8);
    var hash2 = XxHash32.Compute("Hello, World!"u8);
    Assert.That(hash1, Is.EqualTo(hash2));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_LargeInput() {
    var data = new byte[128];
    new Random(42).NextBytes(data);
    var hash = XxHash32.Compute(data);
    Assert.That(hash, Is.Not.EqualTo(0u));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_WithSeed() {
    var hash0 = XxHash32.Compute("test"u8, 0);
    var hash1 = XxHash32.Compute("test"u8, 1);
    Assert.That(hash0, Is.Not.EqualTo(hash1));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_Incremental_MatchesBatch() {
    var data = new byte[1000];
    new Random(42).NextBytes(data);

    var batch = XxHash32.Compute(data);

    var hasher = new XxHash32();
    hasher.Update(data.AsSpan(0, 100));
    hasher.Update(data.AsSpan(100, 500));
    hasher.Update(data.AsSpan(600, 400));

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_Incremental_SingleByteUpdates() {
    var data = new byte[32];
    new Random(99).NextBytes(data);

    var batch = XxHash32.Compute(data);

    var hasher = new XxHash32();
    foreach (var b in data)
      hasher.Update([b]);

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("EdgeCase")]
  [Test]
  public void XxHash32_Incremental_EmptyUpdates() {
    var hasher = new XxHash32();
    hasher.Update(ReadOnlySpan<byte>.Empty);
    hasher.Update(ReadOnlySpan<byte>.Empty);

    var batch = XxHash32.Compute(ReadOnlySpan<byte>.Empty);
    Assert.That(hasher.Value, Is.EqualTo(batch));
  }

  [Category("HappyPath")]
  [Test]
  public void XxHash32_Reset_ProducesSameResult() {
    var data = new byte[200];
    new Random(7).NextBytes(data);

    var hasher = new XxHash32();
    hasher.Update(data);
    var first = hasher.Value;

    hasher.Reset();
    hasher.Update(data);
    var second = hasher.Value;

    Assert.That(second, Is.EqualTo(first));
  }

  [Category("Boundary")]
  [Test]
  public void XxHash32_Incremental_StraddleStripeSize() {
    // Test with 17 bytes: one full 16-byte stripe + 1 remaining
    var data = new byte[17];
    new Random(55).NextBytes(data);

    var batch = XxHash32.Compute(data);

    var hasher = new XxHash32();
    hasher.Update(data.AsSpan(0, 5));
    hasher.Update(data.AsSpan(5, 12));

    Assert.That(hasher.Value, Is.EqualTo(batch));
  }
}
