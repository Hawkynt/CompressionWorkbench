using Compression.Core.Entropy.Fse;

namespace Compression.Tests.Entropy;

[TestFixture]
public class FseTests {
  [Test]
  public void NormalizeCounts_DistributesCorrectly() {
    int[] counts = { 100, 50, 25, 25 };
    short[] normalized = FseEncoder.NormalizeCounts(counts, 3, 8); // tableLog=8, tableSize=256

    var sum = 0;
    for (int i = 0; i <= 3; i++) {
      if (normalized[i] > 0) sum += normalized[i];
      else if (normalized[i] == -1) sum += 1;
    }

    Assert.That(sum, Is.EqualTo(256)); // must sum to tableSize
  }

  [Test]
  public void NormalizeCounts_PreservesNonZero() {
    int[] counts = { 1000, 1, 1, 1 };
    short[] normalized = FseEncoder.NormalizeCounts(counts, 3, 8);

    // All symbols with count > 0 must have normalizedCount >= -1 (at least 1 table entry)
    for (int i = 0; i <= 3; i++)
      Assert.That(normalized[i], Is.Not.EqualTo(0), $"Symbol {i} should be preserved");
  }

  [Test]
  public void Table_Build_CreatesCorrectSize() {
    short[] counts = { 4, 2, 1, 1 }; // sum = 8, tableLog = 3
    var table = FseTable.Build(counts, 3, 3);
    Assert.That(table.TableLog, Is.EqualTo(3));
    Assert.That(table.Symbol.Length, Is.EqualTo(8));
  }

  [Test]
  public void Table_Build_AllSymbolsPresent() {
    short[] counts = { 4, 2, 1, 1 }; // sum = 8, tableLog = 3
    var table = FseTable.Build(counts, 3, 3);

    // Each symbol should appear in the table the correct number of times
    var symbolCounts = new int[4];
    for (int i = 0; i < 8; i++)
      symbolCounts[table.Symbol[i]]++;

    Assert.That(symbolCounts[0], Is.EqualTo(4));
    Assert.That(symbolCounts[1], Is.EqualTo(2));
    Assert.That(symbolCounts[2], Is.EqualTo(1));
    Assert.That(symbolCounts[3], Is.EqualTo(1));
  }

  [Test]
  public void EncodeDecode_SingleSymbol() {
    // Data with only one unique symbol
    byte[] data = new byte[100]; // all zeros

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    var maxSym = 0;

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, 8);
    var encoder = new FseEncoder(normalized, maxSym, 8);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 8);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_TwoSymbols() {
    byte[] data = [0, 1, 0, 1, 0, 1, 0, 0, 1, 1];

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    var maxSym = 1;

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, 6);
    var encoder = new FseEncoder(normalized, maxSym, 6);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 6);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_FourSymbols() {
    // Simple 4-symbol test with known distribution
    byte[] data = new byte[64];
    for (int i = 0; i < 32; i++) data[i] = 0;      // 50%
    for (int i = 32; i < 48; i++) data[i] = 1;      // 25%
    for (int i = 48; i < 56; i++) data[i] = 2;      // 12.5%
    for (int i = 56; i < 64; i++) data[i] = 3;      // 12.5%

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    var maxSym = 3;

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, 6);
    var encoder = new FseEncoder(normalized, maxSym, 6);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, 6);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_SkewedDistribution() {
    // 80% 'a', 15% 'b', 5% 'c'
    byte[] data = new byte[1000];
    for (int i = 0; i < 800; i++) data[i] = (byte)'a';
    for (int i = 800; i < 950; i++) data[i] = (byte)'b';
    for (int i = 950; i < 1000; i++) data[i] = (byte)'c';

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    int maxSym = (byte)'c';

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, FseConstants.DefaultTableLog);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_UniformDistribution() {
    byte[] data = new byte[256];
    for (int i = 0; i < 256; i++) data[i] = (byte)i;

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;

    short[] normalized = FseEncoder.NormalizeCounts(counts, 255, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, 255, FseConstants.DefaultTableLog);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, 255, FseConstants.DefaultTableLog);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_LargeData() {
    // 2KB of varied data
    byte[] data = new byte[2048];
    var rng = new Random(42);
    // Generate with geometric distribution (more realistic)
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(rng.Next(0, 16) < 8 ? 0 : rng.Next(0, 256));

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    var maxSym = 0;
    for (int i = 255; i >= 0; i--) {
      if (counts[i] > 0) { maxSym = i; break; }
    }

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    byte[] compressed = encoder.Encode(data);

    var decoder = new FseDecoder(normalized, maxSym, FseConstants.DefaultTableLog);
    byte[] decompressed = decoder.Decode(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void EncodeDecode_SkewedCompresses() {
    // Highly skewed data should compress significantly
    byte[] data = new byte[1000];
    for (int i = 0; i < 800; i++) data[i] = (byte)'a';
    for (int i = 800; i < 950; i++) data[i] = (byte)'b';
    for (int i = 950; i < 1000; i++) data[i] = (byte)'c';

    int[] counts = new int[256];
    foreach (byte b in data) counts[b]++;
    int maxSym = (byte)'c';

    short[] normalized = FseEncoder.NormalizeCounts(counts, maxSym, FseConstants.DefaultTableLog);
    var encoder = new FseEncoder(normalized, maxSym, FseConstants.DefaultTableLog);
    byte[] compressed = encoder.Encode(data);

    Assert.That(compressed.Length, Is.LessThan(data.Length),
      $"Compressed size {compressed.Length} should be less than original {data.Length}");
  }

  [Test]
  public void NormalizedCounts_WriteRead_RoundTrips() {
    int[] counts = { 100, 50, 25, 15, 10 };
    short[] normalized = FseEncoder.NormalizeCounts(counts, 4, 8);

    byte[] buffer = new byte[100];
    int written = FseEncoder.WriteNormalizedCounts(buffer, 0, normalized, 4, 8);

    var (readCounts, readMaxSym, readTableLog, bytesRead) =
      FseDecoder.ReadNormalizedCounts(buffer.AsSpan(0, written));

    Assert.That(readTableLog, Is.EqualTo(8));
    Assert.That(readMaxSym, Is.EqualTo(4));
    for (int i = 0; i <= 4; i++)
      Assert.That(readCounts[i], Is.EqualTo(normalized[i]), $"Count mismatch at symbol {i}");
  }

  [Test]
  public void NormalizedCounts_WriteRead_WithSubProbability() {
    // Create counts where some symbols will get -1 (sub-probability)
    int[] counts = { 10000, 1, 1, 1, 1 };
    short[] normalized = FseEncoder.NormalizeCounts(counts, 4, 8);

    byte[] buffer = new byte[100];
    int written = FseEncoder.WriteNormalizedCounts(buffer, 0, normalized, 4, 8);

    var (readCounts, readMaxSym, readTableLog, _) =
      FseDecoder.ReadNormalizedCounts(buffer.AsSpan(0, written));

    Assert.That(readTableLog, Is.EqualTo(8));
    for (int i = 0; i <= 4; i++)
      Assert.That(readCounts[i], Is.EqualTo(normalized[i]), $"Count mismatch at symbol {i}");
  }

  [Test]
  public void HuffmanFse_CompressDecompress_RoundTrips() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog. " +
      "This is repetitive text for testing Huffman compression.");

    byte[] compressed = HuffmanFse.CompressHuffman(data);
    byte[] decompressed = HuffmanFse.DecompressHuffman(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void HuffmanFse_BuildWeights_NonZeroForUsedSymbols() {
    byte[] data = { 0, 1, 2, 3, 0, 0, 1, 1, 2 };
    int[] weights = HuffmanFse.BuildWeights(data);

    Assert.That(weights[0], Is.GreaterThan(0));
    Assert.That(weights[1], Is.GreaterThan(0));
    Assert.That(weights[2], Is.GreaterThan(0));
    Assert.That(weights[3], Is.GreaterThan(0));
    Assert.That(weights[4], Is.EqualTo(0)); // symbol 4 not present
  }

  [Test]
  public void HuffmanFse_WriteReadWeights_RoundTrips() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes("hello world hello hello");
    int[] weights = HuffmanFse.BuildWeights(data);

    var maxSymbol = 0;
    for (int i = 255; i >= 0; i--) {
      if (weights[i] > 0) { maxSymbol = i; break; }
    }

    byte[] buffer = new byte[256];
    int written = HuffmanFse.WriteWeights(buffer, 0, weights, maxSymbol);

    int[] readWeights = HuffmanFse.ReadWeights(buffer.AsSpan(0, written), out int bytesRead);

    Assert.That(bytesRead, Is.EqualTo(written));
    for (int i = 0; i <= maxSymbol; i++)
      Assert.That(readWeights[i], Is.EqualTo(weights[i]), $"Weight mismatch at symbol {i}");
  }
}
