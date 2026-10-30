using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzav;

/// <summary>
/// LZAV data format 3, exactly as Aleksey Vaneev's LZAV 5.20 writes and reads it.
/// </summary>
/// <remarks>
/// <para>
/// Converted from <c>lzav.h</c> (MIT; see THIRD-PARTY-NOTICE.lzav.txt): <c>lzav_compress</c>
/// with <c>lzav_compress_default</c>'s choice of minimum reference length (5 below 256 KiB,
/// else 6), <c>lzav_write_blk_3</c>, <c>lzav_write_fin_3</c> and <c>lzav_decompress_3</c>. The
/// C version copies in 8- and 16-byte words and leans on buffer padding; this one copies byte
/// by byte. The result is the same stream.
/// </para>
/// <para>
/// A stream is a prefix byte (format 3 in the high nibble, the minimum reference length in the
/// low one), then blocks: literal blocks <c>CC00LLLL</c> and reference blocks
/// <c>OO01RRRR</c>, <c>OO10RRRR</c> and <c>OO11RRRR</c> with 10-, 15- and 21-bit offsets. A
/// length nibble of 0 means 16 plus base-128 continuation bytes. A reference's high offset bits
/// are carried ahead: two bits in the next literal block header, and three or five in the top
/// of the reference's own last offset byte, so the window grows beyond 21 bits. A literal block
/// is always followed by a reference block, and the stream ends with at least nine literals.
/// </para>
/// </remarks>
public static class LzavCodec {
  private const int FormatCurrent = 3;
  private const int LiteralsAtEnd = 9;
  private const int OffsetMin = 8;
  private const int OffsetThreshold1 = (1 << 10) - 1;
  private const int OffsetThreshold2 = (1 << 15) - 1;
  private const long WindowLength = 1 << 21;
  private const int Mref5Threshold = 1 << 18;

  private static ReadOnlySpan<int> CarryShift => [0, 0, 3, 5];

  // ---------- compression ----------

  /// <summary>Compresses <paramref name="data"/> the way <c>lzav_compress_default</c> does.</summary>
  /// <returns>The LZAV stream; empty for empty input (LZAV defines no stream for it).</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var mref = data.Length < Mref5Threshold ? 5 : 6;
    var mref1 = mref - 1;
    // Worst case: every byte a literal; 16 bytes of slack for the 4-byte offset write.
    var output = new byte[data.Length + data.Length / 15 + 64];
    var op = 0;
    output[op++] = (byte)(FormatCurrent << 4 | mref);

    if (data.Length < 16) {
      output[op++] = (byte)data.Length;
      data.CopyTo(output.AsSpan(op));
      // Fewer than nine bytes: the decoder still needs nine literal bytes to be there.
      return output[..(2 + Math.Max(data.Length, LiteralsAtEnd))];
    }

    var htsize = (1 << 7) * sizeof(uint) * 4;
    const int htsizeMax = 1 << 20;
    while ((htsize >> 2) < data.Length && (htsize << 1) <= htsizeMax)
      htsize <<= 1;

    var table = new uint[htsize / sizeof(uint)];
    var hmask = (uint)((htsize - 1) ^ 7);
    var length = data.Length;
    var ipe = length - LiteralsAtEnd;
    var ipet = ipe - 15 + LiteralsAtEnd;
    var ipa = 0;

    // Every bucket starts as (the four bytes at offset 1, offset 1).
    var initial = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
    for (var i = 0; i < table.Length; i += 2) {
      table[i] = initial;
      table[i + 1] = 1;
    }

    var carryByte = op - 1;   // the prefix byte takes the first, always empty, carry
    var carryShift = 0;
    ulong averageMatch = 100UL << 17;
    var ip = 2;

    while (ip < ipet) {
      var iw1 = BinaryPrimitives.ReadUInt32LittleEndian(data[ip..]);
      var iw2 = LoadW2(data, ip + 4, mref);
      var bucket = (int)(Hash(iw1, iw2, hmask) / sizeof(uint));
      var ipo = (uint)ip;

      bool found;
      uint wpo = 0;
      if (iw1 != table[bucket])
        found = false;
      else {
        wpo = table[bucket + 1];
        found = LoadW2(data, (int)wpo + 4, mref) == iw2;
      }

      if (!found) {
        var wp = ip;
        table[bucket] = iw1;
        averageMatch -= averageMatch >> 11;
        ++ip;
        table[bucket + 1] = ipo;
        if (averageMatch < 200 << 10 && wp != ipa) {
          ip += 1 + (int)(ipo & 1) + (averageMatch < 130 << 10 ? 1 : 0);
          if (averageMatch < 100 << 10)
            ip += 100 - (int)(averageMatch >> 10);
        }
        continue;
      }

      var d = (long)ipo - wpo;
      var ml = ipe - ip;
      if (d < OffsetMin) {
        ++ip;
        continue;
      }

      var wp2 = (int)wpo;
      table[bucket + 1] = d > 31 ? ipo : wpo;
      var rc = MatchLength(data, ip, wp2, ml, mref);
      var ip0 = ip;
      var lc = ip - ipa;

      if (lc != 0 && data[ip - 1] == data[wp2 - 1]) {
        var back = MatchLengthBackward(data, ip, wp2, Math.Min(lc, wp2));
        lc -= back;
        rc += back;
        ip -= back;
      }

      if (d < WindowLength << carryShift << (lc != 0 ? 2 : 0)) {
        // Hash the two positions the match skipped.
        iw1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(ip0 + 2)..]);
        iw2 = LoadW2(data, ip0 + 6, mref);
        bucket = (int)(Hash(iw1, iw2, hmask) / sizeof(uint));
        ipo += 2;
        averageMatch -= averageMatch >> 10;
        table[bucket] = iw1;
        ip += rc;
        var anchor = ipa;
        table[bucket + 1] = ipo;

        iw1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(ip0 + 4)..]);
        iw2 = LoadW2(data, ip0 + 8, mref);
        bucket = (int)(Hash(iw1, iw2, hmask) / sizeof(uint));
        ipo += 2;
        table[bucket] = iw1;
        ipa = ip;
        averageMatch += (ulong)rc << 7;
        table[bucket + 1] = ipo;

        op = WriteBlock(output, op, lc, rc, d, data, anchor, ref carryByte, ref carryShift, mref1);
        continue;
      }

      ip = ip0 + 1;
    }

    op = WriteFinal(output, op, ipe - ipa + LiteralsAtEnd, data, ipa);
    return output[..op];
  }

  private static uint LoadW2(ReadOnlySpan<byte> data, int at, int mref)
    => mref == 5 ? data[at] : BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

  private static uint Hash(uint iw1, uint iw2, uint hmask) {
    var seed = 0x243F6A88u ^ iw1;
    var value = (0x85A308D3u ^ iw2) * seed;
    return (value >> 12) & hmask;
  }

  // lzav_match_len: matching bytes from offset o on (the first o are known to match), up to ml.
  private static int MatchLength(ReadOnlySpan<byte> data, int p1, int p2, int ml, int o) {
    if (o >= ml)
      return ml;
    while (o < ml && data[p1 + o] == data[p2 + o])
      ++o;
    return o;
  }

  // lzav_match_len_r1: matching bytes going backwards from -1 (known to match), up to ml.
  private static int MatchLengthBackward(ReadOnlySpan<byte> data, int p1, int p2, int ml) {
    var n = 1;
    while (n < ml && data[p1 - 1 - n] == data[p2 - 1 - n])
      ++n;
    return n;
  }

  private static int WriteBlock(byte[] output, int op, int lc, int rc, long d, ReadOnlySpan<byte> data, int anchor, ref int carryByte, ref int carryShift, int mref1) {
    // The bits the previous reference block's carry byte has room for.
    rc -= mref1;
    var dc = (d << 8) >> carryShift;
    d >>= carryShift;
    output[carryByte] |= (byte)dc;

    if (lc != 0) {
      var cv = d << 6;  // two more offset bits ride in the literal block header
      d >>= 2;
      if (lc < 16) {
        output[op++] = (byte)(cv | (uint)lc);
      } else if (lc < 33) {
        output[op++] = (byte)cv;
        output[op++] = (byte)(lc - 16);
      } else {
        output[op++] = (byte)cv;
        var lcw = lc - 16;
        while (lcw > 127) {
          output[op++] = (byte)(0x80 | (lcw & 0x7F));
          lcw >>= 7;
        }
        output[op++] = (byte)lcw;
      }

      data.Slice(anchor, lc).CopyTo(output.AsSpan(op));
      op += lc;
    }

    var bt = 1 + (d > OffsetThreshold1 ? 1 : 0) + (d > OffsetThreshold2 ? 1 : 0);
    carryShift = CarryShift[bt];
    carryByte = op + bt;
    var header = (uint)(d << 6 | (uint)bt << 4);
    if (rc < 16)
      header |= (uint)rc;

    for (var i = 0; i <= bt; ++i)
      output[op + i] = (byte)(header >> (8 * i));
    op += bt + 1;

    if (rc < 16)
      return op;

    rc -= 16;
    while (rc > 127) {
      output[op++] = (byte)(0x80 | (rc & 0x7F));
      rc >>= 7;
    }
    output[op++] = (byte)rc;
    return op;
  }

  private static int WriteFinal(byte[] output, int op, int lc, ReadOnlySpan<byte> data, int anchor) {
    var lcw = lc;
    if (lc > 15) {
      output[op++] = 0;
      lcw -= 16;
      while (lcw > 127) {
        output[op++] = (byte)(0x80 | (lcw & 0x7F));
        lcw >>= 7;
      }
    }

    output[op++] = (byte)lcw;
    data.Slice(anchor, lc).CopyTo(output.AsSpan(op));
    return op + lc;
  }

  // ---------- decompression ----------

  /// <summary>Decompresses a format-3 LZAV stream to exactly <paramref name="length"/> bytes.</summary>
  /// <exception cref="InvalidDataException">
  /// The stream is shorter than LZAV's minimum, names another format or minimum reference
  /// length, references data outside the output, overruns its input or output, or decodes to a
  /// different length.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> src, int length) {
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    if (length == 0)
      return src.IsEmpty ? [] : throw new InvalidDataException("LZAV: data for an empty output.");
    if (src.Length < 11)
      throw new InvalidDataException("LZAV: stream is shorter than 11 bytes.");
    if (src[0] >> 4 != FormatCurrent)
      throw new InvalidDataException($"LZAV: data format {src[0] >> 4} is not supported (only 3).");

    var mref1 = (src[0] & 15) - 1;
    if (mref1 is < 0 or > 5)
      throw new InvalidDataException($"LZAV: minimum reference length {src[0] & 15} is invalid.");

    var output = new byte[length];
    var ipet = src.Length - LiteralsAtEnd;
    var ip = 1;
    int bh = src[ip];
    long cv = 0;
    var csh = 0;
    var op = 0;
    var referenceNext = false;

    while (ip < ipet) {
      if (referenceNext || (bh & 0x30) != 0) {
        referenceNext = false;
        ++ip;
        var bt = (bh >> 4) & 3;
        var bv = ReadU32(src, ip);
        long mask = bt switch { 1 => 0x3FF, 2 => 0x7FFF, 3 => 0x1FFFFF, _ => 0 };
        var x = ((long)bv << 2 | (uint)(bh >> 6)) & mask;
        var d = x << csh | cv;
        csh = CarryShift[bt];
        cv = bt == 0 ? 0 : ((long)ByteAt(src, ip + bt - 1) << csh) >> 8;
        if (d > op)
          throw new InvalidDataException($"LZAV: reference distance {d} reaches before the start at {op}.");

        var source = op - (int)d;
        ip += bt;
        long count = mref1;
        var cc = bh & 15;
        if (cc != 0) {
          count += cc;
          bh = ByteAt(src, ip);
        } else {
          int first = ByteAt(src, ip++);
          count += first & 0x7F;
          if ((first & 0x80) != 0)
            count += ReadVarint(src, ref ip);
          count += 16;
          bh = ByteAt(src, ip);
        }

        if (d < OffsetMin)
          throw new InvalidDataException($"LZAV: reference distance {d} is below {OffsetMin}.");
        if (length - op - count < 8)
          throw new InvalidDataException("LZAV: reference runs into the final literals or past the output.");

        for (var i = 0; i < count; ++i)
          output[op++] = output[source++];
        continue;
      }

      long ncv = bh >> 6;
      ++ip;
      long literals = bh & 15;
      if (literals == 0) {
        int b = ByteAt(src, ip++);
        literals = (b & 0x7F) + 16;
        if ((b & 0x80) != 0)
          literals += ReadVarint(src, ref ip);
      }

      cv |= ncv << csh;
      csh += 2;
      var start = ip;
      if (op + literals > length)
        throw new InvalidDataException("LZAV: literal block runs past the output.");
      if (start + literals > src.Length)
        throw new InvalidDataException("LZAV: literal block runs past the input.");
      ip = start + (int)literals;
      src.Slice(start, (int)literals).CopyTo(output.AsSpan(op));
      op += (int)literals;

      if (ip < ipet) {
        bh = src[ip];
        referenceNext = true;
        continue;
      }

      break;
    }

    if (op != length)
      throw new InvalidDataException($"LZAV: stream decoded to {op} bytes, expected {length}.");
    return output;
  }

  // LZAV_LOAD_VARINT with shift 7: up to three more base-128 groups, low first.
  private static long ReadVarint(ReadOnlySpan<byte> src, ref int ip) {
    long value = 0;
    var shift = 7;
    int b;
    do {
      b = ByteAt(src, ip++);
      value += (long)(b & 0x7F) << shift;
      if (shift == 7 + 21)
        break;
      shift += 7;
    } while ((b & 0x80) != 0);
    return value;
  }

  private static byte ByteAt(ReadOnlySpan<byte> src, int at)
    => at < src.Length ? src[at] : throw new InvalidDataException("LZAV: block is truncated.");

  private static uint ReadU32(ReadOnlySpan<byte> src, int at) {
    uint value = 0;
    for (var i = 0; i < 4 && at + i < src.Length; ++i)
      value |= (uint)src[at + i] << (8 * i);
    return value;
  }
}
