using Compression.Core.Entropy.Arithmetic;

namespace Compression.Tests.Entropy;

[TestFixture]
public class ArithmeticTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void BitCoding_RoundTrip_AllZeros() {
    var data = new int[100];
    var compressed = EncodeBits(data, 32768); // prob0 = 0.5
    var decoded = DecodeBits(compressed, 100, 32768);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void BitCoding_RoundTrip_AllOnes() {
    var data = Enumerable.Repeat(1, 100).ToArray();
    var compressed = EncodeBits(data, 32768);
    var decoded = DecodeBits(compressed, 100, 32768);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void BitCoding_RoundTrip_MixedBits() {
    var rng = new Random(42);
    var data = new int[200];
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(2);
    var compressed = EncodeBits(data, 32768);
    var decoded = DecodeBits(compressed, 200, 32768);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void BitCoding_SkewedProbability() {
    // Mostly zeros with high prob0 — should compress well
    var rng = new Random(7);
    var data = new int[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(100) < 5 ? 1 : 0; // 95% zeros
    var compressed = EncodeBits(data, 62259); // prob0 ≈ 0.95
    var decoded = DecodeBits(compressed, 500, 62259);
    Assert.That(decoded, Is.EqualTo(data));
    // Should compress well below 500 bits
    Assert.That(compressed.Length, Is.LessThan(100));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SymbolCoding_RoundTrip() {
    var rng = new Random(99);
    int numSymbols = 8;
    var data = new int[200];
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(numSymbols);

    var compressed = EncodeSymbols(data, numSymbols);
    var decoded = DecodeSymbols(compressed, 200, numSymbols);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void SymbolCoding_AdaptiveModel_CompressesBetter() {
    // Data with strong pattern: lots of symbol 0
    var data = new int[500];
    var rng = new Random(42);
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(100) < 80 ? 0 : rng.Next(1, 4);

    var compressed = EncodeSymbols(data, 4);
    // With adaptation, should be smaller than uniform coding
    // Uniform would be 2 bits/symbol = 125 bytes
    Assert.That(compressed.Length, Is.LessThan(125));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void SymbolCoding_SingleSymbol() {
    var data = Enumerable.Repeat(0, 100).ToArray();
    var compressed = EncodeSymbols(data, 4);
    var decoded = DecodeSymbols(compressed, 100, 4);
    Assert.That(decoded, Is.EqualTo(data));
    // Should compress extremely well
    Assert.That(compressed.Length, Is.LessThan(20));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void SymbolCoding_LargeAlphabet() {
    var rng = new Random(11);
    var data = new int[300];
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(256);

    var compressed = EncodeSymbols(data, 256);
    var decoded = DecodeSymbols(compressed, 300, 256);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void AdaptiveModel_FindSymbol_Correct() {
    var model = new AdaptiveModel(4);
    // Initial: uniform [1,1,1,1], cumFreq = [0,1,2,3,4]
    Assert.That(model.FindSymbol(0), Is.EqualTo(0));
    Assert.That(model.FindSymbol(1), Is.EqualTo(1));
    Assert.That(model.FindSymbol(2), Is.EqualTo(2));
    Assert.That(model.FindSymbol(3), Is.EqualTo(3));
  }

  [Category("HappyPath")]
  [Test]
  public void AdaptiveModel_Update_IncreasesFrequency() {
    var model = new AdaptiveModel(4);
    model.Update(2);
    Assert.That(model.GetFrequency(2), Is.EqualTo(2));
    Assert.That(model.TotalFrequency, Is.EqualTo(5));
  }

  // Helper methods

  private static byte[] EncodeBits(int[] bits, int prob0) {
    using var ms = new MemoryStream();
    var encoder = new ArithmeticEncoder(ms);
    foreach (int bit in bits)
      encoder.EncodeBit(bit, prob0);
    encoder.Finish();
    return ms.ToArray();
  }

  private static int[] DecodeBits(byte[] compressed, int count, int prob0) {
    using var ms = new MemoryStream(compressed);
    var decoder = new ArithmeticDecoder(ms);
    var result = new int[count];
    for (int i = 0; i < count; ++i)
      result[i] = decoder.DecodeBit(prob0);
    return result;
  }

  private static byte[] EncodeSymbols(int[] symbols, int numSymbols) {
    using var ms = new MemoryStream();
    var encoder = new ArithmeticEncoder(ms);
    var model = new AdaptiveModel(numSymbols);

    foreach (int sym in symbols) {
      uint cumFreq = (uint)model.GetCumulativeFrequency(sym);
      uint symFreq = (uint)model.GetFrequency(sym);
      uint totalFreq = (uint)model.TotalFrequency;
      encoder.EncodeSymbol(cumFreq, symFreq, totalFreq);
      model.Update(sym);
    }

    encoder.Finish();
    return ms.ToArray();
  }

  private static int[] DecodeSymbols(byte[] compressed, int count, int numSymbols) {
    using var ms = new MemoryStream(compressed);
    var decoder = new ArithmeticDecoder(ms);
    var model = new AdaptiveModel(numSymbols);
    var result = new int[count];

    for (int i = 0; i < count; ++i) {
      uint totalFreq = (uint)model.TotalFrequency;
      uint cumCount = decoder.GetCumulativeCount(totalFreq);
      int sym = model.FindSymbol((int)cumCount);
      uint cumFreq = (uint)model.GetCumulativeFrequency(sym);
      uint symFreq = (uint)model.GetFrequency(sym);
      decoder.UpdateSymbol(cumFreq, symFreq, totalFreq);
      model.Update(sym);
      result[i] = sym;
    }

    return result;
  }
}
