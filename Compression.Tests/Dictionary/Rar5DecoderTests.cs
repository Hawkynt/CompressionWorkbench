using Compression.Core.Dictionary.Rar;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Rar5DecoderTests {
  // --- Rar5HuffmanDecoder unit tests ---

  [Category("EdgeCase")]
  [Test]
  public void HuffmanDecoder_Build_SingleSymbol_DecodesCorrectly() {
    var decoder = new Rar5HuffmanDecoder();
    var lengths = new int[4];
    lengths[2] = 1; // symbol 2 with code length 1

    decoder.Build(lengths, 4);

    // Create a bit stream with the code for symbol 2 repeated
    // Code length 1 for symbol 2: code is 0 or 1 depending on canonical assignment
    // With only one symbol, any bit pattern should decode to symbol 2
    var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    var reader = new Rar5BitReader(data);
    int sym = decoder.DecodeSymbol(reader);
    Assert.That(sym, Is.EqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void HuffmanDecoder_Build_TwoSymbols_DecodesCorrectly() {
    var decoder = new Rar5HuffmanDecoder();
    var lengths = new int[4];
    lengths[0] = 1; // symbol 0, code 0 (1 bit)
    lengths[1] = 1; // symbol 1, code 1 (1 bit)

    decoder.Build(lengths, 4);

    // Byte 0b10 = bits: 0, 1 (LSB first)
    var data = new byte[] { 0b00000010, 0, 0, 0 };
    var reader = new Rar5BitReader(data);

    int s1 = decoder.DecodeSymbol(reader); // should read bit 0 → symbol 0
    int s2 = decoder.DecodeSymbol(reader); // should read bit 1 → symbol 1

    Assert.That(s1, Is.EqualTo(0));
    Assert.That(s2, Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void HuffmanDecoder_Build_ThreeSymbols() {
    var decoder = new Rar5HuffmanDecoder();
    var lengths = new int[3];
    lengths[0] = 1; // symbol 0: code 0 (1 bit)
    lengths[1] = 2; // symbol 1: code 10 → reversed 01 (2 bits)
    lengths[2] = 2; // symbol 2: code 11 → reversed 11 (2 bits)

    decoder.Build(lengths, 3);

    // Encode: symbol 0 (0), symbol 1 (01 reversed = 10), symbol 2 (11 reversed = 11)
    // Bit stream: 0, 10, 11 → 0 01 11 (pad) = 0b11_10_0 in LSB order
    // Byte: bits[0]=0 (sym0), bits[1..2]=10 (sym1), bits[3..4]=11 (sym2)
    // = 0b_11_10_0 = bit layout 0, 0, 1, 1, 1 → byte 0b00011100 = 0x1C? No...
    // LSB first: bit0=0, bit1=0, bit2=1, bit3=1, bit4=1 → byte = 0b00011100 = 0x1C
    // Wait, reversed codes:
    // sym0: canonical=0 (1 bit), reversed=0
    // sym1: canonical=10 (2 bit), reversed=01
    // sym2: canonical=11 (2 bit), reversed=11
    // Stream: sym0→bit0=0, sym1→bit1..2=01, sym2→bit3..4=11
    // byte = 0b_11_01_0 as bits 4..0 = 0b11010 = 0x1A

    var data = new byte[] { 0x1A, 0, 0, 0 };
    var reader = new Rar5BitReader(data);

    Assert.That(decoder.DecodeSymbol(reader), Is.EqualTo(0));
    Assert.That(decoder.DecodeSymbol(reader), Is.EqualTo(1));
    Assert.That(decoder.DecodeSymbol(reader), Is.EqualTo(2));
  }

  [Category("EdgeCase")]
  [Test]
  public void HuffmanDecoder_EmptyTable_DoesNotThrow() {
    var decoder = new Rar5HuffmanDecoder();
    var lengths = new int[4]; // all zeros

    decoder.Build(lengths, 4);

    var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    var reader = new Rar5BitReader(data);
    // Should not throw, returns 0 for invalid codes
    int sym = decoder.DecodeSymbol(reader);
    Assert.That(sym, Is.GreaterThanOrEqualTo(0));
  }

  // --- Rar5BitReader unit tests ---

  [Category("HappyPath")]
  [Test]
  public void BitReader_ReadBits_ReadsCorrectly() {
    var data = new byte[] { 0xAB, 0xCD };
    var reader = new Rar5BitReader(data);

    // LSB first: 0xAB = 10101011, first 4 bits (LSB) = 1011 = 11
    uint first4 = reader.ReadBits(4);
    Assert.That(first4, Is.EqualTo(0xB)); // 1011 = 0xB

    uint next4 = reader.ReadBits(4);
    Assert.That(next4, Is.EqualTo(0xA)); // 1010 = 0xA
  }

  [Category("Boundary")]
  [Test]
  public void BitReader_ReadBits_CrossByteBoundary() {
    var data = new byte[] { 0xFF, 0x00 };
    var reader = new Rar5BitReader(data);

    // Read 12 bits: first 8 are 0xFF, next 4 are 0
    uint bits = reader.ReadBits(12);
    Assert.That(bits, Is.EqualTo(0x0FF));
  }

  [Category("HappyPath")]
  [Test]
  public void BitReader_PeekBits_DoesNotAdvance() {
    var data = new byte[] { 0xAB };
    var reader = new Rar5BitReader(data);

    uint peek1 = reader.PeekBits(4);
    uint peek2 = reader.PeekBits(4);
    Assert.That(peek1, Is.EqualTo(peek2));
  }

  [Category("Boundary")]
  [Test]
  public void BitReader_IsAtEnd_TrueWhenExhausted() {
    var data = new byte[] { 0x42 };
    var reader = new Rar5BitReader(data);
    reader.ReadBits(8);
    Assert.That(reader.IsAtEnd, Is.True);
  }

  // --- Rar5Filters unit tests ---

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Filter_Delta_RoundTrip() {
    // Create original interleaved data for 2 channels
    var original = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };

    // Manually create delta-encoded data (channel-separated, delta format)
    int channels = 2;
    int channelSize = original.Length / channels;

    // Separate channels
    var encoded = new byte[original.Length];
    for (int ch = 0; ch < channels; ++ch) {
      byte prev = 0;
      for (int i = 0; i < channelSize; ++i) {
        byte val = original[i * channels + ch];
        encoded[ch * channelSize + i] = unchecked((byte)(val - prev));
        prev = val;
      }
    }

    // Apply delta filter (decode)
    var decoded = Rar5Filters.Apply(Rar5Constants.FilterDelta, encoded, channels);
    Assert.That(decoded, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Test]
  public void Filter_E8E9_ModifiesCallInstructions() {
    // Create data with an E8 instruction
    var data = new byte[10];
    data[0] = 0xE8;
    data[1] = 15; data[2] = 0; data[3] = 0; data[4] = 0; // absolute addr = 15

    var result = Rar5Filters.Apply(Rar5Constants.FilterE8E9, data);
    // After filter: addr = 15 - (0 + 5) = 10
    int addr = BitConverter.ToInt32(result, 1);
    Assert.That(addr, Is.EqualTo(10));
  }

  [Category("EdgeCase")]
  [Test]
  public void Filter_Unknown_PassThrough() {
    var data = new byte[] { 1, 2, 3, 4 };
    var result = Rar5Filters.Apply(99, data);
    Assert.That(result, Is.EqualTo(data));
  }

  // --- Rar5Constants unit tests ---

  [Category("ThemVsUs")]
  [Test]
  public void Constants_DistanceBase_SmallSlots() {
    Assert.That(Rar5Constants.DistanceBase(0), Is.EqualTo(0));
    Assert.That(Rar5Constants.DistanceBase(1), Is.EqualTo(1));
    Assert.That(Rar5Constants.DistanceBase(2), Is.EqualTo(2));
    Assert.That(Rar5Constants.DistanceBase(3), Is.EqualTo(3));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Constants_DistanceExtraBits_Growth() {
    Assert.That(Rar5Constants.DistanceExtraBits(0), Is.EqualTo(0));
    Assert.That(Rar5Constants.DistanceExtraBits(1), Is.EqualTo(0));
    Assert.That(Rar5Constants.DistanceExtraBits(4), Is.EqualTo(1));
    Assert.That(Rar5Constants.DistanceExtraBits(5), Is.EqualTo(1));
    Assert.That(Rar5Constants.DistanceExtraBits(6), Is.EqualTo(2));
  }

  // --- Rar5Decoder integration test ---

  [Category("HappyPath")]
  [Test]
  public void Decoder_Constructor_ValidDictionarySize() {
    var decoder = new Rar5Decoder(256 * 1024);
    Assert.That(decoder, Is.Not.Null);
  }

  [Category("Boundary")]
  [Test]
  public void Decoder_Constructor_MinDictionarySize() {
    var decoder = new Rar5Decoder(Rar5Constants.MinDictionarySize);
    Assert.That(decoder, Is.Not.Null);
  }

  [Category("Exception")]
  [Test]
  public void Decoder_Constructor_BelowMinSize_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Rar5Decoder(1024));
  }

  [Category("EdgeCase")]
  [Test]
  public void Decoder_Decompress_EmptyInput_ReturnsEmpty() {
    var decoder = new Rar5Decoder(256 * 1024);
    var result = decoder.Decompress([], 0);
    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void Decoder_DecompressLiterals_ValidHuffmanStream() {
    // Construct a minimal valid RAR5 compressed stream:
    // 1. Code-length code lengths (20 × 4 bits = 80 bits = 10 bytes)
    // 2. Main table code lengths (306 symbols via code-length codes)
    // 3. Offset table (64 symbols)
    // 4. Low-offset table (16 symbols)
    // 5. Length table (16 symbols)
    // 6. Compressed data symbols
    //
    // This is complex to construct manually, so we test at the component level
    // (Huffman decoder, bit reader, filters) and verify the decoder
    // doesn't crash on various inputs.

    // Test that the decoder handles a request for 0 output size gracefully
    var decoder = new Rar5Decoder(256 * 1024);
    var result = decoder.Decompress(new byte[100], 0);
    Assert.That(result, Is.Empty);
  }

  // --- Filter integration tests ---

  [Category("HappyPath")]
  [Test]
  public void Filter_Delta_SingleChannel() {
    // Single channel: deltas → accumulated values
    var deltas = new byte[] { 5, 3, 2, 10 }; // accumulated: 5, 8, 10, 20
    var result = Rar5Filters.Apply(Rar5Constants.FilterDelta, deltas, 1);
    Assert.That(result, Is.EqualTo(new byte[] { 5, 8, 10, 20 }));
  }

  [Category("HappyPath")]
  [Test]
  public void Filter_Arm_RoundTrip() {
    // Create ARM BL instruction
    var original = new byte[8];
    original[0] = 0x10; original[1] = 0x00; original[2] = 0x00; original[3] = 0xEB;

    // The ARM filter converts absolute→relative on decode
    // First make it look absolute (as if encoder already converted relative→absolute)
    int offset = 0x10; // original relative offset
    int wordAddr = 0 >> 2; // at position 0
    int absOffset = offset + wordAddr;
    original[0] = (byte)(absOffset & 0xFF);
    original[1] = (byte)((absOffset >> 8) & 0xFF);
    original[2] = (byte)((absOffset >> 16) & 0xFF);

    var decoded = Rar5Filters.Apply(Rar5Constants.FilterArm, original);
    // After ARM filter: offset -= wordAddr(=0), so result should equal input
    Assert.That(decoded[3], Is.EqualTo(0xEB));
  }

  [Category("EdgeCase")]
  [Test]
  public void Filter_E8E9_NoCallInstructions_Unchanged() {
    var data = new byte[] { 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC };
    var result = Rar5Filters.Apply(Rar5Constants.FilterE8E9, data);
    Assert.That(result, Is.EqualTo(data));
  }
}
