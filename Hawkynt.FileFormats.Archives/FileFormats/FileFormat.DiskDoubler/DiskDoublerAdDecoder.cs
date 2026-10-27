using System.Buffers.Binary;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Decodes DiskDoubler's "AD" methods (6 = ADS/AD2, 9 = AD1): a sequence of independent LZSS blocks
/// of at most 8 KiB each.
/// </summary>
/// <remarks>
/// <para>Each block opens with a 12-byte header:</para>
/// <code>
///   [0..2]  compressed size (bytes after this header)   [2..4] uncompressed size (≤ 0x2000)
///   [4..8]  unused here      [8] data check (unverified)  [9] flags (bit 0 = stored block)
///   [10]    unused here      [11] XOR of header bytes 0..11 (bytes 0-1 and 2-3 folded as 16-bit values)
/// </code>
/// <para>A compressed block is an MSB-first bit stream of tokens: 0 + 8 bits is a literal; 1 is a match,
/// followed by a "far" bit selecting a 12- or 8-bit distance, then the length: 0 → 2, 100 → 3,
/// 101 → 4, 11 + 4 bits → 5-20. Matches never reach outside their own block.</para>
/// <para>Sourcing: converted from The Unarchiver's XADMaster (<c>XADDiskDoublerADnHandle.m</c>,
/// LGPL-2.1-or-later); checked against real DiskDoubler files, decoding them as unar does.</para>
/// </remarks>
internal static class DiskDoublerAdDecoder {
  private const int HeaderSize = 12;
  private const int MaxBlock = 0x2000;

  public static byte[] Decode(ReadOnlySpan<byte> input, long originalSize) {
    var output = new byte[checked((int)originalSize)];
    var outPos = 0;
    var inPos = 0;

    while (outPos < output.Length) {
      if (input.Length - inPos < HeaderSize)
        throw new InvalidDataException("DiskDoubler AD stream ends inside a block header.");
      var header = input.Slice(inPos, HeaderSize);
      var compSize = BinaryPrimitives.ReadUInt16BigEndian(header);
      var uncompSize = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
      var check = (byte)(compSize ^ (compSize >> 8) ^ uncompSize ^ (uncompSize >> 8));
      for (var i = 4; i < 11; ++i)
        check ^= header[i];
      if (check != header[11])
        throw new InvalidDataException("DiskDoubler AD block header fails its XOR check.");
      if (uncompSize > MaxBlock)
        throw new InvalidDataException($"DiskDoubler AD block claims {uncompSize} bytes; blocks hold at most {MaxBlock}.");
      if (uncompSize > output.Length - outPos)
        throw new InvalidDataException("DiskDoubler AD block runs past the fork's declared size.");

      inPos += HeaderSize;
      if (compSize > input.Length - inPos)
        throw new InvalidDataException("DiskDoubler AD block runs past the end of the fork.");
      var block = input.Slice(inPos, compSize);
      var target = output.AsSpan(outPos, uncompSize);

      if ((header[9] & 1) != 0) {
        if (compSize < uncompSize)
          throw new InvalidDataException("DiskDoubler AD stored block is shorter than its size.");
        block[..uncompSize].CopyTo(target);
      } else
        DecodeBlock(block, target);

      outPos += uncompSize;
      inPos += compSize;
    }

    return output;
  }

  private static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> output) {
    var bitPos = 0;
    var pos = 0;

    int Bit(ReadOnlySpan<byte> data) {
      var index = bitPos >> 3;
      if (index >= data.Length)
        throw new InvalidDataException("DiskDoubler AD block ends before its output is complete.");
      var bit = (data[index] >> (7 - (bitPos & 7))) & 1;
      ++bitPos;
      return bit;
    }

    int Bits(ReadOnlySpan<byte> data, int count) {
      var value = 0;
      for (var i = 0; i < count; ++i)
        value = (value << 1) | Bit(data);
      return value;
    }

    while (pos < output.Length) {
      if (Bit(block) == 0) {
        output[pos++] = (byte)Bits(block, 8);
        continue;
      }

      var offset = Bits(block, Bit(block) != 0 ? 12 : 8);
      int length;
      if (Bit(block) == 0)
        length = 2;
      else if (Bit(block) == 0)
        length = Bit(block) == 0 ? 3 : 4;
      else
        length = Bits(block, 4) + 5;

      if (offset == 0 || offset > pos)
        throw new InvalidDataException("DiskDoubler AD match reaches before the start of its block.");
      length = Math.Min(length, output.Length - pos);
      for (var i = 0; i < length; ++i, ++pos)
        output[pos] = output[pos - offset];
    }
  }
}
