#pragma warning disable CS1591

using System.Buffers.Binary;

namespace FileFormat.QuickLz;

/// <summary>
/// QuickLZ level-1 stream format by Lasse Mikkel Reinhold.
///
/// Header layout (9-byte long form, always used here):
///   byte 0      flags: 0x47 = compressed, 0x46 = stored (level 1, long header, bit6=1)
///   uint32 LE   compressed size   (includes the 9-byte header)
///   uint32 LE   decompressed size
///
/// Payload encoding (after header):
///   Control words are 32-bit LE values written before each group of up to 31 tokens.
///   Bit 31 is a sentinel (always 1). The remaining bits describe tokens from LSB upward:
///     0 = literal  → 1 raw byte follows
///     1 = match    → 2-byte LE offset + 1-byte (length - 3) follow; min match = 3
///   If compressed output >= original, the block is stored uncompressed (flag 0x46).
/// </summary>
public static class QuickLzStream {

  private const byte FlagCompressed   = 0x47; // level 1 | long header | bit6 | compressed
  private const byte FlagUncompressed = 0x46; // level 1 | long header | bit6 | stored
  private const int  HeaderSize       = 9;
  private const int  HashBits         = 12;
  private const int  HashSize         = 1 << HashBits;   // 4096
  private const int  HashMask         = HashSize - 1;
  private const int  MinMatch         = 3;
  private const int  MaxMatch         = 255 + MinMatch;  // length byte is (len-3), max 255 → len 258
  private const int  MaxDistance      = 65535;            // offset is uint16

  /// <summary>Compresses <paramref name="input"/> into QuickLZ format and writes to <paramref name="output"/>.</summary>
  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var src = ms.ToArray();

    var compressed = CompressData(src);

    // If compression doesn't help, store uncompressed
    if (compressed.Length >= src.Length) {
      WriteHeader(output, FlagUncompressed, src.Length + HeaderSize, src.Length);
      output.Write(src);
    } else {
      WriteHeader(output, FlagCompressed, compressed.Length + HeaderSize, src.Length);
      output.Write(compressed);
    }
  }

  /// <summary>Decompresses a QuickLZ stream from <paramref name="input"/> and writes to <paramref name="output"/>.</summary>
  public static void Decompress(Stream input, Stream output) {
    Span<byte> hdr = stackalloc byte[HeaderSize];
    input.ReadExactly(hdr);

    var flags        = hdr[0];
    var compSize     = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr[1..]) - HeaderSize;
    var decompSize   = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr[5..]);

    if ((flags & 0x40) == 0)
      throw new InvalidDataException("Not a QuickLZ stream: bit 6 not set in flags byte.");

    bool isCompressed = (flags & 0x01) != 0;

    if (!isCompressed) {
      // Stored: payload is the raw data
      if (compSize < 0)
        throw new InvalidDataException("Invalid QuickLZ header: negative payload size.");
      var buf = new byte[compSize];
      input.ReadExactly(buf);
      output.Write(buf);
      return;
    }

    var payload = new byte[compSize];
    input.ReadExactly(payload);
    var result = DecompressData(payload, decompSize);
    output.Write(result);
  }

  // ── Compression ──────────────────────────────────────────────────────────

  private static byte[] CompressData(byte[] src) {
    if (src.Length == 0)
      return [];

    // Hash table: hash → position of most recent match
    var hashTable = new int[HashSize];
    Array.Fill(hashTable, -1);

    using var dst = new MemoryStream(src.Length);

    // Control word: bits 0..N-1 are token types (0=literal, 1=match),
    // bit N is the sentinel (always 1). Decompressor shifts right through
    // token bits; when cw==1, sentinel reached = group done. Max 31 tokens.
    var controlPos   = 0L;
    uint controlBits = 0;
    var tokenCount   = 0;

    // Reusable buffers hoisted out of loops (stackalloc inside loops triggers CA2014)
    Span<byte> zeroes4 = stackalloc byte[4] { 0, 0, 0, 0 };
    Span<byte> tok     = stackalloc byte[3];

    // Reserve space for the first control word
    dst.Write(zeroes4);
    controlPos = 0;

    var i = 0;
    while (i < src.Length) {
      if (tokenCount == 31) {
        // Flush: sentinel at bit 31
        FlushControl(dst, controlPos, controlBits | (1u << 31));
        controlPos = dst.Position;
        dst.Write(zeroes4);
        controlBits = 0;
        tokenCount  = 0;
      }

      // Try to find a match
      int matchLen = 0, matchOffset = 0;

      if (i + MinMatch <= src.Length) {
        var h = Hash3(src, i);
        var candidate = hashTable[h];
        hashTable[h] = i;

        if (candidate >= 0 && i - candidate <= MaxDistance) {
          var maxLen = Math.Min(MaxMatch, src.Length - i);
          var len = 0;
          while (len < maxLen && src[candidate + len] == src[i + len])
            len++;
          if (len >= MinMatch) {
            matchLen    = len;
            matchOffset = i - candidate;
          }
        }
      }

      if (matchLen >= MinMatch) {
        // Match token: set bit at current position
        controlBits |= (1u << tokenCount);
        BinaryPrimitives.WriteUInt16LittleEndian(tok, (ushort)matchOffset);
        tok[2] = (byte)(matchLen - MinMatch);
        dst.Write(tok);

        for (var k = 1; k < matchLen; k++) {
          if (i + k + MinMatch <= src.Length)
            hashTable[Hash3(src, i + k)] = i + k;
        }
        i += matchLen;
      } else {
        // Literal token: bit stays 0
        dst.WriteByte(src[i]);
        i++;
      }

      tokenCount++;
    }

    // Flush final partial group: sentinel at bit tokenCount
    FlushControl(dst, controlPos, controlBits | (1u << tokenCount));

    return dst.ToArray();
  }

  private static void FlushControl(MemoryStream dst, long pos, uint controlWord) {
    var saved = dst.Position;
    dst.Position = pos;
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, controlWord);
    dst.Write(buf);
    dst.Position = saved;
  }

  private static int Hash3(byte[] data, int pos) {
    // 3-byte hash into HashBits bits
    var v = (uint)data[pos] | ((uint)data[pos + 1] << 8) | ((uint)data[pos + 2] << 16);
    return (int)((v * 0x9E3779B1u) >> (32 - HashBits)) & HashMask;
  }

  // ── Decompression ────────────────────────────────────────────────────────

  private static byte[] DecompressData(byte[] src, int decompSize) {
    if (decompSize == 0)
      return [];

    var dst = new byte[decompSize];
    var si  = 0;
    var di  = 0;

    while (di < decompSize && si < src.Length) {
      // Read control word
      if (si + 4 > src.Length)
        throw new InvalidDataException("QuickLZ: truncated control word.");
      var cw = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(si));
      si += 4;

      // Process tokens: shift right through bits, sentinel is the last remaining 1-bit.
      // When cw == 1, all tokens have been consumed.
      while (cw != 1 && di < decompSize && si < src.Length) {
        if ((cw & 1) == 0) {
          // Literal
          dst[di++] = src[si++];
        } else {
          // Match: 2-byte offset + 1-byte (len-3)
          if (si + 3 > src.Length)
            throw new InvalidDataException("QuickLZ: truncated match token.");
          var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(si));
          var length = src[si + 2] + MinMatch;
          si += 3;

          if (offset == 0 || di - offset < 0)
            throw new InvalidDataException("QuickLZ: invalid match offset.");

          var srcPos = di - offset;
          for (var k = 0; k < length && di < decompSize; k++)
            dst[di++] = dst[srcPos + k];
        }
        cw >>= 1;
      }
    }

    return dst;
  }

  // ── Header helpers ───────────────────────────────────────────────────────

  private static void WriteHeader(Stream output, byte flags, int totalCompSize, int decompSize) {
    Span<byte> hdr = stackalloc byte[HeaderSize];
    hdr[0] = flags;
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[1..], (uint)totalCompSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[5..], (uint)decompSize);
    output.Write(hdr);
  }
}
