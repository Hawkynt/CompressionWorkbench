using System.Security.Cryptography;
using FileFormat.Csc;
namespace Compression.Tests.Csc;

[TestFixture]
public class CscTests {

  private static byte[] Compress(byte[] data) {
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CscStream.Compress(input, compressed);
    return compressed.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var decompressed = new MemoryStream();
    CscStream.Decompress(input, decompressed);
    return decompressed.ToArray();
  }

  private static byte[] SeededRandom(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static void AssertRoundTrips(byte[] data, string context) {
    var round = Decompress(Compress(data));
    Assert.That(round.AsSpan().SequenceEqual(data), Is.True, $"round-trip differs ({context})");
  }

  /// <summary>
  /// Random content with a fresh seed per run, so every run explores new inputs. The seed is
  /// logged and part of the failure message: reproduce with <see cref="Random(int)"/>.
  /// </summary>
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(256)]
  [TestCase(4096)]
  [TestCase(65536)]
  public void RoundTrip(int size) {
    var seed = Random.Shared.Next();
    TestContext.Out.WriteLine($"size={size} seed={seed}");
    AssertRoundTrips(SeededRandom(size, seed), $"size={size}, seed={seed}");
  }

  /// <summary>
  /// Inputs that drove the carryless range coder's interval down to a handful of values while
  /// the bit-0 probability was low: range * prob / 4096 truncated to 0, bit 0 got an empty
  /// interval and the stream desynchronised ("Invalid CSC back-reference", or silently wrong
  /// output for 131072/4106). Each is the prefix of new Random(seed).NextBytes(new byte[65536 or
  /// 131072]); the lengths are the shortest prefixes that failed.
  /// </summary>
  [TestCase(12710, 3499)]
  [TestCase(16988, 2226)]
  [TestCase(57274, 5964)]
  [TestCase(65536, 2226)]
  [TestCase(65536, 3499)]
  [TestCase(65536, 5964)]
  [Category("Regression")]
  public void Given_InputCollapsingTheCoderInterval_When_RoundTripped_Then_BytesAreIdentical(int length, int seed) {
    var data = SeededRandom(Math.Max(length, 65536), seed)[..length];
    AssertRoundTrips(data, $"length={length}, seed={seed}");
  }

  [Test, Category("Regression")]
  public void Given_InputThatUsedToDecodeSilentlyWrong_When_RoundTripped_Then_BytesAreIdentical() {
    var data = SeededRandom(131072, 4106);
    AssertRoundTrips(data, "length=131072, seed=4106");
  }

  /// <summary>
  /// Streams that never collapse the coder interval are unaffected by the interval clamp:
  /// hashes captured from the encoder before it (random, and 3-bit skewed content).
  /// </summary>
  [TestCase(65536, 0, false, "F9C864A44EA841B76A00B0D4003713C9178881FB616D1F16C8F3BDBAC4A4F9CF")]
  [TestCase(4096, 1, true, "26CE826A1C4E822EC06B4D4A435EC592D7DC1F373EB7FE19FC2B4AE8FF315B2A")]
  [TestCase(200000, 3, true, "CC399519267B822F3A73D065D3E89F10FF0908B1EE5E8FD7552D0CBD648AA021")]
  [Category("Regression")]
  public void Given_InputNotCollapsingTheInterval_When_Compressed_Then_StreamIsUnchanged(int size, int seed, bool skewed, string sha256) {
    var data = SeededRandom(size, seed);
    if (skewed)
      for (var i = 0; i < data.Length; ++i)
        data[i] &= 0x07;

    Assert.That(Convert.ToHexString(SHA256.HashData(Compress(data))), Is.EqualTo(sha256));
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(257)]
  [TestCase(258)]
  [TestCase(259)]
  [TestCase(517)]
  [TestCase(65535)]
  [TestCase(65537)]
  [Category("Boundary")]
  public void Given_RunAroundMatchLengthAndWindowLimits_When_RoundTripped_Then_BytesAreIdentical(int length) {
    var data = new byte[length];
    Array.Fill(data, (byte)0x5A);
    AssertRoundTrips(data, $"run length={length}");
  }

  /// <summary>
  /// A random block repeated at exactly the maximum distance (the 65536-byte dictionary, coded
  /// as distance - 1 = 0xFFFF in 16 bits) and one byte beyond it, which must fall back to literals.
  /// </summary>
  [TestCase(65536)]
  [TestCase(65537)]
  [TestCase(65535)]
  [Category("Boundary")]
  public void Given_RepeatAtDictionaryDistance_When_RoundTripped_Then_BytesAreIdentical(int distance) {
    const int block = 300;
    var data = SeededRandom(distance + block, 7);
    Array.Copy(data, 0, data, distance, block);
    AssertRoundTrips(data, $"distance={distance}");
  }

  [TestCase(1 << 20, 11)]
  [TestCase(1 << 20, 12)]
  [Category("Boundary")]
  public void Given_SkewedAlphabet_When_RoundTripped_Then_BytesAreIdentical(int size, int seed) {
    // A 3-bit alphabet drives the bit probabilities to their extremes, the regime in which
    // the coder interval collapses.
    var data = SeededRandom(size, seed);
    for (var i = 0; i < data.Length; ++i)
      data[i] &= 0x07;
    AssertRoundTrips(data, $"skewed size={size}, seed={seed}");
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    Assert.That(Decompress(Compress(data)), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Header_Is14BytesMinimum() {
    // 10-byte property header + 4-byte uncompressed size = 14 bytes minimum
    var data = new byte[100];
    Assert.That(Compress(data).Length, Is.GreaterThan(14));
  }
}
