namespace Compression.Core.Dictionary.Lzham;

/// <summary>
/// LZHAM decoder: reconstructs data from Huffman-coded LZ77 token stream.
/// </summary>
public sealed class LzhamDecoder {
  /// <summary>
  /// Decompresses LZHAM-encoded data.
  /// </summary>
  public byte[] Decode(byte[] compressed, int originalSize) {
    if (originalSize == 0) return [];

    var reader = new BitReader(compressed);

    // Read code lengths.
    var litLenCodeLen = new int[286];
    for (var i = 0; i < 286; i++)
      litLenCodeLen[i] = reader.ReadBits(4);

    var distCodeLen = new int[30];
    for (var i = 0; i < 30; i++)
      distCodeLen[i] = reader.ReadBits(4);

    // Build decode tables.
    var litLenCodes = LzhamEncoder.BuildCanonicalCodes(litLenCodeLen);
    var distCodes = LzhamEncoder.BuildCanonicalCodes(distCodeLen);

    var output = new byte[originalSize];
    var outPos = 0;

    while (outPos < originalSize) {
      var sym = DecodeSymbol(reader, litLenCodes, litLenCodeLen);

      if (sym < 256) {
        output[outPos++] = (byte)sym;
      } else {
        var length = DecodeLength(sym, reader);
        var distSym = DecodeSymbol(reader, distCodes, distCodeLen);
        var distance = DecodeDistance(distSym, reader);

        for (var i = 0; i < length; i++)
          output[outPos + i] = output[outPos - distance + i];
        outPos += length;
      }
    }

    return output;
  }

  private static int DecodeSymbol(BitReader reader, (uint code, int len)[] codes, int[] codeLens) {
    var code = 0u;
    var maxLen = codeLens.Max();
    if (maxLen == 0) throw new InvalidDataException("Empty Huffman table.");

    for (var len = 1; len <= maxLen; len++) {
      code = (code << 1) | (uint)reader.ReadBit();
      for (var sym = 0; sym < codes.Length; sym++) {
        if (codeLens[sym] == len && codes[sym].code == code)
          return sym;
      }
    }

    throw new InvalidDataException("Invalid Huffman code.");
  }

  private static int DecodeLength(int code, BitReader reader) => code switch {
    >= 257 and <= 264 => code - 254,
    >= 265 and <= 268 => 11 + (code - 265) * 2 + reader.ReadBits(1),
    >= 269 and <= 272 => 19 + (code - 269) * 4 + reader.ReadBits(2),
    >= 273 and <= 276 => 35 + (code - 273) * 8 + reader.ReadBits(3),
    >= 277 and <= 280 => 67 + (code - 277) * 16 + reader.ReadBits(4),
    >= 281 and <= 284 => 131 + (code - 281) * 32 + reader.ReadBits(5),
    285 => 258,
    _ => throw new InvalidDataException($"Invalid length code: {code}")
  };

  private static int DecodeDistance(int code, BitReader reader) {
    if (code <= 1) return code + 1;
    var extra = (code - 2) / 2;
    var baseDist = (2 + (code & 1)) << extra;
    return baseDist + reader.ReadBits(extra) + 1;
  }

  internal sealed class BitReader(byte[] data) {
    private int _bitPos;

    public int ReadBit() {
      if (_bitPos / 8 >= data.Length) return 0;
      var bit = (data[_bitPos / 8] >> (7 - (_bitPos % 8))) & 1;
      _bitPos++;
      return bit;
    }

    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; i++)
        value = (value << 1) | ReadBit();
      return value;
    }
  }
}
