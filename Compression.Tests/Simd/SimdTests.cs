using Compression.Core.Simd;

namespace Compression.Tests.Simd;

[TestFixture]
public class SimdTests {

  // --- SimdMatchLength tests ---

  [Test]
  public void MatchLength_FullMatch() {
    var data = new byte[] { 1, 2, 3, 4, 5, 1, 2, 3, 4, 5 };
    var result = SimdMatchLength.GetMatchLength(data, 0, 5, 5);
    Assert.That(result, Is.EqualTo(5));
  }

  [Test]
  public void MatchLength_NoMatch() {
    var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    var result = SimdMatchLength.GetMatchLength(data, 0, 5, 5);
    Assert.That(result, Is.EqualTo(0));
  }

  [Test]
  public void MatchLength_PartialMatch() {
    var data = new byte[] { 1, 2, 3, 4, 5, 1, 2, 3, 9, 9 };
    var result = SimdMatchLength.GetMatchLength(data, 0, 5, 5);
    Assert.That(result, Is.EqualTo(3));
  }

  [Test]
  public void MatchLength_ZeroLimit_ReturnsZero() {
    var data = new byte[] { 1, 2, 3, 1, 2, 3 };
    var result = SimdMatchLength.GetMatchLength(data, 0, 3, 0);
    Assert.That(result, Is.EqualTo(0));
  }

  [Test]
  public void MatchLength_LargeData_FullMatch() {
    // Test with data large enough to exercise SIMD path (>32 bytes)
    var data = new byte[256];
    for (var i = 0; i < 128; i++)
      data[i] = (byte)(i & 0xFF);
    // Copy the same pattern at offset 128
    Array.Copy(data, 0, data, 128, 128);

    var result = SimdMatchLength.GetMatchLength(data, 0, 128, 128);
    Assert.That(result, Is.EqualTo(128));
  }

  [Test]
  public void MatchLength_LargeData_PartialMatch() {
    var data = new byte[256];
    for (var i = 0; i < 128; i++)
      data[i] = (byte)(i & 0xFF);
    Array.Copy(data, 0, data, 128, 128);
    // Introduce a mismatch at offset 50
    data[128 + 50] = 0xFF;

    var result = SimdMatchLength.GetMatchLength(data, 0, 128, 128);
    Assert.That(result, Is.EqualTo(50));
  }

  [Test]
  public void MatchLength_TwoSpan_FullMatch() {
    var a = new byte[] { 10, 20, 30, 40, 50 };
    var b = new byte[] { 10, 20, 30, 40, 50 };
    var result = SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, 5);
    Assert.That(result, Is.EqualTo(5));
  }

  [Test]
  public void MatchLength_TwoSpan_NoMatch() {
    var a = new byte[] { 10, 20, 30 };
    var b = new byte[] { 99, 98, 97 };
    var result = SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, 3);
    Assert.That(result, Is.EqualTo(0));
  }

  [Test]
  public void MatchLength_TwoSpan_PartialMatch() {
    var a = new byte[] { 10, 20, 30, 40, 50 };
    var b = new byte[] { 10, 20, 99, 40, 50 };
    var result = SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, 5);
    Assert.That(result, Is.EqualTo(2));
  }

  [Test]
  public void MatchLength_TwoSpan_LargeData() {
    var a = new byte[200];
    var b = new byte[200];
    Array.Fill(a, (byte)0xAA);
    Array.Fill(b, (byte)0xAA);
    b[150] = 0xBB; // mismatch at position 150

    var result = SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, 200);
    Assert.That(result, Is.EqualTo(150));
  }

  // --- SimdHistogram tests ---

  [Test]
  public void Histogram_EmptyData_ReturnsZeroCounts() {
    var result = SimdHistogram.ComputeHistogram(ReadOnlySpan<byte>.Empty);
    Assert.That(result, Has.Length.EqualTo(256));
    Assert.That(result.Sum(), Is.EqualTo(0));
  }

  [Test]
  public void Histogram_SingleByteValue() {
    var data = new byte[] { 42 };
    var result = SimdHistogram.ComputeHistogram(data);
    Assert.That(result[42], Is.EqualTo(1));
    Assert.That(result.Where((v, i) => i != 42).Sum(), Is.EqualTo(0));
  }

  [Test]
  public void Histogram_AllSameByte() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xFF);
    var result = SimdHistogram.ComputeHistogram(data);
    Assert.That(result[0xFF], Is.EqualTo(1000));
    Assert.That(result.Where((v, i) => i != 0xFF).Sum(), Is.EqualTo(0));
  }

  [Test]
  public void Histogram_AllByteValues() {
    // Each byte value exactly once
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;
    var result = SimdHistogram.ComputeHistogram(data);
    for (var i = 0; i < 256; i++)
      Assert.That(result[i], Is.EqualTo(1), $"Byte value {i} should appear exactly once");
  }

  [Test]
  public void Histogram_KnownDistribution() {
    var data = new byte[] { 0, 0, 0, 1, 1, 2, 3, 3, 3, 3 };
    var result = SimdHistogram.ComputeHistogram(data);
    Assert.That(result[0], Is.EqualTo(3));
    Assert.That(result[1], Is.EqualTo(2));
    Assert.That(result[2], Is.EqualTo(1));
    Assert.That(result[3], Is.EqualTo(4));
    Assert.That(result.Sum(), Is.EqualTo(10));
  }

  [Test]
  public void Histogram_LargeData_MatchesManualCount() {
    // Large enough to exercise SIMD path (>128 bytes)
    var rng = new Random(77);
    var data = new byte[4096];
    rng.NextBytes(data);

    var simdResult = SimdHistogram.ComputeHistogram(data);

    // Manual count for verification
    var expected = new long[256];
    foreach (var b in data)
      expected[b]++;

    for (var i = 0; i < 256; i++)
      Assert.That(simdResult[i], Is.EqualTo(expected[i]),
        $"Mismatch at byte value {i}: SIMD={simdResult[i]}, manual={expected[i]}");
  }

  [Test]
  public void Entropy_UniformSingleByte_IsZero() {
    var data = new byte[100];
    Array.Fill(data, (byte)0x42);
    var entropy = SimdHistogram.ComputeEntropy(data);
    Assert.That(entropy, Is.EqualTo(0.0).Within(0.001));
  }

  [Test]
  public void Entropy_TwoEquallyDistributed_IsOne() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 2);
    var entropy = SimdHistogram.ComputeEntropy(data);
    Assert.That(entropy, Is.EqualTo(1.0).Within(0.01));
  }

  [Test]
  public void Entropy_Empty_IsZero() {
    var entropy = SimdHistogram.ComputeEntropy(ReadOnlySpan<byte>.Empty);
    Assert.That(entropy, Is.EqualTo(0.0));
  }

  // --- SimdRunScan tests ---

  [Test]
  public void RunLength_AllSame() {
    var data = new byte[100];
    Array.Fill(data, (byte)0xAA);
    var result = SimdRunScan.GetRunLength(data, 0, 100);
    Assert.That(result, Is.EqualTo(100));
  }

  [Test]
  public void RunLength_NoRun() {
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var result = SimdRunScan.GetRunLength(data, 0, 5);
    Assert.That(result, Is.EqualTo(1));
  }

  [Test]
  public void RunLength_PartialRun() {
    var data = new byte[] { 0xAA, 0xAA, 0xAA, 0xBB, 0xBB };
    var result = SimdRunScan.GetRunLength(data, 0, 5);
    Assert.That(result, Is.EqualTo(3));
  }

  [Test]
  public void RunLength_FromMiddle() {
    var data = new byte[] { 1, 2, 2, 2, 2, 3 };
    var result = SimdRunScan.GetRunLength(data, 1, 5);
    Assert.That(result, Is.EqualTo(4));
  }

  [Test]
  public void RunLength_MaxRunCap() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xFF);
    var result = SimdRunScan.GetRunLength(data, 0, 10);
    Assert.That(result, Is.EqualTo(10));
  }

  [Test]
  public void RunLength_PastEnd_ReturnsZero() {
    var data = new byte[] { 1, 2, 3 };
    var result = SimdRunScan.GetRunLength(data, 10, 5);
    Assert.That(result, Is.EqualTo(0));
  }

  [Test]
  public void RunLength_LargeRun_ExercisesSimd() {
    // 256 bytes of same value — enough to exercise SIMD path
    var data = new byte[256];
    Array.Fill(data, (byte)0x42);
    data[200] = 0x99; // break the run at position 200
    var result = SimdRunScan.GetRunLength(data, 0, 256);
    Assert.That(result, Is.EqualTo(200));
  }

  [Test]
  public void FindAllRuns_SimpleData() {
    var data = new byte[] { 1, 1, 1, 2, 2, 3 };
    var runs = SimdRunScan.FindAllRuns(data);
    Assert.That(runs, Has.Count.EqualTo(3));
    Assert.That(runs[0], Is.EqualTo((0, 3, (byte)1)));
    Assert.That(runs[1], Is.EqualTo((3, 2, (byte)2)));
    Assert.That(runs[2], Is.EqualTo((5, 1, (byte)3)));
  }

  [Test]
  public void FindAllRuns_AllDifferent() {
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var runs = SimdRunScan.FindAllRuns(data);
    Assert.That(runs, Has.Count.EqualTo(5));
    foreach (var (_, len, _) in runs)
      Assert.That(len, Is.EqualTo(1));
  }

  [Test]
  public void FindAllRuns_Empty() {
    var runs = SimdRunScan.FindAllRuns(ReadOnlySpan<byte>.Empty);
    Assert.That(runs, Has.Count.EqualTo(0));
  }

  // --- SimdMemCopy tests ---

  [Test]
  public void MemCopy_SmallCopy() {
    var src = new byte[] { 10, 20, 30, 40, 50 };
    var dst = new byte[10];
    SimdMemCopy.Copy(src, 0, dst, 2, 5);
    Assert.That(dst[2..7], Is.EqualTo(src));
  }

  [Test]
  public void MemCopy_LargeCopy() {
    var rng = new Random(123);
    var src = new byte[512];
    rng.NextBytes(src);
    var dst = new byte[512];

    SimdMemCopy.Copy(src, 0, dst, 0, 512);
    Assert.That(dst, Is.EqualTo(src));
  }

  [Test]
  public void MemCopy_WithOffsets() {
    var src = new byte[] { 0, 0, 1, 2, 3, 4, 5, 0, 0 };
    var dst = new byte[10];
    SimdMemCopy.Copy(src, 2, dst, 3, 5);
    Assert.That(dst[3..8], Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test]
  public void MemCopy_ZeroLength_DoesNothing() {
    var src = new byte[] { 1, 2, 3 };
    var dst = new byte[3];
    SimdMemCopy.Copy(src, 0, dst, 0, 0);
    Assert.That(dst, Is.EqualTo(new byte[] { 0, 0, 0 }));
  }

  [Test]
  public void MemCopy_ExactlyOneVector() {
    // 32 bytes = exactly one Vector256 width
    var src = new byte[32];
    for (var i = 0; i < 32; i++)
      src[i] = (byte)(i + 1);
    var dst = new byte[32];
    SimdMemCopy.Copy(src, 0, dst, 0, 32);
    Assert.That(dst, Is.EqualTo(src));
  }

  [Test]
  public void Fill_SmallFill() {
    var dst = new byte[10];
    SimdMemCopy.Fill(dst, 2, 5, 0xAB);
    Assert.That(dst[0..2], Is.EqualTo(new byte[] { 0, 0 }));
    Assert.That(dst[2..7], Is.All.EqualTo((byte)0xAB));
    Assert.That(dst[7..10], Is.EqualTo(new byte[] { 0, 0, 0 }));
  }

  [Test]
  public void Fill_LargeFill() {
    var dst = new byte[512];
    SimdMemCopy.Fill(dst, 0, 512, 0x42);
    Assert.That(dst, Is.All.EqualTo((byte)0x42));
  }

  [Test]
  public void Fill_ZeroLength_DoesNothing() {
    var dst = new byte[] { 1, 2, 3 };
    SimdMemCopy.Fill(dst, 0, 0, 0xFF);
    Assert.That(dst, Is.EqualTo(new byte[] { 1, 2, 3 }));
  }
}
