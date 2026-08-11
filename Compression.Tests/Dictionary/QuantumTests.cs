using Compression.Core.Dictionary.Quantum;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class QuantumTests {
  // -------------------------------------------------------------------------
  // QuantumConstants
  // -------------------------------------------------------------------------

  [Category("ThemVsUs")]
  [TestCase(1, 1024)]
  [TestCase(2, 2048)]
  [TestCase(3, 4096)]
  [TestCase(4, 8192)]
  [TestCase(5, 16384)]
  [TestCase(6, 32768)]
  [TestCase(7, 65536)]
  public void WindowSize_AllLevels(int level, int expected) {
    Assert.That(QuantumConstants.WindowSize(level), Is.EqualTo(expected));
  }

  [Category("EdgeCase")]
  [Test]
  public void StateTables_CoverEveryState() {
    Assert.Multiple(() => {
      Assert.That(QuantumConstants.LiteralNextState, Has.Length.EqualTo(QuantumConstants.StateCount));
      Assert.That(QuantumConstants.MatchNextState, Has.Length.EqualTo(QuantumConstants.StateCount));
      Assert.That(QuantumConstants.LiteralNextState, Is.All.InRange(0, QuantumConstants.StateCount - 1));
      Assert.That(QuantumConstants.MatchNextState, Is.All.InRange(0, QuantumConstants.StateCount - 1));
    });
  }

  // -------------------------------------------------------------------------
  // QuantumModel
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Model_InitialState_UniformFrequencies() {
    var model = new QuantumModel(4);
    Assert.Multiple(() => {
      Assert.That(model.NumSymbols, Is.EqualTo(4));
      Assert.That(model.TotalFrequency, Is.EqualTo(4));
      for (var symbol = 0; symbol < 4; ++symbol)
        Assert.That(model.GetFrequency(symbol), Is.EqualTo(1));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Model_CumulativeBelow_IsRunningSum() {
    var model = new QuantumModel(4);
    Assert.Multiple(() => {
      Assert.That(model.CumulativeBelow(0), Is.EqualTo(0));
      Assert.That(model.CumulativeBelow(1), Is.EqualTo(1));
      Assert.That(model.CumulativeBelow(2), Is.EqualTo(2));
      Assert.That(model.CumulativeBelow(3), Is.EqualTo(3));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Model_Update_AddsTheIncrement() {
    var model = new QuantumModel(4);
    model.Update(2);
    Assert.Multiple(() => {
      Assert.That(model.GetFrequency(2), Is.EqualTo(1 + QuantumConstants.ModelIncrement));
      Assert.That(model.TotalFrequency, Is.EqualTo(4 + QuantumConstants.ModelIncrement));
    });
  }

  [Category("HappyPath")]
  [Test]
  public void Model_FindSymbol_ReturnsCorrectSymbolAndCumulative() {
    var model = new QuantumModel(4);
    for (var scaled = 0; scaled < 4; ++scaled) {
      var symbol = model.FindSymbol(scaled, out var cumulative);
      Assert.Multiple(() => {
        Assert.That(symbol, Is.EqualTo(scaled));
        Assert.That(cumulative, Is.EqualTo(scaled));
      });
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Model_FindSymbol_AfterUpdate_SpansTheWiderSubRange() {
    var model = new QuantumModel(4);
    model.Update(1); // freq = [1, 25, 1, 1]
    Assert.Multiple(() => {
      Assert.That(model.FindSymbol(0, out _), Is.EqualTo(0));
      Assert.That(model.FindSymbol(1, out _), Is.EqualTo(1));
      Assert.That(model.FindSymbol(25, out _), Is.EqualTo(1));
      Assert.That(model.FindSymbol(26, out var cumulative), Is.EqualTo(2));
      Assert.That(cumulative, Is.EqualTo(26));
    });
  }

  [Category("Exception")]
  [Test]
  public void Model_FindSymbol_BeyondTotal_Throws() {
    var model = new QuantumModel(4);
    Assert.Throws<InvalidDataException>(() => model.FindSymbol(4, out _));
  }

  [Category("Boundary")]
  [Test]
  public void Model_Rescale_KeepsTotalBoundedAndOrderingIntact() {
    var model = new QuantumModel(4);
    for (var i = 0; i < 5000; ++i)
      model.Update(0);

    Assert.Multiple(() => {
      Assert.That(model.TotalFrequency, Is.LessThanOrEqualTo(QuantumConstants.ModelMaxTotal));
      Assert.That(model.GetFrequency(0), Is.GreaterThan(model.GetFrequency(1)));
    });
  }

  [Category("Boundary")]
  [Test]
  public void Model_Rescale_NeverDropsASymbolToZero() {
    var model = new QuantumModel(4);
    for (var i = 0; i < 5000; ++i)
      model.Update(0);

    Assert.Multiple(() => {
      for (var symbol = 1; symbol < 4; ++symbol)
        Assert.That(model.GetFrequency(symbol), Is.GreaterThanOrEqualTo(1));
    });
  }

  [Category("Boundary")]
  [Test]
  public void Model_LiteralAlphabet_Adapts() {
    // The literal alphabet is the largest in the format; a rescale threshold that is
    // too close to it would fire on every update and pin the model to uniform, which
    // silently costs a full 8 bits for every literal.
    var model = new QuantumModel(QuantumConstants.LiteralSymbols);
    for (var i = 0; i < 5000; ++i)
      model.Update('A');

    Assert.That(model.GetFrequency('A'), Is.GreaterThan(model.GetFrequency('B')));
  }

  // -------------------------------------------------------------------------
  // QuantumRangeEncoder / QuantumRangeDecoder
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeCoder_Symbols_RoundTrip() {
    int[] symbols = [0, 1, 2, 0, 0, 1, 3];
    var encoded = EncodeSymbols(symbols, 4);

    var decoder = new QuantumRangeDecoder(encoded);
    var model = new QuantumModel(4);
    Assert.Multiple(() => {
      foreach (var expected in symbols)
        Assert.That(decoder.DecodeSymbol(model), Is.EqualTo(expected));
    });
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeCoder_LargeAlphabet_RoundTrips() {
    var random = new Random(456);
    var symbols = new int[1000];
    for (var i = 0; i < symbols.Length; ++i)
      symbols[i] = random.Next(QuantumConstants.LiteralSymbols);

    var encoded = EncodeSymbols(symbols, QuantumConstants.LiteralSymbols);
    var decoder = new QuantumRangeDecoder(encoded);
    var model = new QuantumModel(QuantumConstants.LiteralSymbols);
    for (var i = 0; i < symbols.Length; ++i)
      Assert.That(decoder.DecodeSymbol(model), Is.EqualTo(symbols[i]), $"Mismatch at symbol {i}");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeCoder_EqualProbabilityBits_RoundTrip() {
    var random = new Random(0x0B17);
    var bits = new int[500];
    for (var i = 0; i < bits.Length; ++i)
      bits[i] = random.Next(2);

    using var buffer = new MemoryStream();
    var encoder = new QuantumRangeEncoder(buffer);
    foreach (var bit in bits)
      encoder.EncodeEqualProbabilityBit(bit);
    encoder.Finish();

    var decoder = new QuantumRangeDecoder(buffer.ToArray());
    for (var i = 0; i < bits.Length; ++i)
      Assert.That(decoder.DecodeEqualProbabilityBit(), Is.EqualTo(bits[i]), $"Mismatch at bit {i}");
  }

  [Category("HappyPath")]
  [Test]
  public void RangeCoder_SkewedSymbols_CostFarLessThanEightBitsEach() {
    // A model that adapts must spend well under one byte on a symbol it sees
    // constantly. Pinning the coder at a fixed uniform model would fail this.
    var symbols = new int[4000];
    var encoded = EncodeSymbols(symbols, QuantumConstants.LiteralSymbols);
    Assert.That(encoded, Has.Length.LessThan(symbols.Length / 8));
  }

  // -------------------------------------------------------------------------
  // QuantumSlotCoding
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [TestCase(0, 0)]
  [TestCase(1, 1)]
  [TestCase(2, 2)]
  [TestCase(3, 2)]
  [TestCase(255, 8)]
  [TestCase(256, 9)]
  [TestCase(65536, 17)]
  public void SlotCoding_BitLength(long value, int expected) {
    Assert.That(QuantumSlotCoding.BitLength(value), Is.EqualTo(expected));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SlotCoding_RoundTripsAcrossMagnitudes() {
    long[] values = [1, 2, 3, 4, 7, 8, 100, 255, 256, 4095, 65535, 65536, 1 << 20];

    using var buffer = new MemoryStream();
    var encoder = new QuantumRangeEncoder(buffer);
    var encodeModel = new QuantumModel(QuantumConstants.SlotSymbols);
    foreach (var value in values)
      QuantumSlotCoding.Encode(encoder, encodeModel, value);
    encoder.Finish();

    var decoder = new QuantumRangeDecoder(buffer.ToArray());
    var decodeModel = new QuantumModel(QuantumConstants.SlotSymbols);
    Assert.Multiple(() => {
      foreach (var expected in values)
        Assert.That(QuantumSlotCoding.Decode(decoder, decodeModel), Is.EqualTo(expected));
    });
  }

  // -------------------------------------------------------------------------
  // QuantumDecompressor argument handling
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidWindowLevel_Throws() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => QuantumDecompressor.Decompress(default, 0, 0));
      Assert.Throws<ArgumentOutOfRangeException>(() => QuantumDecompressor.Decompress(default, 0, 8));
    });
  }

  [Category("Exception")]
  [Test]
  public void Decompress_NegativeSize_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumDecompressor.Decompress(default, -1, 1));
  }

  [Category("EdgeCase")]
  [Test]
  public void Decompress_ZeroSize_ReturnsEmpty() {
    Assert.That(QuantumDecompressor.Decompress(default, 0, 1), Is.Empty);
  }

  [Category("Exception")]
  [Test]
  public void Decompress_MatchBeforeAnyOutput_Throws() {
    // An all-ones stream decodes a match as the very first token, so its distance
    // necessarily points before the start of the output and must be rejected.
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xFF);
    Assert.Throws<InvalidDataException>(() => QuantumDecompressor.Decompress(garbage, 100, 7));
  }

  [Category("EdgeCase")]
  [Test]
  public void Decompress_TruncatedStream_DoesNotReturnTheOriginal() {
    // The format carries no integrity check and pads a short stream with zero bits,
    // so truncation yields wrong data rather than an error. Callers that need
    // detection must supply their own checksum.
    var data = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    var compressed = QuantumCompressor.Compress(data, 7);
    var truncated = QuantumDecompressor.Decompress(compressed.AsMemory(0, 1), data.Length, 7);
    Assert.That(truncated, Is.Not.EqualTo(data).AsCollection);
  }

  // -------------------------------------------------------------------------
  // QuantumCompressor round-trips
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Test]
  public void Compressor_Empty_ReturnsEmpty() {
    Assert.That(QuantumCompressor.Compress([], 1), Is.Empty);
  }

  [Category("Exception")]
  [TestCase(0)]
  [TestCase(8)]
  public void Compressor_InvalidWindowLevel_Throws(int level) {
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumCompressor.Compress([1, 2, 3], level));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_SingleByte_RoundTrip() {
    AssertRoundTrips([0x42], 1);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_ShortText_RoundTrip() {
    AssertRoundTrips("Hello, Quantum!"u8.ToArray(), 1);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_RepetitiveData_RoundTrip() {
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    AssertRoundTrips(data, 3);
  }

  [Category("HappyPath")]
  [Test]
  public void Compressor_RepetitiveData_SmallerThanLiteral() {
    var data = new byte[200];
    Array.Fill(data, (byte)0xAA);
    Assert.That(QuantumCompressor.Compress(data, 3), Has.Length.LessThan(data.Length));
  }

  [Category("HappyPath")]
  [Test]
  public void Compressor_IncompressibleData_DoesNotExpandMuch() {
    // An arithmetic coder over adaptive models should cost a few percent on random
    // input. Expansion beyond that means the coder is losing its interval.
    var data = new byte[4096];
    new Random(0x0A17).NextBytes(data);
    Assert.That(QuantumCompressor.Compress(data, 7), Has.Length.LessThan(data.Length * 11 / 10));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(50)]
  [TestCase(100)]
  [TestCase(200)]
  [TestCase(300)]
  [TestCase(500)]
  public void Compressor_RandomData_RoundTrip(int size) {
    var data = new byte[size];
    new Random(42).NextBytes(data);
    AssertRoundTrips(data, 5);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)i;

    AssertRoundTrips(data, 3);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(1)]
  [TestCase(3)]
  [TestCase(5)]
  [TestCase(7)]
  public void Compressor_AllWindowLevels_RoundTrip(int level) {
    AssertRoundTrips("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray(), level);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_MatchLongerThanItsDistance_RoundTrips() {
    // A run is coded as one overlapping match, so the copy has to be byte by byte.
    var data = new byte[5000];
    Array.Fill(data, (byte)0x5A);
    AssertRoundTrips(data, 7);
  }

  private static void AssertRoundTrips(byte[] data, int windowLevel) {
    var compressed = QuantumCompressor.Compress(data, windowLevel);
    var round = QuantumDecompressor.Decompress(compressed, data.Length, windowLevel);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  private static byte[] EncodeSymbols(int[] symbols, int numSymbols) {
    using var buffer = new MemoryStream();
    var encoder = new QuantumRangeEncoder(buffer);
    var model = new QuantumModel(numSymbols);
    foreach (var symbol in symbols)
      encoder.EncodeSymbol(model, symbol);

    encoder.Finish();
    return buffer.ToArray();
  }
}
