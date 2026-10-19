namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 13 ("LZ+Huffman", the default of StuffIt 3-5): LZSS over a 64 KiB window with
/// two literal/length codes (one used after a literal, the other after a match) and a distance
/// code, all read least-significant bit first. The first byte selects either one of five
/// built-in code sets or codes transmitted with a 37-symbol meta code.
/// </summary>
/// <remarks>Converted from XADMaster's XADStuffIt13Handle (LGPL-2.1-or-later); tables copied verbatim.</remarks>
internal static class StuffIt13Decoder {
  private const int WindowSize = 65536;

  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    if (outputLength == 0) return output;
    var reader = new StuffItBitReader(input);

    var selector = reader.ReadByte();
    var set = selector >> 4;
    StuffItPrefixCode first, second, offsets;
    if (set == 0) {
      var meta = new StuffItPrefixCode();
      for (var i = 0; i < MetaCodes.Length; ++i) meta.AddLowBitFirst(i, (uint)MetaCodes[i], MetaCodeLengths[i]);
      first = ReadCode(reader, meta, 321);
      second = (selector & 0x08) != 0 ? first : ReadCode(reader, meta, 321);
      offsets = ReadCode(reader, meta, (selector & 0x07) + 10);
    } else if (set < 6) {
      first = StuffItPrefixCode.FromLengths(FirstCodeLengths[set - 1]);
      second = StuffItPrefixCode.FromLengths(SecondCodeLengths[set - 1]);
      offsets = StuffItPrefixCode.FromLengths(OffsetCodeLengths[set - 1]);
    } else {
      throw new InvalidDataException($"StuffIt method 13 code set {set} is invalid.");
    }

    var current = first;
    var o = 0;
    while (o < outputLength) {
      var symbol = current.DecodeLe(reader);
      if (symbol < 0x100) {
        output[o++] = (byte)symbol;
        current = first;
        continue;
      }
      current = second;
      int length;
      if (symbol < 0x13E) length = symbol - 0x100 + 3;
      else if (symbol == 0x13E) length = reader.ReadBitsLe(10) + 65;
      else if (symbol == 0x13F) length = reader.ReadBitsLe(15) + 65;
      else break; // 0x140: end of stream

      var bitLength = offsets.DecodeLe(reader);
      var distance = bitLength switch {
        0 => 1,
        1 => 2,
        _ => (1 << (bitLength - 1)) + reader.ReadBitsLe(bitLength - 1) + 1,
      };
      if (distance > WindowSize)
        throw new InvalidDataException("StuffIt method 13 match reaches past the 64 KiB window.");
      // The window starts zero-filled, so a match reaching before the first byte copies zeros.
      for (var k = 0; k < length && o < outputLength; ++k, ++o) output[o] = o >= distance ? output[o - distance] : (byte)0;
    }
    if (o != outputLength) throw new InvalidDataException("StuffIt method 13 data ended before the recorded size.");
    return output;
  }

  private static StuffItPrefixCode ReadCode(StuffItBitReader reader, StuffItPrefixCode meta, int count) {
    var lengths = new int[count];
    var length = 0;
    for (var i = 0; i < count; ++i) {
      var value = meta.DecodeLe(reader);
      switch (value) {
        case 31: length = -1; break;
        case 32: ++length; break;
        case 33: --length; break;
        case 34:
          if (reader.ReadBitLe() != 0) Set(lengths, i++, length);
          break;
        case 35:
          for (var n = reader.ReadBitsLe(3) + 2; n > 0; --n) Set(lengths, i++, length);
          break;
        case 36:
          for (var n = reader.ReadBitsLe(6) + 10; n > 0; --n) Set(lengths, i++, length);
          break;
        default:
          length = value + 1;
          break;
      }
      Set(lengths, i, length);
    }
    return StuffItPrefixCode.FromLengths(lengths);
  }

  private static void Set(int[] lengths, int index, int length) {
    if ((uint)index >= (uint)lengths.Length)
      throw new InvalidDataException("StuffIt method 13 code-length run overflows the table.");
    lengths[index] = length;
  }

  // ── tables (verbatim from XADStuffIt13Handle.m) ──────────────────────

  private static readonly int[] MetaCodes = [
    0x5d8, 0x058, 0x040, 0x0c0, 0x000, 0x078, 0x02b, 0x014,
    0x00c, 0x01c, 0x01b, 0x00b, 0x010, 0x020, 0x038, 0x018,
    0x0d8, 0xbd8, 0x180, 0x680, 0x380, 0xf80, 0x780, 0x480,
    0x080, 0x280, 0x3d8, 0xfd8, 0x7d8, 0x9d8, 0x1d8, 0x004,
    0x001, 0x002, 0x007, 0x003, 0x008,
  ];

  private static readonly int[] MetaCodeLengths = [
    11, 8, 8, 8, 8, 7, 6, 5, 5, 5, 5, 6, 5, 6, 7, 7, 9, 12, 10, 11, 11, 12,
    12, 11, 11, 11, 12, 12, 12, 12, 12, 5, 2, 2, 3, 4, 5,
  ];

  private static readonly int[][] FirstCodeLengths = [StuffIt13Tables.First1, StuffIt13Tables.First2, StuffIt13Tables.First3, StuffIt13Tables.First4, StuffIt13Tables.First5];
  private static readonly int[][] SecondCodeLengths = [StuffIt13Tables.Second1, StuffIt13Tables.Second2, StuffIt13Tables.Second3, StuffIt13Tables.Second4, StuffIt13Tables.Second5];
  private static readonly int[][] OffsetCodeLengths = [StuffIt13Tables.Offset1, StuffIt13Tables.Offset2, StuffIt13Tables.Offset3, StuffIt13Tables.Offset4, StuffIt13Tables.Offset5];
}
