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
    int[] freq = new int[8];
    freq[0] = 10; freq[1] = 5; freq[2] = 3; freq[3] = 1;
    freq[4] = 1;  freq[5] = 1; freq[6] = 1; freq[7] = 1;

    int[] lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    uint[] codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Write each symbol using its code, then decode
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    int[] symbols = [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 1, 2];
    foreach (int sym in symbols)
      writer.WriteBits(codes[sym], lengths[sym]);
    writer.FlushBits();

    // Build decode table
    int maxLen = lengths.Max();
    int tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[maxLen + 1];
    for (int i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) blCount[lengths[i]]++;

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (int sym = 0; sym < lengths.Length; ++sym) {
      int len = lengths[sym];
      if (len == 0) continue;
      int symCode = nextCode[len]++;
      int fillBits = maxLen - len;
      int baseIdx = symCode << fillBits;
      int packedValue = sym | (len << 16);
      for (int fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    // Read back
    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);
    var decoded = new List<int>();
    for (int i = 0; i < symbols.Length; ++i) {
      reader.EnsureBits(maxLen);
      uint peekBits = reader.PeekBits(maxLen);
      int entry = table[(int)peekBits];
      Assert.That(entry, Is.GreaterThanOrEqualTo(0), $"Table entry at {peekBits} is -1");
      int decodedSym = entry & 0xFFFF;
      int codeLen = entry >> 16;
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
    int[] freq = new int[5];
    freq[0] = 3; freq[1] = 2; freq[2] = 1; freq[3] = 1; freq[4] = 5;

    int[] lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    uint[] codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Write tree + some symbols
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    // WriteTree format: [numSymbols : symbolBits] [code lengths...]
    int numSymbols = 5;
    int symbolBits = 4;
    writer.WriteBits((uint)numSymbols, symbolBits);
    for (int i = 0; i < numSymbols; ++i) {
      if (lengths[i] < 7)
        writer.WriteBits((uint)lengths[i], 3);
      else {
        writer.WriteBits(7, 3);
        for (int j = 7; j < lengths[i]; ++j) writer.WriteBits(1, 1);
        writer.WriteBits(0, 1);
      }
    }

    // Write some symbols
    int[] syms = [4, 0, 1, 2, 3, 0, 4];
    foreach (int s in syms) {
      Assert.That(lengths[s], Is.GreaterThan(0), $"Symbol {s} has zero length");
      writer.WriteBits(codes[s], lengths[s]);
    }
    writer.FlushBits();

    // Read back
    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);

    int readNum = (int)reader.ReadBits(symbolBits);
    Assert.That(readNum, Is.EqualTo(numSymbols));

    var readLengths = new int[readNum];
    int readMaxLen = 0;
    for (int i = 0; i < readNum; ++i) {
      int len = (int)reader.ReadBits(3);
      if (len == 7) {
        while (reader.ReadBits(1) == 1) len++;
      }
      readLengths[i] = len;
      if (len > readMaxLen) readMaxLen = len;
    }

    Assert.That(readLengths, Is.EqualTo(lengths.Take(numSymbols).ToArray()));

    // Build decode table and decode symbols
    int tableBits = readMaxLen;
    int tableSize = 1 << tableBits;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[tableBits + 1];
    for (int i = 0; i < readLengths.Length; ++i)
      if (readLengths[i] > 0 && readLengths[i] <= tableBits)
        blCount[readLengths[i]]++;

    var nextCode = new int[tableBits + 1];
    int code = 0;
    for (int bits = 1; bits <= tableBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (int sym = 0; sym < readLengths.Length; ++sym) {
      int len = readLengths[sym];
      if (len == 0 || len > tableBits) continue;
      int symCode = nextCode[len]++;
      int fillBits = tableBits - len;
      int baseIdx = symCode << fillBits;
      int packedValue = sym | (len << 16);
      for (int fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    var decoded = new List<int>();
    for (int i = 0; i < syms.Length; ++i) {
      reader.EnsureBits(tableBits);
      uint peekBits = reader.PeekBits(tableBits);
      int entry = table[(int)peekBits];
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
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_RepeatedWithMatches() {
    byte[] data = new byte[20];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchAtDistance5() {
    // Data: ABCDEABCDE - match at distance 5
    byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45, 0x41, 0x42, 0x43, 0x44, 0x45];
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchAtDistance10() {
    // ABCDEFGHIJABCDEFGHIJ - match at distance 10
    byte[] data = new byte[20];
    for (int i = 0; i < 10; ++i) data[i] = (byte)(0x41 + i);
    for (int i = 10; i < 20; ++i) data[i] = (byte)(0x41 + i - 10);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Debug_PositionSlotEncoding() {
    // Verify position slot encode/decode directly
    for (int dist = 0; dist < 100; ++dist) {
      int slot = LzhEncoder.GetPositionSlot(dist);
      int reconstructed;
      if (slot <= 1) {
        reconstructed = slot;
      } else {
        int extraBits = slot - 1;
        int extraValue = dist - (1 << extraBits);
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
    int[] freq = new int[300];
    freq[0x41] = 1; freq[0x42] = 1; freq[0x43] = 1; freq[0x44] = 1; freq[0x45] = 1;
    freq[0x46] = 1; freq[0x47] = 1; freq[0x48] = 1; freq[0x49] = 1; freq[0x4A] = 1;
    freq[263] = 1; // length code for match length 10

    int[] lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    uint[] codes = LzhEncoder.BuildCanonicalCodes(lengths);

    // Verify all codes are unique and decodable
    int maxLen = lengths.Max();
    int tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[maxLen + 1];
    for (int i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) blCount[lengths[i]]++;

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int bits = 1; bits <= maxLen; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    for (int sym = 0; sym < lengths.Length; ++sym) {
      int len = lengths[sym];
      if (len == 0) continue;
      int symCode = nextCode[len]++;
      int fillBits = maxLen - len;
      int baseIdx = symCode << fillBits;
      int packedValue = sym | (len << 16);
      for (int fill = 0; fill < (1 << fillBits); ++fill)
        table[baseIdx + fill] = packedValue;
    }

    // Now write each symbol and verify decode
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);
    int[] syms = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 263];
    foreach (int sym in syms) {
      Assert.That(lengths[sym], Is.GreaterThan(0), $"Symbol {sym} has no code");
      writer.WriteBits(codes[sym], lengths[sym]);
    }
    writer.FlushBits();

    ms.Position = 0;
    var reader = new BitBuffer<MsbBitOrder>(ms);
    foreach (int expectedSym in syms) {
      reader.EnsureBits(maxLen);
      uint peek = reader.PeekBits(maxLen);
      int entry = table[(int)peek];
      Assert.That(entry, Is.GreaterThanOrEqualTo(0), $"No table entry for peek={peek}");
      int decodedSym = entry & 0xFFFF;
      int codeLen = entry >> 16;
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

    uint huff = reader.ReadBits(3);
    uint extra = reader.ReadBits(3);
    uint huff2 = reader.ReadBits(2);

    Assert.That(huff, Is.EqualTo(0b101u), "Huffman code");
    Assert.That(extra, Is.EqualTo(4u), "Extra bits");
    Assert.That(huff2, Is.EqualTo(0b11u), "Second code");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_ManyLiterals() {
    byte[] data = new byte[100];
    for (int i = 0; i < 100; ++i) data[i] = (byte)i;

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_ManualEncodeDecode() {
    // Manually create an LZH stream: 3 literals (A, B, C) + match(len=3, dist=2, 0-based)
    // This should decode to: A, B, C, B, C, B

    // Code symbols: 0x41(A), 0x42(B), 0x43(C), 256(len=3, code=3-3+256=256)
    // Position: dist=2, slot=GetPositionSlot(2)=2, extraBits=1, extraValue=2-(1<<1)=0
    int[] codeFreq = new int[LzhConstants.NumCodes];
    codeFreq[0x41] = 1; codeFreq[0x42] = 1; codeFreq[0x43] = 1; codeFreq[256] = 1;

    int[] posFreq = new int[3]; // slots 0,1,2
    posFreq[2] = 1;

    int[] codeLengths = LzhEncoder.BuildCodeLengths(codeFreq, 16);
    int[] posLengths = LzhEncoder.BuildCodeLengths(posFreq, 17);
    uint[] codeCodes = LzhEncoder.BuildCanonicalCodes(codeLengths);
    uint[] posCodes = LzhEncoder.BuildCanonicalCodes(posLengths);

    // Write the stream
    using var ms = new MemoryStream();
    var writer = new BitWriter<MsbBitOrder>(ms);

    // Block size = 4 tokens (3 literals + 1 match)
    writer.WriteBits(4, 16);

    // Write code tree (single-symbol flag=0, multi)
    var codeUsed = new List<(int sym, int len)>();
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0) codeUsed.Add((i, codeLengths[i]));

    writer.WriteBits(0, 1); // multi-symbol
    writer.WriteBits((uint)codeUsed.Count, 16);
    foreach (var (sym, len) in codeUsed) {
      writer.WriteBits((uint)sym, 16);
      writer.WriteBits((uint)len, 5);
    }

    // Write pos tree (single-symbol: slot 2)
    writer.WriteBits(1, 1); // single-symbol
    writer.WriteBits(2, 16); // symbol = 2

    // Write tokens:
    // Literal A
    writer.WriteBits(codeCodes[0x41], codeLengths[0x41]);
    // Literal B
    writer.WriteBits(codeCodes[0x42], codeLengths[0x42]);
    // Literal C
    writer.WriteBits(codeCodes[0x43], codeLengths[0x43]);
    // Match: len code 256
    writer.WriteBits(codeCodes[256], codeLengths[256]);
    // Position: slot 2 (single-symbol, no Huffman bits needed), extra: 1 bit, value 0
    writer.WriteBits(0, 1); // extra bit for slot 2 (extraBits = 2-1 = 1)

    writer.FlushBits();

    // Decode
    ms.Position = 0;
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(6); // A,B,C,B,C,B

    Assert.That(result, Is.EqualTo(new byte[] { 0x41, 0x42, 0x43, 0x41, 0x42, 0x43 }));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Debug_MatchDistance10_Manual() {
    // The encoder produces this for ABCDEFGHIJABCDEFGHIJ:
    // 10 literals + 1 match(len=10, dist=9 0-based)
    // Let me verify the encoder output matches what the decoder expects
    byte[] data = new byte[20];
    for (int i = 0; i < 10; ++i) data[i] = (byte)(0x41 + i);
    for (int i = 10; i < 20; ++i) data[i] = (byte)(0x41 + i - 10);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    // Manually read the stream to trace
    using var ms = new MemoryStream(compressed);
    var bits = new BitBuffer<MsbBitOrder>(ms);

    // Block size
    uint blockSize = bits.ReadBits(16);
    Assert.That(blockSize, Is.EqualTo(11u), "Should be 11 tokens (10 lit + 1 match)");

    // Code tree
    uint codeFlag = bits.ReadBits(1);
    Assert.That(codeFlag, Is.EqualTo(0u), "Multi-symbol code tree");
    uint codeCount = bits.ReadBits(16);
    var codeEntries = new (int sym, int len)[codeCount];
    for (int i = 0; i < (int)codeCount; ++i) {
      int sym = (int)bits.ReadBits(16);
      int len = (int)bits.ReadBits(5);
      codeEntries[i] = (sym, len);
    }

    // Position tree
    uint posFlag = bits.ReadBits(1);
    // Could be single or multi, let's check
    int posSlot;
    if (posFlag == 1) {
      posSlot = (int)bits.ReadBits(16);
    } else {
      uint posCount = bits.ReadBits(16);
      posSlot = -1;
      // Read pos tree entries and build table...
      // Skip for now
    }

    // Now we know the structure. Let's use the actual decoder instead.
    ms.Position = 0;
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);

    for (int i = 0; i < data.Length; ++i) {
      if (result[i] != data[i]) {
        Assert.Fail($"Mismatch at index {i}: expected {data[i]} (0x{data[i]:X2}), got {result[i]} (0x{result[i]:X2})");
      }
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(30)]
  public void Debug_PatternData_Sizes(int size) {
    byte[] data = new byte[size];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(100)]
  [TestCase(200)]
  [TestCase(500)]
  public void Debug_RandomData_Sizes(int size) {
    var rng = new Random(42);
    byte[] data = new byte[size];
    rng.NextBytes(data);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    byte[] compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    byte[] result = decoder.Decode(data.Length);
    Assert.That(result, Is.EqualTo(data));
  }
}
