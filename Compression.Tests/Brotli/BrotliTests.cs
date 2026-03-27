using System.IO.Compression;
using System.Text;
using Compression.Core.Dictionary.Brotli;

namespace Compression.Tests.Brotli;

[TestFixture]
public class BrotliTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 42 };
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ShortText() {
    var data = Encoding.UTF8.GetBytes("Hello, Brotli!");
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xAA);
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var data = new byte[10000];
    new Random(42).NextBytes(data);
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData_MultipleBlocks() {
    // > 65536 bytes forces multiple meta-blocks
    var data = new byte[100_000];
    new Random(99).NextBytes(data);
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ExactBlockBoundary() {
    var data = new byte[65536];
    new Random(7).NextBytes(data);
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var compressed = BrotliCompressor.Compress(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compress_Empty_ProducesValidStream() {
    var compressed = BrotliCompressor.Compress([]);
    // Should be a minimal valid Brotli stream (ISLAST + ISEMPTY)
    Assert.That(compressed.Length, Is.GreaterThan(0));
    Assert.That(compressed.Length, Is.LessThanOrEqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_ProducesValidOutput() {
    var data = Encoding.UTF8.GetBytes("test data for brotli compression");
    var compressed = BrotliCompressor.Compress(data);
    Assert.That(compressed.Length, Is.GreaterThan(0));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void StreamApi_RoundTrip() {
    var data = Encoding.UTF8.GetBytes("Stream API test for Brotli");
    var compressed = FileFormat.Brotli.BrotliStream.Compress(data);
    var decompressed = FileFormat.Brotli.BrotliStream.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void StreamApi_FromStream() {
    var data = Encoding.UTF8.GetBytes("Streaming Brotli data");
    using var inputStream = new MemoryStream(data);
    var compressed = FileFormat.Brotli.BrotliStream.Compress(inputStream);

    using var compressedStream = new MemoryStream(compressed);
    var decompressed = FileFormat.Brotli.BrotliStream.Decompress(compressedStream);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [TestCase("Zeroes")]
  [TestCase("Incrementing")]
  [TestCase("Alternating")]
  [TestCase("Repeating")]
  [TestCase("Text")]
  [TestCase("Random")]
  [TestCase("BinaryStruct")]
  public void StreamApi_RoundTrip_256KB(string pattern) {
    // 256KB — the benchmark size. Exercises the full descriptor path (CompressLz77 + own decompressor).
    const int size = 262144;
    var data = Make256KBPattern(pattern, size);
    using var inStream = new MemoryStream(data);
    var compressed = FileFormat.Brotli.BrotliStream.Compress(inStream);
    using var compStream = new MemoryStream(compressed);
    var decompressed = FileFormat.Brotli.BrotliStream.Decompress(compStream);
    Assert.That(decompressed.Length, Is.EqualTo(data.Length), "Size mismatch");
    for (var i = 0; i < data.Length; i++)
      if (decompressed[i] != data[i]) {
        Assert.Fail($"Mismatch at index {i}: expected {data[i]}, got {decompressed[i]}");
        break;
      }
  }

  private static byte[] Make256KBPattern(string pattern, int size) {
    var buf = new byte[size];
    switch (pattern) {
      case "Zeroes": break;
      case "Incrementing":
        for (var i = 0; i < size; i++) buf[i] = (byte)(i & 0xFF);
        break;
      case "Alternating":
        for (var i = 0; i < size; i++) buf[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
        break;
      case "Repeating":
        var pat = "ABCDEFGHIJKLMNOP"u8;
        for (var i = 0; i < size; i++) buf[i] = pat[i % pat.Length];
        break;
      case "Text":
        var text = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < size; i++) buf[i] = text[i % text.Length];
        break;
      case "Random":
        new Random(77).NextBytes(buf);
        break;
      case "BinaryStruct":
        var rng = new Random(123);
        for (var i = 0; i < size; i++)
          buf[i] = (byte)((i % 16) switch {
            0 or 1 or 2 or 3 => i / 16 & 0xFF,
            4 or 5 => 0,
            6 or 7 => i % 3,
            _ => rng.Next(256),
          });
        break;
    }
    return buf;
  }

  // --- Interop tests: decompress data compressed by System.IO.Compression ---

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void Interop_DecompressSystemBrotli_ShortText() {
    var original = Encoding.UTF8.GetBytes("Hello Brotli interop test!");
    var systemCompressed = CompressWithSystemBrotli(original);
    var result = BrotliDecompressor.Decompress(systemCompressed);
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void Interop_DecompressSystemBrotli_RepetitiveData() {
    var original = Encoding.UTF8.GetBytes(new string('A', 100));
    var systemCompressed = CompressWithSystemBrotli(original);

    TestContext.Out.WriteLine($"Compressed hex: {BitConverter.ToString(systemCompressed)}");
    TestContext.Out.WriteLine($"Compressed length: {systemCompressed.Length}");
    var result = BrotliDecompressor.Decompress(systemCompressed);
    TestContext.Out.WriteLine($"Result length: {result.Length}");
    TestContext.Out.WriteLine($"First 20 result: {BitConverter.ToString(result.AsSpan(0, Math.Min(20, result.Length)).ToArray())}");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void Interop_DecompressSystemBrotli_MixedContent() {
    // Progressively test different data sizes to find the complexity threshold
    var texts = new[] {
      "Hello World!!!", // 14 bytes
      "The quick brown fox jumps over the lazy dog.", // 44 bytes
      "ABCABCABCABCABCABC 123 test", // with repeats
      string.Join(" ", Enumerable.Range(0, 30).Select(i => $"word{i}")), // ~199 bytes
    };
    foreach (var text in texts) {
      var original = Encoding.UTF8.GetBytes(text);
      var systemCompressed = CompressWithSystemBrotli(original);
      TestContext.Out.WriteLine($"\n'{text.Substring(0, Math.Min(60, text.Length))}...': {original.Length} → {systemCompressed.Length} bytes");
      TestContext.Out.WriteLine($"  Hex: {BitConverter.ToString(systemCompressed)}");
      try {
        var result = BrotliDecompressor.Decompress(systemCompressed);
        TestContext.Out.WriteLine($"  OK: {result.Length} bytes decompressed");
        if (!result.SequenceEqual(original)) {
          var firstDiff = Enumerable.Range(0, Math.Min(result.Length, original.Length))
            .FirstOrDefault(i => result[i] != original[i], -1);
          TestContext.Out.WriteLine($"  MISMATCH at byte {firstDiff}: expected 0x{original[firstDiff]:X2} got 0x{result[firstDiff]:X2}");
          TestContext.Out.WriteLine($"  Expected first 40: {BitConverter.ToString(original.AsSpan(0, Math.Min(40, original.Length)).ToArray())}");
          TestContext.Out.WriteLine($"  Got first 40:      {BitConverter.ToString(result.AsSpan(0, Math.Min(40, result.Length)).ToArray())}");
        }
        Assert.That(result, Is.EqualTo(original), $"Failed for: '{text.Substring(0, Math.Min(60, text.Length))}'");
      } catch (Exception ex) {
        TestContext.Out.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
        throw;
      }
    }
  }

  // --- Static dictionary tests ---

  [Category("ThemVsUs")]
  [Test]
  public void StaticDictionary_GetNumBits_ValidLengths() {
    // Lengths 4-24 should return positive bit counts
    for (var len = BrotliStaticDictionary.MinWordLength; len <= BrotliStaticDictionary.MaxWordLength; ++len)
      Assert.That(BrotliStaticDictionary.GetNumBits(len), Is.GreaterThan(0),
        $"Length {len} should have positive bit count");
  }

  [Category("EdgeCase")]
  [Test]
  public void StaticDictionary_GetNumBits_InvalidLengths() {
    Assert.That(BrotliStaticDictionary.GetNumBits(0), Is.EqualTo(0));
    Assert.That(BrotliStaticDictionary.GetNumBits(3), Is.EqualTo(0));
    Assert.That(BrotliStaticDictionary.GetNumBits(25), Is.EqualTo(0));
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticDictionary_GetWord_ReturnsBytes() {
    Span<byte> buf = stackalloc byte[64];
    var written = BrotliStaticDictionary.GetWord(4, 0, 0, buf);
    Assert.That(written, Is.EqualTo(4)); // identity transform preserves length
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticDictionary_TryGetStaticReference_BeyondWindow() {
    // Distance beyond window size should be a dictionary reference
    var found = BrotliStaticDictionary.TryGetStaticReference(
      100, 10, out var wordLen, out _, out _);
    // 100 > 10, so it's a static dict reference
    Assert.That(found, Is.True);
    Assert.That(wordLen, Is.GreaterThanOrEqualTo(BrotliStaticDictionary.MinWordLength));
  }

  [Category("EdgeCase")]
  [Test]
  public void StaticDictionary_TryGetStaticReference_WithinWindow() {
    var found = BrotliStaticDictionary.TryGetStaticReference(5, 10, out _, out _, out _);
    Assert.That(found, Is.False); // within window, not a dict reference
  }

  // --- LZ77 Compressor tests ---

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_Empty() {
    var data = Array.Empty<byte>();
    var compressed = BrotliCompressor.CompressLz77(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_ShortData_FallsBackToUncompressed() {
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var compressed = BrotliCompressor.CompressLz77(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_RepetitiveData_RoundTrip() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var compressed = BrotliCompressor.CompressLz77(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));

    // LZ77 should produce smaller output for repetitive data
    var uncompressed = BrotliCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThanOrEqualTo(uncompressed.Length));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_SimpleRepeat_RoundTrip() {
    // Simple test: "ABCDABCD" — one match at position 4 with distance 4, length 4
    var data = "ABCDABCD"u8.ToArray();
    var compressed = BrotliCompressor.CompressLz77(data);

    // System cross-validation
    using var sysMs = new MemoryStream(compressed);
    using var sysBrotli = new BrotliStream(sysMs, CompressionMode.Decompress);
    using var sysResult = new MemoryStream();
    sysBrotli.CopyTo(sysResult);
    Assert.That(sysResult.ToArray(), Is.EqualTo(data), "System decoder should accept our LZ77 output");

    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_MediumRepeat_RoundTrip() {
    // 32 unique + 32 repeated = 64 bytes
    var data = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345ABCDEFGHIJKLMNOPQRSTUVWXYZ012345"u8.ToArray();
    var compressed = BrotliCompressor.CompressLz77(data);

    // System cross-validation
    using var sysMs = new MemoryStream(compressed);
    using var sysBrotli = new BrotliStream(sysMs, CompressionMode.Decompress);
    using var sysResult = new MemoryStream();
    sysBrotli.CopyTo(sysResult);
    Assert.That(sysResult.ToArray(), Is.EqualTo(data), "System decoder should accept our LZ77 output");

    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase("ABCDABCDABCDABCDABCDABCD", Description = "Multiple 11-capped copies")]
  [TestCase("ABCDEFGHIJKLMNOPQABCDEFGHIJKLMNOPQ", Description = "17+17 unique")]
  [TestCase("AAAAABBBBBCCCCCAAAAABBBBBCCCCCXYZ", Description = "Short pattern repeat + tail")]
  public void CompressLz77_VariousRepeats_RoundTrip(string text) {
    var data = Encoding.ASCII.GetBytes(text);
    var compressed = BrotliCompressor.CompressLz77(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data), $"Failed for: {text}");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void CompressLz77_TextWithRepeats_RoundTrip() {
    var data = "The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog again."u8.ToArray();
    var compressed = BrotliCompressor.CompressLz77(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }


  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void DiagSystemBrotli_RingBuffer() {
    // Verify what system decoder produces from the known compressed hex
    var compressedHex = "8B-31-00-00-20-82-D8-00-0E-5C-C3-09-71-36-80-0B-70-58-DE-06";
    var compressed = compressedHex.Split('-').Select(h => Convert.ToByte(h, 16)).ToArray();

    // Decompress with system
    using var ms = new MemoryStream(compressed);
    using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
    using var result = new MemoryStream();
    brotli.CopyTo(result);
    var sysResult = result.ToArray();

    TestContext.Out.WriteLine($"System result length: {sysResult.Length}");
    TestContext.Out.WriteLine($"System first 20: {BitConverter.ToString(sysResult.AsSpan(0, Math.Min(20, sysResult.Length)).ToArray())}");

    // Also decompress with our decoder
    var ourResult = BrotliDecompressor.Decompress(compressed);
    TestContext.Out.WriteLine($"Our result length: {ourResult.Length}");
    TestContext.Out.WriteLine($"Our first 20: {BitConverter.ToString(ourResult.AsSpan(0, Math.Min(20, ourResult.Length)).ToArray())}");

    Assert.That(ourResult, Is.EqualTo(sysResult), "Our decoder must match system decoder");
  }

  // --- Full interop tests: compress(own)→decompress(system) and compress(system)→decompress(own) ---

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [TestCase(0, Description = "Empty")]
  [TestCase(1, Description = "Single byte")]
  [TestCase(14, Description = "Short text")]
  [TestCase(100, Description = "Repetitive")]
  [TestCase(1000, Description = "Pattern mod 10")]
  [TestCase(10000, Description = "Random 10K")]
  [TestCase(100000, Description = "Random 100K")]
  public void Interop_OwnCompress_SystemDecompress(int size) {
    var data = MakeTestData(size);
    var compressed = BrotliCompressor.Compress(data, BrotliCompressionLevel.Default);

    using var ms = new MemoryStream(compressed);
    using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
    using var result = new MemoryStream();
    brotli.CopyTo(result);
    Assert.That(result.ToArray(), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [TestCase(0, Description = "Empty")]
  [TestCase(1, Description = "Single byte")]
  [TestCase(14, Description = "Short text")]
  [TestCase(100, Description = "Repetitive")]
  [TestCase(1000, Description = "Pattern mod 10")]
  [TestCase(10000, Description = "Random 10K")]
  [TestCase(100000, Description = "Random 100K")]
  public void Interop_SystemCompress_OwnDecompress(int size) {
    var data = MakeTestData(size);
    var compressed = CompressWithSystemBrotli(data);
    var decompressed = BrotliDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [TestCase(0, Description = "Empty")]
  [TestCase(1, Description = "Single byte")]
  [TestCase(14, Description = "Short text")]
  [TestCase(100, Description = "Repetitive")]
  [TestCase(1000, Description = "Pattern mod 10")]
  [TestCase(10000, Description = "Random 10K")]
  [TestCase(100000, Description = "Random 100K")]
  [TestCase(262144, Description = "Random 256K — requires 5 MLEN nibbles")]
  public void Interop_OwnLz77_SystemDecompress(int size) {
    var data = MakeTestData(size);
    var compressed = BrotliCompressor.CompressLz77(data);

    using var ms = new MemoryStream(compressed);
    using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
    using var result = new MemoryStream();
    brotli.CopyTo(result);
    Assert.That(result.ToArray(), Is.EqualTo(data));
  }

  private static byte[] MakeTestData(int size) => size switch {
    0 => [],
    1 => [42],
    14 => Encoding.UTF8.GetBytes("Hello, Brotli!"),
    100 => Encoding.UTF8.GetBytes(new string('A', 100)),
    1000 => Enumerable.Range(0, 1000).Select(i => (byte)(i % 10)).ToArray(),
    _ => MakeRandom(size),
  };

  private static byte[] MakeRandom(int size) {
    var data = new byte[size];
    new Random(42).NextBytes(data);
    return data;
  }

  private static byte[] CompressWithSystemBrotli(byte[] data) {
    using var ms = new MemoryStream();
    using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      brotli.Write(data);
    return ms.ToArray();
  }
}
