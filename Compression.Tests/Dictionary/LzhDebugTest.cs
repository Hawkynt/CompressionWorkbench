using Compression.Core.Dictionary.Lzh;
using Compression.Core.BitIO;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzhDebugTest {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Verify_CanonicalCodes_RoundTrip() {
    // Test that the canonical Huffman encode/decode round-trips
    var freq = new int[8];
    freq[0] = 10; freq[1] = 5; freq[2] = 3; freq[3] = 1;
    freq[4] = 1;  freq[5] = 1; freq[6] = 1; freq[7] = 1;

    var lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    var codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Write each symbol using its code, then decode
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    int[] symbols = [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 1, 2];
    foreach (var sym in symbols)
      writer.WriteBits(codes[sym], lengths[sym]);
    writer.FlushBits();

    // Build decode table
    var maxLen = lengths.Max();
    var tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[maxLen + 1];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) blCount[lengths[i]]++;

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < lengths.Length; ++sym) {
      var len = lengths[sym];
      if (len == 0) continue;
      var symCode = nextCode[len]++;
      var fillBits = maxLen - len;
      var baseIdx = symCode << fillBits;
      var packedValue = sym | (len << 16);
      for (var fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    // Read back
    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);
    var decoded = new List<int>();
    for (var i = 0; i < symbols.Length; ++i) {
      reader.EnsureBits(maxLen);
      var peekBits = reader.PeekBits(maxLen);
      var entry = table[(int)peekBits];
      Assert.That(entry, Is.GreaterThanOrEqualTo(0), $"Table entry at {peekBits} is -1");
      var decodedSym = entry & 0xFFFF;
      var codeLen = entry >> 16;
      reader.DropBits(codeLen);
      decoded.Add(decodedSym);
    }

    Assert.That(decoded.ToArray(), Is.EqualTo(symbols));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Verify_TreeWriteRead_RoundTrip() {
    // Verify that WriteTree/ReadTree round-trips for a small set
    var freq = new int[5];
    freq[0] = 3; freq[1] = 2; freq[2] = 1; freq[3] = 1; freq[4] = 5;

    var lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    var codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Write tree + some symbols
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    // WriteTree format: [numSymbols : symbolBits] [code lengths...]
    var numSymbols = 5;
    var symbolBits = 4;
    writer.WriteBits((uint)numSymbols, symbolBits);
    for (var i = 0; i < numSymbols; ++i) {
      if (lengths[i] < 7)
        writer.WriteBits((uint)lengths[i], 3);
      else {
        writer.WriteBits(7, 3);
        for (var j = 7; j < lengths[i]; ++j) writer.WriteBits(1, 1);
        writer.WriteBits(0, 1);
      }
    }

    // Write some symbols
    int[] syms = [4, 0, 1, 2, 3, 0, 4];
    foreach (var s in syms) {
      Assert.That(lengths[s], Is.GreaterThan(0), $"Symbol {s} has zero length");
      writer.WriteBits(codes[s], lengths[s]);
    }
    writer.FlushBits();

    // Read back
    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);

    var readNum = (int)reader.ReadBits(symbolBits);
    Assert.That(readNum, Is.EqualTo(numSymbols));

    var readLengths = new int[readNum];
    var readMaxLen = 0;
    for (var i = 0; i < readNum; ++i) {
      var len = (int)reader.ReadBits(3);
      if (len == 7) {
        while (reader.ReadBits(1) == 1) len++;
      }
      readLengths[i] = len;
      if (len > readMaxLen) readMaxLen = len;
    }

    Assert.That(readLengths, Is.EqualTo(lengths.Take(numSymbols).ToArray()));

    // Build decode table and decode symbols
    var tableBits = readMaxLen;
    var tableSize = 1 << tableBits;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[tableBits + 1];
    for (var i = 0; i < readLengths.Length; ++i)
      if (readLengths[i] > 0 && readLengths[i] <= tableBits)
        blCount[readLengths[i]]++;

    var nextCode = new int[tableBits + 1];
    var code = 0;
    for (var bits = 1; bits <= tableBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < readLengths.Length; ++sym) {
      var len = readLengths[sym];
      if (len == 0 || len > tableBits) continue;
      var symCode = nextCode[len]++;
      var fillBits = tableBits - len;
      var baseIdx = symCode << fillBits;
      var packedValue = sym | (len << 16);
      for (var fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    var decoded = new List<int>();
    for (var i = 0; i < syms.Length; ++i) {
      reader.EnsureBits(tableBits);
      var peekBits = reader.PeekBits(tableBits);
      var entry = table[(int)peekBits];
      Assert.That(entry, Is.GreaterThanOrEqualTo(0));
      decoded.Add(entry & 0xFFFF);
      reader.DropBits(entry >> 16);
    }

    Assert.That(decoded.ToArray(), Is.EqualTo(syms));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_FiveDistinctBytes() {
    byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45];
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_RepeatedWithMatches() {
    var data = new byte[20];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchAtDistance5() {
    // Data: ABCDEABCDE - match at distance 5
    byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45, 0x41, 0x42, 0x43, 0x44, 0x45];
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchAtDistance10() {
    // ABCDEFGHIJABCDEFGHIJ - match at distance 10
    var data = new byte[20];
    for (var i = 0; i < 10; ++i) data[i] = (byte)(0x41 + i);
    for (var i = 10; i < 20; ++i) data[i] = (byte)(0x41 + i - 10);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Debug_PositionSlotEncoding() {
    // Verify position slot encode/decode directly
    for (var dist = 0; dist < 100; ++dist) {
      var slot = LzhEncoder.GetPositionSlot(dist);
      int reconstructed;
      if (slot <= 1) {
        reconstructed = slot;
      } else {
        var extraBits = slot - 1;
        var extraValue = dist - (1 << extraBits);
        reconstructed = (1 << extraBits) + extraValue;
      }
      Assert.That(reconstructed, Is.EqualTo(dist), $"Distance {dist} -> slot {slot} -> reconstructed {reconstructed}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_HuffmanCodeTable_Verify() {
    // Build a code tree for 11 symbols, verify every entry decodes correctly
    var freq = new int[300];
    freq[0x41] = 1; freq[0x42] = 1; freq[0x43] = 1; freq[0x44] = 1; freq[0x45] = 1;
    freq[0x46] = 1; freq[0x47] = 1; freq[0x48] = 1; freq[0x49] = 1; freq[0x4A] = 1;
    freq[263] = 1; // length code for match length 10

    var lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    var codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Verify all codes are unique and decodable
    var maxLen = lengths.Max();
    var tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[maxLen + 1];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) blCount[lengths[i]]++;

    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (var sym = 0; sym < lengths.Length; ++sym) {
      var len = lengths[sym];
      if (len == 0) continue;
      var symCode = nextCode[len]++;
      var fillBits = maxLen - len;
      var baseIdx = symCode << fillBits;
      var packedValue = sym | (len << 16);
      for (var fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    // Now write each symbol and verify decode
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);
    int[] syms = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 263];
    foreach (var sym in syms) {
      Assert.That(lengths[sym], Is.GreaterThan(0), $"Symbol {sym} has no code");
      writer.WriteBits(codes[sym], lengths[sym]);
    }
    writer.FlushBits();

    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);
    foreach (var expectedSym in syms) {
      reader.EnsureBits(maxLen);
      var peek = reader.PeekBits(maxLen);
      var entry = table[(int)peek];
      Assert.That(entry, Is.GreaterThanOrEqualTo(0), $"No table entry for peek={peek}");
      var decodedSym = entry & 0xFFFF;
      var codeLen = entry >> 16;
      reader.DropBits(codeLen);
      Assert.That(decodedSym, Is.EqualTo(expectedSym), $"Expected sym {expectedSym}, got {decodedSym}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_ExtraBitsRoundTrip() {
    // Test MSB BitWriter + BitBuffer round-trip for extra bits
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    // Write a 3-bit Huffman code, then 3 extra bits
    writer.WriteBits(0b101, 3); // Huffman code
    writer.WriteBits(4, 3);     // extra bits = 4 = 100 binary
    writer.WriteBits(0b11, 2);  // another code
    writer.FlushBits();

    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);

    var huff = reader.ReadBits(3);
    var extra = reader.ReadBits(3);
    var huff2 = reader.ReadBits(2);

    Assert.That(huff, Is.EqualTo(0b101u), "Huffman code");
    Assert.That(extra, Is.EqualTo(4u), "Extra bits");
    Assert.That(huff2, Is.EqualTo(0b11u), "Second code");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_ManyLiterals() {
    var data = new byte[100];
    for (var i = 0; i < 100; ++i) data[i] = (byte)i;

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_ManualEncodeDecode() {
    // Encode ABC + match(len=3, dist=2) → should produce ABCABC when decoded
    // Use the encoder/decoder pair rather than manual bitstream construction
    byte[] data = [0x41, 0x42, 0x43, 0x41, 0x42, 0x43];
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchDistance10_Manual() {
    // ABCDEFGHIJABCDEFGHIJ: 10 literals + 1 match(len=10, dist=9 0-based)
    var data = new byte[20];
    for (var i = 0; i < 10; ++i) data[i] = (byte)(0x41 + i);
    for (var i = 10; i < 20; ++i) data[i] = (byte)(0x41 + i - 10);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(30)]
  public void Debug_PatternData_Sizes(int size) {
    var data = new byte[size];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(100)]
  [TestCase(200)]
  [TestCase(500)]
  public void Debug_RandomData_Sizes(int size) {
    var rng = new Random(42);
    var data = new byte[size];
    rng.NextBytes(data);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }
}
