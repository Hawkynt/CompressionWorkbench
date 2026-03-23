using Compression.Core.Entropy.Fse;

namespace Compression.Tests.Entropy;

[TestFixture]
public class FseTests {
  [Category("HappyPath")]
  [Test]
  public void NormalizeCounts_DistributesCorrectly() {
    int[] counts = { 100, 50, 25, 25 };
    var normalized = FseEncoder.NormalizeCounts(counts, 3, 8); // tableLog=8, tableSize=256

    var sum = 0;
    for (var i = 0; i <= 3; ++i) {
      if (normalized[i] > 0) sum += normalized[i];
      else if (normalized[i] == -1) sum += 1;
    }

    Assert.That(sum, Is.EqualTo(256)); // must sum to tableSize
  }

  [Category("EdgeCase")]
  [Test]
  public void NormalizeCounts_PreservesNonZero() {
    int[] counts = { 1000, 1, 1, 1 };
    var normalized = FseEncoder.NormalizeCounts(counts, 3, 8);

    // All symbols with count > 0 must have normalizedCount >= -1 (at least 1 table entry)
    for (var i = 0; i <= 3; ++i)
      Assert.That(normalized[i], Is.Not.EqualTo(0), $"Symbol {i} should be preserved");
  }

  [Category("HappyPath")]
  [Test]
  public void Table_Build_CreatesCorrectSize() {
    short[] counts = { 4, 2, 1, 1 }; // sum = 8, tableLog = 3
    var table = FseTable.Build(counts, 3, 3);
    Assert.That(table.TableLog, Is.EqualTo(3));
    Assert.That(table.Symbol.Length, Is.EqualTo(8));
  }

  [Category("HappyPath")]
  [Test]
  public void Table_Build_AllSymbolsPresent() {
    short[] counts = { 4, 2, 1, 1 }; // sum = 8, tableLog = 3
    var table = FseTable.Build(counts, 3, 3);

    // Each symbol should appear in the table the correct number of times
    var symbolCounts = new int[4];
    for (var i = 0; i < 8; ++i)
      symbolCounts[table.Symbol[i]]++;

    Assert.That(symbolCounts[0], Is.EqualTo(4));
    Assert.That(symbolCounts[1], Is.EqualTo(2));
    Assert.That(symbolCounts[2], Is.EqualTo(1));
    Assert.That(symbolCounts[3], Is.EqualTo(1));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_SingleSymbol() {
    // Data with only one unique symbol
    var data = new byte[100]; // all zeros

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    var maxSym = 0;

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, 8);
    var encoder = new FseEncoder(normalized, maxSym, 8);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 8);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_TwoSymbols() {
    byte[] data = [0, 1, 0, 1, 0, 1, 0, 0, 1, 1];

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    var maxSym = 1;

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, 6);
    var encoder = new FseEncoder(normalized, maxSym, 6);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 6);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_FourSymbols() {
    // Simple 4-symbol test with known distribution
    var data = new byte[64];
    for (var i = 0; i < 32; ++i) data[i] = 0;      // 50%
    for (var i = 32; i < 48; ++i) data[i] = 1;      // 25%
    for (var i = 48; i < 56; ++i) data[i] = 2;      // 12.5%
    for (var i = 56; i < 64; ++i) data[i] = 3;      // 12.5%

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    var maxSym = 3;

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, 6);
    var encoder = new FseEncoder(normalized, maxSym, 6);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 6);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_SkewedDistribution() {
    // 80% 'a', 15% 'b', 5% 'c'
    var data = new byte[1000];
    for (var i = 0; i < 800; ++i) data[i] = (byte)'a';
    for (var i = 800; i < 950; ++i) data[i] = (byte)'b';
    for (var i = 950; i < 1000; ++i) data[i] = (byte)'c';

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    int maxSym = (byte)'c';

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, FseConstants.DefaultTableLog);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_UniformDistribution() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i) data[i] = (byte)i;

    var counts = new int[256];
    foreach (var b in data) counts[b]++;

    var normalized = FseEncoder.NormalizeCounts(counts, 255, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, 255, FseConstants.DefaultTableLog);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, 255, FseConstants.DefaultTableLog);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_LargeData() {
    // 2KB of varied data
    var data = new byte[2048];
    var rng = new Random(42);
    // Generate with geometric distribution (more realistic)
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(rng.Next(0, 16) < 8 ? 0 : rng.Next(0, 256));

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    var maxSym = 0;
    for (var i = 255; i >= 0; i--) {
      if (counts[i] > 0) { maxSym = i; break; }
    }

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    var compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, FseConstants.DefaultTableLog);
    var decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void EncodeDecode_SkewedCompresses() {
    // Highly skewed data should compress significantly
    var data = new byte[1000];
    for (var i = 0; i < 800; ++i) data[i] = (byte)'a';
    for (var i = 800; i < 950; ++i) data[i] = (byte)'b';
    for (var i = 950; i < 1000; ++i) data[i] = (byte)'c';

    var counts = new int[256];
    foreach (var b in data) counts[b]++;
    int maxSym = (byte)'c';

    var normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    var compressed = encoder.Encode(data);

    Assert.That(compressed.Length, Is.LessThan(data.Length),
      $"Compressed size {compressed.Length} should be less than original {data.Length}");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void NormalizedCounts_WriteRead_RoundTrips() {
    int[] counts = { 100, 50, 25, 15, 10 };
    var normalized = FseEncoder.NormalizeCounts(counts, 4, 8);

    var buffer = new byte[100];
    var written = FseEncoder.WriteNormalizedCounts(buffer, 0, normalized, 4, 8);

    var (readCounts, readMaxSym, readTableLog, bytesRead) =
      FseDecoder.ReadNormalizedCounts(buffer.AsSpan(0, written));

    Assert.That(readTableLog, Is.EqualTo(8));
    Assert.That(readMaxSym, Is.EqualTo(4));
    for (var i = 0; i <= 4; ++i)
      Assert.That(readCounts[i], Is.EqualTo(normalized[i]), $"Count mismatch at symbol {i}");
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void NormalizedCounts_WriteRead_WithSubProbability() {
    // Create counts where some symbols will get -1 (sub-probability)
    int[] counts = { 10000, 1, 1, 1, 1 };
    var normalized = FseEncoder.NormalizeCounts(counts, 4, 8);

    var buffer = new byte[100];
    var written = FseEncoder.WriteNormalizedCounts(buffer, 0, normalized, 4, 8);

    var (readCounts, readMaxSym, readTableLog, _) =
      FseDecoder.ReadNormalizedCounts(buffer.AsSpan(0, written));

    Assert.That(readTableLog, Is.EqualTo(8));
    for (var i = 0; i <= 4; ++i)
      Assert.That(readCounts[i], Is.EqualTo(normalized[i]), $"Count mismatch at symbol {i}");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void HuffmanFse_CompressDecompress_RoundTrips() {
    var data = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog. " +
      "This is repetitive text for testing Huffman compression.");

    var compressed = HuffmanFse.CompressHuffman(data);
    var decompressed = HuffmanFse.DecompressHuffman(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void HuffmanFse_BuildWeights_NonZeroForUsedSymbols() {
    byte[] data = { 0, 1, 2, 3, 0, 0, 1, 1, 2 };
    var weights = HuffmanFse.BuildWeights(data);

    Assert.That(weights[0], Is.GreaterThan(0));
    Assert.That(weights[1], Is.GreaterThan(0));
    Assert.That(weights[2], Is.GreaterThan(0));
    Assert.That(weights[3], Is.GreaterThan(0));
    Assert.That(weights[4], Is.EqualTo(0)); // symbol 4 not present
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void HuffmanFse_WriteReadWeights_RoundTrips() {
    var data = System.Text.Encoding.UTF8.GetBytes("hello world hello hello");
    var weights = HuffmanFse.BuildWeights(data);

    var maxSymbol = 0;
    for (var i = 255; i >= 0; i--) {
      if (weights[i] > 0) { maxSymbol = i; break; }
    }

    var buffer = new byte[256];
    var written = HuffmanFse.WriteWeights(buffer, 0, weights, maxSymbol);

    var readWeights = HuffmanFse.ReadWeights(buffer.AsSpan(0, written), out var bytesRead);

    Assert.That(bytesRead, Is.EqualTo(written));
    for (var i = 0; i <= maxSymbol; ++i)
      Assert.That(readWeights[i], Is.EqualTo(weights[i]), $"Weight mismatch at symbol {i}");
  }
}
