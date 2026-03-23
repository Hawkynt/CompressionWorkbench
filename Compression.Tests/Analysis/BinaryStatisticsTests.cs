using Compression.Analysis.Statistics;

namespace Compression.Tests.Analysis;

[TestFixture]
public class BinaryStatisticsTests {

  [Test, Category("HappyPath")]
  public void Entropy_AllSameByte_ReturnsZero() {
    var data = new byte[256];
    Array.Fill(data, (byte)0xAA);
    var entropy = BinaryStatistics.ComputeEntropy(data);
    Assert.That(entropy, Is.EqualTo(0.0).Within(0.001));
  }

  [Test, Category("HappyPath")]
  public void Entropy_UniformDistribution_ReturnsEight() {
    // Each byte value appears exactly once
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    var entropy = BinaryStatistics.ComputeEntropy(data);
    Assert.That(entropy, Is.EqualTo(8.0).Within(0.001));
  }

  [Test, Category("HappyPath")]
  public void Entropy_TwoValues_ReturnsOne() {
    // Half 0x00, half 0x01 => 1 bit/byte
    var data = new byte[256];
    for (var i = 0; i < 128; i++) data[i] = 0;
    for (var i = 128; i < 256; i++) data[i] = 1;
    var entropy = BinaryStatistics.ComputeEntropy(data);
    Assert.That(entropy, Is.EqualTo(1.0).Within(0.001));
  }

  [Test, Category("HappyPath")]
  public void ChiSquare_Uniform_IsLow() {
    var data = new byte[256 * 100];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
    var chiSq = BinaryStatistics.ComputeChiSquare(data);
    Assert.That(chiSq, Is.EqualTo(0.0).Within(0.001));
  }

  [Test, Category("HappyPath")]
  public void ChiSquare_AllSameByte_IsHigh() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0x42);
    var chiSq = BinaryStatistics.ComputeChiSquare(data);
    Assert.That(chiSq, Is.GreaterThan(1000));
  }

  [Test, Category("HappyPath")]
  public void Mean_AllZeros_ReturnsZero() {
    var data = new byte[100];
    Assert.That(BinaryStatistics.ComputeMean(data), Is.EqualTo(0.0));
  }

  [Test, Category("HappyPath")]
  public void Mean_Uniform_IsNear127() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    Assert.That(BinaryStatistics.ComputeMean(data), Is.EqualTo(127.5).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void SerialCorrelation_Random_IsNearZero() {
    var rng = new Random(42);
    var data = new byte[10000];
    rng.NextBytes(data);
    var sc = BinaryStatistics.ComputeSerialCorrelation(data);
    Assert.That(Math.Abs(sc), Is.LessThan(0.05));
  }

  [Test, Category("HappyPath")]
  public void SerialCorrelation_Sequential_IsHigh() {
    // Repeating 0,1,2,...,255 pattern has high serial correlation
    var data = new byte[2560];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
    var sc = BinaryStatistics.ComputeSerialCorrelation(data);
    Assert.That(sc, Is.GreaterThan(0.9));
  }

  [Test, Category("HappyPath")]
  public void MonteCarloPi_Random_IsNearPi() {
    var rng = new Random(42);
    var data = new byte[100000];
    rng.NextBytes(data);
    var pi = BinaryStatistics.ComputeMonteCarloPi(data);
    Assert.That(pi, Is.EqualTo(Math.PI).Within(0.1));
  }

  [Test, Category("HappyPath")]
  public void Analyze_ReturnsAllFields() {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var result = BinaryStatistics.Analyze(data);
    Assert.That(result.Entropy, Is.GreaterThan(7.0));
    Assert.That(result.ByteFrequency.Length, Is.EqualTo(256));
    Assert.That(result.UniqueBytesCount, Is.EqualTo(256));
    Assert.That(result.UniqueBigrams, Is.GreaterThan(0));
  }

  [Test, Category("EdgeCase")]
  public void Analyze_EmptyData_ReturnsDefaults() {
    var result = BinaryStatistics.Analyze(ReadOnlySpan<byte>.Empty);
    Assert.That(result.Entropy, Is.EqualTo(0));
    Assert.That(result.UniqueBytesCount, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void ByteFrequency_CountsCorrectly() {
    var data = new byte[] { 0, 0, 0, 1, 1, 2 };
    var freq = BinaryStatistics.ComputeByteFrequency(data);
    Assert.That(freq[0], Is.EqualTo(3));
    Assert.That(freq[1], Is.EqualTo(2));
    Assert.That(freq[2], Is.EqualTo(1));
    Assert.That(freq[3], Is.EqualTo(0));
  }
}
