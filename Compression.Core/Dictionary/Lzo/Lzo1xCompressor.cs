namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// LZO1X-1 style compressor using a hash table for fast match finding.
/// Uses an LZ4-style token format: token byte (high nibble = literal length 0–15,
/// low nibble = match extra length 0–15), optional literal-length extension bytes,
/// literal bytes, 2-byte LE offset, optional match-length extension bytes.
/// Minimum match length is 4; maximum distance is 65535 (fits in u16 LE field).
/// </summary>
public static class Lzo1xCompressor {
  private const int HashBits = 14;
  private const int HashSize = 1 << Lzo1xCompressor.HashBits; // 16384
  private const int MinMatch = 4;
  private const int MaxDistance = 65535; // fits in u16 LE offset field

  /// <summary>
  /// Compresses the given data using the LZO1X-1 algorithm.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>A byte array containing the compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    // Worst case: every byte is a literal. Allocate generously to avoid mid-stream resizing.
    var output = new byte[data.Length + data.Length / 255 + 32];
    var outPos = 0;

    var hashTable = new int[Lzo1xCompressor.HashSize];
    hashTable.AsSpan().Fill(-1); // -1 = empty

    var anchor = 0; // start of pending literal run
    var pos = 0;

    // We need MinMatch bytes ahead to form a hash key.
    var limit = data.Length - Lzo1xCompressor.MinMatch;

    while (pos <= limit) {
      var hash = Hash4(data, pos);
      var matchPos = hashTable[hash];
      hashTable[hash] = pos;

      // Check match validity: correct bytes, non-negative offset, within max distance
      // TODO: maybe we can compare 4 items at once
      if (matchPos >= 0
          && (pos - matchPos) <= Lzo1xCompressor.MaxDistance
          && data[pos]     == data[matchPos]
          && data[pos + 1] == data[matchPos + 1]
          && data[pos + 2] == data[matchPos + 2]
          && data[pos + 3] == data[matchPos + 3]) {

        // Extend match as far as possible
        var matchLen = Lzo1xCompressor.MinMatch;
        var maxMatchLen = data.Length - pos;
        while (matchLen < maxMatchLen && data[pos + matchLen] == data[matchPos + matchLen])
          ++matchLen;

        var literalLen = pos - anchor;
        var matchExtra = matchLen - Lzo1xCompressor.MinMatch;
        var distance = pos - matchPos;

        // Encode: [token] [lit_len_ext...] [literals] [dist_lo] [dist_hi] [match_len_ext...]
        // Token byte: high nibble = clipped literal length, low nibble = clipped match extra
        var litNibble = Math.Min(literalLen, 15);
        var matchNibble = Math.Min(matchExtra, 15);
        output[outPos++] = (byte)((litNibble << 4) | matchNibble);

        // Literal-length extension bytes (if litLen >= 15)
        if (litNibble == 15) {
          var remaining = literalLen - 15;
          while (remaining >= 255) {
            output[outPos++] = 255;
            remaining -= 255;
          }
          output[outPos++] = (byte)remaining;
        }

        // Literal bytes
        data.Slice(anchor, literalLen).CopyTo(output.AsSpan(outPos));
        outPos += literalLen;

        // 2-byte LE offset
        output[outPos++] = (byte)(distance & 0xFF);
        output[outPos++] = (byte)(distance >> 8);

        // Match-length extension bytes (if matchExtra >= 15)
        if (matchNibble == 15) {
          var remaining = matchExtra - 15;
          while (remaining >= 255) {
            output[outPos++] = 255;
            remaining -= 255;
          }
          output[outPos++] = (byte)remaining;
        }

        // Update hash for positions skipped inside the match
        for (var i = 1; i < matchLen; ++i) {
          var skipped = pos + i;
          if (skipped > limit)
            break;
          var h = Hash4(data, skipped);
          hashTable[h] = skipped;
        }

        pos += matchLen;
        anchor = pos;
      } else {
        ++pos;
      }
    }

    // Emit the final literal run (from anchor to end of input).
    // The final token has low nibble = 0 and is NOT followed by an offset.
    var finalLiteralLen = data.Length - anchor;
    var finalLitNibble = Math.Min(finalLiteralLen, 15);
    output[outPos++] = (byte)(finalLitNibble << 4); // low nibble = 0 → end marker

    // Final literal-length extension if needed
    if (finalLitNibble == 15) {
      var remaining = finalLiteralLen - 15;
      while (remaining >= 255) {
        output[outPos++] = 255;
        remaining -= 255;
      }
      output[outPos++] = (byte)remaining;
    }

    // Final literal bytes
    data.Slice(anchor, finalLiteralLen).CopyTo(output.AsSpan(outPos));
    outPos += finalLiteralLen;

    return output[..outPos];
  }

  // 4-byte hash using a multiply-shift scheme; returns index in [0, HashSize).
  private static int Hash4(ReadOnlySpan<byte> data, int pos) {
    var v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
    return (int)((v * 2654435761u) >> (32 - Lzo1xCompressor.HashBits));
  }
}
