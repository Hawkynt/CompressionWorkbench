namespace FileFormat.Lzfse;

/// <summary>
/// Implements the LZVN compression and decompression algorithm used within LZFSE blocks.
/// </summary>
/// <remarks>
/// <para>
/// LZVN is a byte-oriented LZ77 variant developed by Apple. It uses variable-length opcodes
/// to encode sequences of literal bytes and match references (length + distance pairs).
/// </para>
/// <para>
/// The compressor emits data using a subset of LZVN opcodes: short literal runs (0xE0..0xFF
/// for 1..32 literals), medium-distance matches (0x60..0x9F, 3-byte opcode with 16-bit LE
/// distance), and an end-of-stream marker (0x06). Match lengths are capped at 18 per opcode.
/// The decompressor handles the full LZVN opcode set.
/// </para>
/// </remarks>
internal static class Lzvn {

  private const int HashBits = 14;
  private const int HashSize = 1 << HashBits;
  private const int MaxDistance = 65535;
  private const int MinMatchLength = 4;
  private const int MaxOpcodeMatchLength = 18; // max M encodable in a single med_d opcode (M-3 fits in 4 bits: 0..15 => M 3..18)
  private const int MaxChainSteps = 64;

  #region Compression

  /// <summary>
  /// Compresses <paramref name="src"/> using the LZVN algorithm, returning the compressed bytes.
  /// </summary>
  /// <param name="src">The uncompressed data to compress.</param>
  /// <returns>A byte array containing the LZVN-compressed data terminated with an EOS marker.</returns>
  internal static byte[] Compress(ReadOnlySpan<byte> src) {
    if (src.Length == 0)
      return [0x06];

    using var output = new MemoryStream();
    var hashTable = new int[HashSize];
    var chain = new int[src.Length];
    Array.Fill(hashTable, -1);
    Array.Fill(chain, -1);

    var pos = 0;
    var literalStart = 0;

    while (pos < src.Length) {
      var bestLen = 0;
      var bestDist = 0;

      if (pos + MinMatchLength <= src.Length)
        FindMatch(src, pos, hashTable, chain, out bestLen, out bestDist);

      if (bestLen >= MinMatchLength) {
        // Flush all pending literals before the match.
        if (pos > literalStart)
          EmitLiterals(output, src.Slice(literalStart, pos - literalStart));

        // Emit the match, possibly as multiple opcodes for long matches.
        EmitMatch(output, bestLen, bestDist);

        // Insert all match positions into hash table.
        for (var i = 0; i < bestLen && pos + i + 3 < src.Length; i++)
          InsertHash(src, pos + i, hashTable, chain);

        pos += bestLen;
        literalStart = pos;
      } else {
        if (pos + 3 < src.Length)
          InsertHash(src, pos, hashTable, chain);
        pos++;
      }
    }

    // Flush remaining literals.
    if (literalStart < pos)
      EmitLiterals(output, src[literalStart..pos]);

    // End-of-stream marker.
    output.WriteByte(0x06);

    return output.ToArray();
  }

  /// <summary>
  /// Emits literal-only opcodes (0xE0..0xFF) for runs of 1..32 literals at a time.
  /// </summary>
  private static void EmitLiterals(MemoryStream output, ReadOnlySpan<byte> literals) {
    var offset = 0;
    while (offset < literals.Length) {
      var count = Math.Min(literals.Length - offset, 32);
      output.WriteByte((byte)(0xE0 + count - 1));
      output.Write(literals.Slice(offset, count));
      offset += count;
    }
  }

  /// <summary>
  /// Emits one or more med_d match opcodes (0x60..0x6F with L=0) to encode the full match.
  /// </summary>
  /// <remarks>
  /// Uses the med_d opcode format: <c>byte0 = 0x60 + (M-3)</c>, <c>byte1 = D_low</c>,
  /// <c>byte2 = D_high</c>. L is always 0 (literals are flushed separately). M ranges 3..18
  /// per opcode. Long matches are split into multiple opcodes, ensuring each chunk is at least 3.
  /// </remarks>
  private static void EmitMatch(MemoryStream output, int totalMatchLen, int dist) {
    var remaining = totalMatchLen;
    while (remaining > 0) {
      int m;
      if (remaining <= MaxOpcodeMatchLength) {
        m = remaining;
      } else if (remaining - MaxOpcodeMatchLength >= 3) {
        // Take the max chunk; what's left is still >= 3, so it can be a valid match.
        m = MaxOpcodeMatchLength;
      } else {
        // remaining is 19..20: split evenly so both halves >= 3.
        // E.g. 19 => 10+9, 20 => 10+10.
        m = remaining / 2;
        if (m < 3)
          m = 3;
      }

      // med_d opcode: byte0 = 0x60 | ((M-3) & 0x0F), L=0 encoded in bits [5:4] as 0.
      var opcData = (m - 3) & 0x0F; // M-3 in bits [3:0]
      output.WriteByte((byte)(0x60 + opcData));
      output.WriteByte((byte)(dist & 0xFF));
      output.WriteByte((byte)((dist >> 8) & 0xFF));
      remaining -= m;
    }
  }

  #endregion

  #region Decompression

  /// <summary>
  /// Decompresses LZVN-compressed data from <paramref name="src"/> into <paramref name="dst"/>.
  /// </summary>
  /// <param name="src">The LZVN-compressed data.</param>
  /// <param name="dst">The buffer to receive decompressed data. Must be sized to the expected uncompressed length.</param>
  /// <returns>The number of decompressed bytes written to <paramref name="dst"/>.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when a match distance exceeds the current output position.
  /// </exception>
  internal static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst) {
    var si = 0;
    var di = 0;
    var lastD = 0;

    while (si < src.Length && di < dst.Length) {
      var op = src[si];

      // ---- End of stream (0x06) ----
      if (op == 0x06)
        break;

      // ---- NOP (0x07) ----
      if (op == 0x07) {
        si++;
        continue;
      }

      int L, M, D;

      if (op >= 0xE0) {
        // ---- Short literal run: L = (op & 0x1F) + 1, no match ----
        L = (op & 0x1F) + 1;
        M = 0;
        D = 0;
        si++;
      } else if (op == 0x0E) {
        // ---- Large literal: next 2 bytes = LE (count - 1) ----
        if (si + 2 >= src.Length)
          break;
        L = src[si + 1] + (src[si + 2] << 8) + 1;
        M = 0;
        D = 0;
        si += 3;
      } else if (op >= 0xC0 && op < 0xE0) {
        // ---- pre_d: reuse previous distance, 1 opcode byte ----
        // opc_data = op - 0xC0; L = [5:4], M-3 = [3:0]
        var opcData = op - 0xC0;
        L = (opcData >> 4) & 3;
        M = (opcData & 0x0F) + 3;
        D = lastD;
        si++;
      } else if (op >= 0xA0 && op < 0xC0) {
        // ---- lrg_d: large distance match, 4 opcode bytes ----
        // opc_data = op - 0xA0; L = [5:4], M-3 = [3:0], byte1 = M_ext, D = LE16(byte2,byte3)
        if (si + 3 >= src.Length)
          break;
        var opcData = op - 0xA0;
        L = (opcData >> 4) & 3;
        M = (opcData & 0x0F) + 3 + src[si + 1];
        D = src[si + 2] | (src[si + 3] << 8);
        si += 4;
      } else if (op >= 0x60 && op < 0xA0) {
        // ---- med_d: medium distance match, 3 opcode bytes ----
        // opc_data = op - 0x60; L = [5:4], M-3 = [3:0], D = LE16(byte1,byte2)
        if (si + 2 >= src.Length)
          break;
        var opcData = op - 0x60;
        L = (opcData >> 4) & 3;
        M = (opcData & 0x0F) + 3;
        D = src[si + 1] | (src[si + 2] << 8);
        si += 3;
      } else if (op >= 0x20 && op < 0x60) {
        // ---- sml_d: small distance match, 2 opcode bytes ----
        // opc_data = op - 0x20; L = [5:4], M-3 = [3:1], D_high = [0]
        // D = (D_high << 8) | byte1
        if (si + 1 >= src.Length)
          break;
        var opcData = op - 0x20;
        L = (opcData >> 4) & 3;
        M = ((opcData >> 1) & 7) + 3;
        var dHigh = opcData & 1;
        D = (dHigh << 8) | src[si + 1];
        si += 2;
      } else if (op >= 0x10 && op < 0x20) {
        // ---- pre_d variant: 1 opcode byte ----
        var opcData = op - 0x10;
        L = (opcData >> 2) & 3;
        M = (opcData & 3) + 3;
        D = lastD;
        si++;
      } else {
        // 0x00..0x05, 0x08..0x0D, 0x0F: skip as nop/reserved.
        si++;
        continue;
      }

      // Copy L literal bytes from compressed source.
      if (L > 0) {
        if (si + L > src.Length || di + L > dst.Length)
          break;
        src.Slice(si, L).CopyTo(dst.Slice(di, L));
        si += L;
        di += L;
      }

      // Copy M match bytes from output history at distance D.
      if (M > 0 && D > 0) {
        lastD = D;
        if (D > di)
          throw new InvalidDataException($"LZVN match distance {D} exceeds output position {di}.");
        for (var i = 0; i < M && di < dst.Length; i++) {
          dst[di] = dst[di - D];
          di++;
        }
      }
    }

    return di;
  }

  #endregion

  #region Hash chain match finder

  private static void FindMatch(ReadOnlySpan<byte> src, int pos, int[] hashTable, int[] chain, out int bestLen, out int bestDist) {
    bestLen = 0;
    bestDist = 0;

    if (pos + 3 >= src.Length)
      return;

    var h = Hash4(src, pos);
    var candidate = hashTable[h];
    // Allow finding long matches - they'll be split into multiple opcodes.
    var maxLen = Math.Min(src.Length - pos, 256);
    var minPos = Math.Max(0, pos - MaxDistance);

    var attempts = MaxChainSteps;
    while (candidate >= minPos && attempts-- > 0) {
      if (src[candidate] == src[pos] && src[candidate + 1] == src[pos + 1]) {
        var len = 0;
        while (len < maxLen && src[candidate + len] == src[pos + len])
          len++;

        if (len >= MinMatchLength && len > bestLen) {
          bestLen = len;
          bestDist = pos - candidate;
          if (len == maxLen)
            break;
        }
      }

      var prev = chain[candidate];
      if (prev >= candidate)
        break;
      candidate = prev;
    }
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    ((data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24)) * unchecked((int)0x9E3779B1)) >>> (32 - HashBits);

  private static void InsertHash(ReadOnlySpan<byte> src, int pos, int[] hashTable, int[] chain) {
    var h = Hash4(src, pos);
    chain[pos] = hashTable[h];
    hashTable[h] = pos;
  }

  #endregion
}
