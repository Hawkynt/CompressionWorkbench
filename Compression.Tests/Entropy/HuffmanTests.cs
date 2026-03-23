using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;

namespace Compression.Tests.Entropy;

[TestFixture]
public class HuffmanTests {
  [Category("HappyPath")]
  [Test]
  public void BuildFromFrequencies_CreatesValidTree() {
    var frequencies = new long[256];
    frequencies['a'] = 5;
    frequencies['b'] = 9;
    frequencies['c'] = 12;
    frequencies['d'] = 13;
    frequencies['e'] = 16;
    frequencies['f'] = 45;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);

    Assert.That(root.IsLeaf, Is.False);
    Assert.That(root.Frequency, Is.EqualTo(100));
  }

  [Category("EdgeCase")]
  [Test]
  public void BuildFromFrequencies_SingleSymbol_CreatesValidTree() {
    var frequencies = new long[256];
    frequencies['x'] = 42;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);

    Assert.That(root.IsLeaf, Is.False); // Should have a dummy node
  }

  [Category("Exception")]
  [Test]
  public void BuildFromFrequencies_NoSymbols_Throws() {
    var frequencies = new long[256];

    Assert.Throws<ArgumentException>(() => HuffmanTree.BuildFromFrequencies(frequencies));
  }

  [Category("HappyPath")]
  [Test]
  public void GetCodeLengths_ReturnsCorrectLengths() {
    var frequencies = new long[256];
    frequencies['a'] = 5;
    frequencies['b'] = 9;
    frequencies['c'] = 12;
    frequencies['d'] = 13;
    frequencies['e'] = 16;
    frequencies['f'] = 45;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var lengths = HuffmanTree.GetCodeLengths(root, 256);

    // Most frequent symbol ('f') should have shortest code
    Assert.That(lengths['f'], Is.LessThanOrEqualTo(lengths['a']));

    // All used symbols should have non-zero lengths
    Assert.That(lengths['a'], Is.GreaterThan(0));
    Assert.That(lengths['b'], Is.GreaterThan(0));
    Assert.That(lengths['c'], Is.GreaterThan(0));
    Assert.That(lengths['d'], Is.GreaterThan(0));
    Assert.That(lengths['e'], Is.GreaterThan(0));
    Assert.That(lengths['f'], Is.GreaterThan(0));

    // Unused symbols should have zero lengths
    Assert.That(lengths[0], Is.EqualTo(0));
  }

  [Category("ThemVsUs")]
  [Test]
  public void CanonicalHuffman_GetCode_ReturnsValidCodes() {
    var codeLengths = new int[4];
    codeLengths[0] = 2; // Symbol 0: length 2
    codeLengths[1] = 1; // Symbol 1: length 1
    codeLengths[2] = 3; // Symbol 2: length 3
    codeLengths[3] = 3; // Symbol 3: length 3

    var table = new CanonicalHuffman(codeLengths);

    var (code1, len1) = table.GetCode(1);
    Assert.That(len1, Is.EqualTo(1));
    Assert.That(code1, Is.EqualTo(0u)); // First code at length 1: 0

    var (code0, len0) = table.GetCode(0);
    Assert.That(len0, Is.EqualTo(2));
    Assert.That(code0, Is.EqualTo(0b10u)); // First code at length 2: 10

    var (code2, len2) = table.GetCode(2);
    Assert.That(len2, Is.EqualTo(3));
    Assert.That(code2, Is.EqualTo(0b110u)); // First code at length 3: 110

    var (code3, len3) = table.GetCode(3);
    Assert.That(len3, Is.EqualTo(3));
    Assert.That(code3, Is.EqualTo(0b111u)); // Second code at length 3: 111
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_UniformDistribution() {
    // Create a uniform frequency distribution
    var frequencies = new long[8];
    for (var i = 0; i < 8; ++i)
      frequencies[i] = 100;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 8);
    var table = new CanonicalHuffman(codeLengths);

    // Encode
    var encodedStream = new MemoryStream();
    var bitWriter = new BitWriter<MsbBitOrder>(encodedStream);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);

    int[] symbols = [0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 0];
    encoder.EncodeSymbols(symbols);
    encoder.Flush();

    // Decode
    encodedStream.Position = 0;
    var bitBuffer = new BitBuffer<MsbBitOrder>(encodedStream);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);
    var decoded = decoder.DecodeSymbols(symbols.Length);

    Assert.That(decoded, Is.EqualTo(symbols));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SkewedDistribution() {
    var frequencies = new long[5];
    frequencies[0] = 100; // Very frequent
    frequencies[1] = 10;
    frequencies[2] = 5;
    frequencies[3] = 2;
    frequencies[4] = 1;  // Very rare

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 5);
    var table = new CanonicalHuffman(codeLengths);

    // Encode a mix of symbols
    var encodedStream = new MemoryStream();
    var bitWriter = new BitWriter<MsbBitOrder>(encodedStream);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);

    int[] symbols = [0, 0, 0, 1, 0, 2, 0, 0, 3, 0, 4, 0, 0, 0, 1, 0];
    encoder.EncodeSymbols(symbols);
    encoder.Flush();

    // Decode
    encodedStream.Position = 0;
    var bitBuffer = new BitBuffer<MsbBitOrder>(encodedStream);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);
    var decoded = decoder.DecodeSymbols(symbols.Length);

    Assert.That(decoded, Is.EqualTo(symbols));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var numSymbols = 16;
    var dataLength = 500;

    // Generate random frequencies
    var frequencies = new long[numSymbols];
    for (var i = 0; i < numSymbols; ++i)
      frequencies[i] = rng.Next(1, 1000);

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var codeLengths = HuffmanTree.GetCodeLengths(root, numSymbols);
    var table = new CanonicalHuffman(codeLengths);

    // Generate random data
    var symbols = new int[dataLength];
    for (var i = 0; i < dataLength; ++i)
      symbols[i] = rng.Next(numSymbols);

    // Encode
    var encodedStream = new MemoryStream();
    var bitWriter = new BitWriter<MsbBitOrder>(encodedStream);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);
    encoder.EncodeSymbols(symbols);
    encoder.Flush();

    // Decode
    encodedStream.Position = 0;
    var bitBuffer = new BitBuffer<MsbBitOrder>(encodedStream);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);
    var decoded = decoder.DecodeSymbols(symbols.Length);

    Assert.That(decoded, Is.EqualTo(symbols));
  }

  [Category("Boundary")]
  [Test]
  public void LimitCodeLengths_ClampsToMaxLength() {
    // Create a very skewed distribution that would produce long codes
    var frequencies = new long[20];
    frequencies[0] = 100000;
    for (var i = 1; i < 20; ++i)
      frequencies[i] = 1;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 20);

    HuffmanTree.LimitCodeLengths(codeLengths, 7);

    for (var i = 0; i < 20; ++i) {
      if (codeLengths[i] > 0)
        Assert.That(codeLengths[i], Is.LessThanOrEqualTo(7));
    }

    // Verify the limited code lengths can still build a valid canonical table
    var table = new CanonicalHuffman(codeLengths);
    Assert.That(table.MaxCodeLength, Is.LessThanOrEqualTo(7));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleSymbol() {
    var frequencies = new long[256];
    frequencies[42] = 100;

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 256);
    var table = new CanonicalHuffman(codeLengths);

    var encodedStream = new MemoryStream();
    var bitWriter = new BitWriter<MsbBitOrder>(encodedStream);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);

    int[] symbols = [42, 42, 42];
    encoder.EncodeSymbols(symbols);
    encoder.Flush();

    encodedStream.Position = 0;
    var bitBuffer = new BitBuffer<MsbBitOrder>(encodedStream);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);
    var decoded = decoder.DecodeSymbols(3);

    Assert.That(decoded, Is.EqualTo(symbols));
  }
}
