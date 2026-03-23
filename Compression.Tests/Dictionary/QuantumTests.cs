using Compression.Core.Dictionary.Quantum;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class QuantumTests {
  // -------------------------------------------------------------------------
  // QuantumConstants
  // -------------------------------------------------------------------------

  [Category("ThemVsUs")]
  [Test]
  public void WindowSize_Level1_Returns1024() {
    Assert.That(QuantumConstants.WindowSize(1), Is.EqualTo(1024));
  }

  [Category("ThemVsUs")]
  [Test]
  public void WindowSize_Level7_Returns64KB() {
    Assert.That(QuantumConstants.WindowSize(7), Is.EqualTo(65536));
  }

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

  [Category("ThemVsUs")]
  [TestCase(1, 4)]
  [TestCase(2, 5)]
  [TestCase(3, 6)]
  [TestCase(4, 7)]
  [TestCase(5, 12)]
  [TestCase(6, 24)]
  public void BaseMatchLength_ValidSelectors(int selector, int expected) {
    Assert.That(QuantumConstants.BaseMatchLength(selector), Is.EqualTo(expected));
  }

  [Category("Exception")]
  [Test]
  public void BaseMatchLength_InvalidSelector_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumConstants.BaseMatchLength(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => QuantumConstants.BaseMatchLength(7));
  }

  // -------------------------------------------------------------------------
  // QuantumModel
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Model_InitialState_UniformFrequencies() {
    var model = new QuantumModel(4);
    Assert.That(model.NumSymbols, Is.EqualTo(4));
    Assert.That(model.TotalFrequency, Is.EqualTo(4));
    Assert.That(model.GetFrequency(0), Is.EqualTo(1));
    Assert.That(model.GetFrequency(1), Is.EqualTo(1));
    Assert.That(model.GetFrequency(2), Is.EqualTo(1));
    Assert.That(model.GetFrequency(3), Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Model_CumulativeFrequencies_AreCorrect() {
    var model = new QuantumModel(4);
    Assert.That(model.GetCumulativeFrequency(0), Is.EqualTo(0));
    Assert.That(model.GetCumulativeFrequency(1), Is.EqualTo(1));
    Assert.That(model.GetCumulativeFrequency(2), Is.EqualTo(2));
    Assert.That(model.GetCumulativeFrequency(3), Is.EqualTo(3));
  }

  [Category("HappyPath")]
  [Test]
  public void Model_Update_IncrementsFrequency() {
    var model = new QuantumModel(4);
    model.Update(2);
    Assert.That(model.GetFrequency(2), Is.EqualTo(2));
    Assert.That(model.TotalFrequency, Is.EqualTo(5));
  }

  [Category("HappyPath")]
  [Test]
  public void Model_FindSymbol_ReturnsCorrectSymbol() {
    var model = new QuantumModel(4);
    // Uniform: cumFreq = [0, 1, 2, 3, 4]
    Assert.That(model.FindSymbol(0), Is.EqualTo(0));
    Assert.That(model.FindSymbol(1), Is.EqualTo(1));
    Assert.That(model.FindSymbol(2), Is.EqualTo(2));
    Assert.That(model.FindSymbol(3), Is.EqualTo(3));
  }

  [Category("HappyPath")]
  [Test]
  public void Model_FindSymbol_AfterUpdate_ReturnsCorrectSymbol() {
    var model = new QuantumModel(4);
    model.Update(1); // freq = [1, 2, 1, 1], cumFreq = [0, 1, 3, 4, 5]
    Assert.That(model.FindSymbol(0), Is.EqualTo(0));
    Assert.That(model.FindSymbol(1), Is.EqualTo(1));
    Assert.That(model.FindSymbol(2), Is.EqualTo(1));
    Assert.That(model.FindSymbol(3), Is.EqualTo(2));
    Assert.That(model.FindSymbol(4), Is.EqualTo(3));
  }

  [Category("Boundary")]
  [Test]
  public void Model_Rescale_OccursAtThreshold() {
    var model = new QuantumModel(4);
    // Each update adds 1 to total. Start at 4, threshold at 3800.
    // Need 3796 updates to reach threshold.
    for (var i = 0; i < 3796; ++i)
      model.Update(0);

    // After rescale, frequencies should be roughly halved
    // Before rescale: freq[0] = 3797, freq[1..3] = 1 each, total = 3800
    // After rescale: freq[0] = (3797+1)/2 = 1899, freq[1..3] = (1+1)/2 = 1 each
    // total = 1899 + 3 = 1902
    Assert.That(model.TotalFrequency, Is.LessThan(QuantumConstants.RescaleThreshold));
    Assert.That(model.GetFrequency(0), Is.GreaterThan(model.GetFrequency(1)));
  }

  [Category("Boundary")]
  [Test]
  public void Model_Rescale_MinimumFrequencyIsOne() {
    var model = new QuantumModel(4);
    // Update only symbol 0 until rescale
    for (var i = 0; i < 3796; ++i)
      model.Update(0);

    // All symbols that were at freq 1 should still be at freq 1 after rescale
    Assert.That(model.GetFrequency(1), Is.GreaterThanOrEqualTo(1));
    Assert.That(model.GetFrequency(2), Is.GreaterThanOrEqualTo(1));
    Assert.That(model.GetFrequency(3), Is.GreaterThanOrEqualTo(1));
  }

  // -------------------------------------------------------------------------
  // QuantumRangeDecoder
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeDecoder_DecodeSymbol_RoundTripsWithEncoder() {
    // Encode a known sequence of symbols, then decode them
    var symbols = new[] { 0, 1, 2, 0, 0, 1, 3 };
    var encoded = QuantumRangeEncode(symbols, 4);
    var decoder = new QuantumRangeDecoder(encoded);
    var model = new QuantumModel(4);

    foreach (var expected in symbols) {
      var decoded = decoder.DecodeSymbol(model);
      Assert.That(decoded, Is.EqualTo(expected));
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeDecoder_DecodeSymbol_LargerAlphabet() {
    // Test with 256-symbol alphabet (like literal model)
    var symbols = new[] { 0, 255, 128, 64, 32, 16, 8, 4, 2, 1 };
    var encoded = QuantumRangeEncode(symbols, 256);
    var decoder = new QuantumRangeDecoder(encoded);
    var model = new QuantumModel(256);

    foreach (var expected in symbols) {
      var decoded = decoder.DecodeSymbol(model);
      Assert.That(decoded, Is.EqualTo(expected));
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeDecoder_ReadRawBits_DecodesCorrectly() {
    // Encode a series of raw bit values, then decode
    // Use a model-based encode to set up state, then decode raw bits
    // For simplicity, just encode a few symbols then read raw bits
    var symbols = new[] { 0, 1 };
    var rawValue = 42; // 6 bits: 101010
    var numBits = 6;
    var encoded = QuantumRangeEncodeWithRawBits(symbols, 4, rawValue, numBits);
    var decoder = new QuantumRangeDecoder(encoded);
    var model = new QuantumModel(4);

    // Decode symbols first
    foreach (var expected in symbols) {
      var decoded = decoder.DecodeSymbol(model);
      Assert.That(decoded, Is.EqualTo(expected));
    }

    // Then decode raw bits
    var decodedRaw = decoder.ReadRawBits(numBits);
    Assert.That(decodedRaw, Is.EqualTo(rawValue));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RangeDecoder_ReadRawBits_SingleBit() {
    var rawValue = 1;
    var encoded = QuantumRangeEncodeWithRawBits([], 4, rawValue, 1);
    var decoder = new QuantumRangeDecoder(encoded);
    var decoded = decoder.ReadRawBits(1);
    Assert.That(decoded, Is.EqualTo(rawValue));
  }

  // -------------------------------------------------------------------------
  // QuantumDecompressor
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidWindowLevel_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => QuantumDecompressor.Decompress([], 0, 0));
    Assert.Throws<ArgumentOutOfRangeException>(
      () => QuantumDecompressor.Decompress([], 0, 8));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_NegativeSize_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => QuantumDecompressor.Decompress([], -1, 1));
  }

  [Category("EdgeCase")]
  [Test]
  public void Decompress_ZeroSize_ReturnsEmpty() {
    var result = QuantumDecompressor.Decompress([], 0, 1);
    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Decompress_SingleLiteral_RoundTrip() {
    // Encode a single literal byte (0x42) through the Quantum format
    byte[] original = [0x42];
    var compressed = QuantumCompress(original, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, original.Length, 1);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Decompress_MultipleLiterals_RoundTrip() {
    byte[] original = [0x48, 0x65, 0x6C, 0x6C, 0x6F]; // "Hello"
    var compressed = QuantumCompress(original, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, original.Length, 1);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Decompress_WithMatch_RoundTrip() {
    // Data with a repeated pattern that can use match references
    var original = new byte[20];
    for (var i = 0; i < 20; ++i)
      original[i] = (byte)(i % 4 + 0x41); // ABCDABCDABCD...
    var compressed = QuantumCompress(original, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, original.Length, 1);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Decompress_AllSameBytes_RoundTrip() {
    var original = new byte[32];
    Array.Fill(original, (byte)0xAA);
    var compressed = QuantumCompress(original, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, original.Length, 1);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Decompress_LargerWindow_RoundTrip() {
    var original = new byte[100];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i * 7 + 3);
    var compressed = QuantumCompress(original, 3);
    var decompressed = QuantumDecompressor.Decompress(compressed, original.Length, 3);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // QuantumCompressor round-trips
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Test]
  public void Compressor_Empty_ReturnsEmpty() {
    var compressed = QuantumCompressor.Compress([], 1);
    Assert.That(compressed, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_SingleByte_RoundTrip() {
    byte[] data = [0x42];
    var compressed = QuantumCompressor.Compress(data, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, 1,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_ShortText_RoundTrip() {
    var data = "Hello, Quantum!"u8.ToArray();
    var compressed = QuantumCompressor.Compress(data, 1);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, 1,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_RepetitiveData_RoundTrip() {
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    var compressed = QuantumCompressor.Compress(data, 3);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, 3,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Compressor_RepetitiveData_SmallerThanLiteral() {
    var data = new byte[200];
    Array.Fill(data, (byte)0xAA);
    var compressedLz = QuantumCompressor.Compress(data, 3);
    // LZ should compress repetitive data well
    Assert.That(compressedLz.Length, Is.LessThan(data.Length));
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
    var compressed = QuantumCompressor.Compress(data, 5);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, 5,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressor_AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var compressed = QuantumCompressor.Compress(data, 3);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, 3,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(1)]
  [TestCase(3)]
  [TestCase(5)]
  [TestCase(7)]
  public void Compressor_AllWindowLevels_RoundTrip(int level) {
    var data = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ"u8.ToArray();
    var compressed = QuantumCompressor.Compress(data, level);
    var decompressed = QuantumDecompressor.Decompress(compressed, data.Length, level,
      QuantumConstants.CompressorRescaleThreshold);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeEncoder_ManySymbols_RoundTrip() {
    var rng = new Random(123);
    var symbols = new int[500];
    for (var i = 0; i < symbols.Length; ++i)
      symbols[i] = rng.Next(7);

    const int threshold = QuantumConstants.CompressorRescaleThreshold;
    using var ms = new MemoryStream();
    var encoder = new Compression.Core.Dictionary.Quantum.QuantumRangeEncoder(ms);
    var encModel = new QuantumModel(7, threshold);
    foreach (var sym in symbols)
      encoder.EncodeSymbol(encModel, sym);
    encoder.Finish();
    var encoded = ms.ToArray();

    var decoder = new QuantumRangeDecoder(encoded);
    var decModel = new QuantumModel(7, threshold);
    for (var i = 0; i < symbols.Length; ++i) {
      var decoded = decoder.DecodeSymbol(decModel);
      Assert.That(decoded, Is.EqualTo(symbols[i]), $"Mismatch at symbol {i}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RangeEncoder_LargeAlphabet_ManySymbols_RoundTrip() {
    var rng = new Random(456);
    var symbols = new int[1000];
    for (var i = 0; i < symbols.Length; ++i)
      symbols[i] = rng.Next(256);

    const int threshold = QuantumConstants.CompressorRescaleThreshold;
    using var ms = new MemoryStream();
    var encoder = new Compression.Core.Dictionary.Quantum.QuantumRangeEncoder(ms);
    var encModel = new QuantumModel(256, threshold);
    foreach (var sym in symbols)
      encoder.EncodeSymbol(encModel, sym);
    encoder.Finish();
    var encoded = ms.ToArray();

    var decoder = new QuantumRangeDecoder(encoded);
    var decModel = new QuantumModel(256, threshold);
    for (var i = 0; i < symbols.Length; ++i) {
      var decoded = decoder.DecodeSymbol(decModel);
      Assert.That(decoded, Is.EqualTo(symbols[i]), $"Mismatch at symbol {i}");
    }
  }

  // -------------------------------------------------------------------------
  // Test helper: minimal Quantum range encoder (for generating test data)
  // -------------------------------------------------------------------------

  /// <summary>
  /// Minimal Quantum range encoder that produces data decodable by QuantumRangeDecoder.
  /// </summary>
  private static byte[] QuantumRangeEncode(int[] symbols, int numSymbols) {
    var state = new EncoderState();
    var model = new QuantumModel(numSymbols);

    foreach (var sym in symbols)
      EncodeSymbol(state, model, sym);

    // Flush
    state.Output.Add((byte)(state.Low >> 8));
    state.Output.Add((byte)(state.Low & 0xFF));
    // Padding for decoder read-ahead
    state.Output.Add(0);
    state.Output.Add(0);

    return [.. state.Output];
  }

  private static byte[] QuantumRangeEncodeWithRawBits(
    int[] symbols, int numSymbols, int rawValue, int numBits) {
    var state = new EncoderState();
    var model = new QuantumModel(numSymbols);

    foreach (var sym in symbols)
      EncodeSymbol(state, model, sym);

    // Encode raw bits (MSB first)
    for (var i = numBits - 1; i >= 0; --i) {
      var bit = (rawValue >> i) & 1;
      var range = state.High - state.Low + 1;
      var mid = state.Low + (range >> 1) - 1;
      if (bit == 0)
        state.High = mid;
      else
        state.Low = mid + 1;
      NormalizeEncoder(state);
    }

    // Flush
    state.Output.Add((byte)(state.Low >> 8));
    state.Output.Add((byte)(state.Low & 0xFF));
    state.Output.Add(0);
    state.Output.Add(0);

    return [.. state.Output];
  }

  /// <summary>
  /// Minimal Quantum compressor for test round-trips. Encodes all bytes as literals.
  /// </summary>
  private static byte[] QuantumCompress(byte[] data, int windowLevel) {
    var state = new EncoderState();
    var selectorModel = new QuantumModel(QuantumConstants.SelectorSymbols);
    var literalModel = new QuantumModel(QuantumConstants.LiteralSymbols);

    foreach (var b in data) {
      // Encode selector 0 (literal)
      EncodeSymbol(state, selectorModel, 0);
      // Encode literal value
      EncodeSymbol(state, literalModel, b);
    }

    // Flush
    state.Output.Add((byte)(state.Low >> 8));
    state.Output.Add((byte)(state.Low & 0xFF));
    state.Output.Add(0);
    state.Output.Add(0);

    return [.. state.Output];
  }

  private sealed class EncoderState {
    public int High = 0xFFFF;
    public int Low;
    public List<byte> Output = []; // initial code bytes prepended later

    public EncoderState() {
      // Reserve space for initial code (will be written at start)
      // Actually the decoder reads the first 2 bytes as the initial code.
      // We just emit normally and the first 2 bytes become the code.
    }
  }

  private static void EncodeSymbol(EncoderState state, QuantumModel model, int symbol) {
    var range = state.High - state.Low + 1;
    var symLow = model.GetCumulativeFrequency(symbol);
    var symHigh = symLow + model.GetFrequency(symbol);

    state.High = state.Low + (int)((long)range * symHigh / model.TotalFrequency) - 1;
    state.Low = state.Low + (int)((long)range * symLow / model.TotalFrequency);

    NormalizeEncoder(state);
    model.Update(symbol);
  }

  private static void NormalizeEncoder(EncoderState state) {
    while ((state.High - state.Low) < 256) {
      state.Output.Add((byte)(state.Low >> 8));
      state.High = ((state.High << 8) | 0xFF) & 0xFFFF;
      state.Low = (state.Low << 8) & 0xFFFF;
    }
  }
}
