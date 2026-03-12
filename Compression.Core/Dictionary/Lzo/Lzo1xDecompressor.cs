namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// LZO1X-1 style decompressor. Mirrors the token format produced by <see cref="Lzo1xCompressor"/>:
/// <c>[token] [lit_len_ext...] [literal_bytes] [offset_lo] [offset_hi] [match_len_ext...]</c>
/// The final sequence uses a token with low nibble = 0 and has no offset or match — it ends the stream.
/// </summary>
public static class Lzo1xDecompressor {
  /// <summary>
  /// Decompresses data that was compressed with <see cref="Lzo1xCompressor.Compress"/>.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  /// <param name="uncompressedSize">The expected size of the decompressed output.</param>
  /// <returns>A byte array containing the decompressed data.</returns>
  /// <exception cref="InvalidDataException">Thrown if the compressed data is malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int uncompressedSize) {
    if (data.IsEmpty)
      return [];

    var output = new byte[uncompressedSize];
    var outPos = 0;
    var inPos = 0;

    while (inPos < data.Length) {
      // ── Read token byte ──────────────────────────────────────────────────
      var token = data[inPos++];

      // ── Decode literal length (high nibble) ─────────────────────────────
      var litLen = token >> 4;
      if (litLen == 15) {
        // Read extension bytes: accumulate until a byte < 255
        int ext;
        do {
          if (inPos >= data.Length)
            ThrowTruncated();
          ext = data[inPos++];
          litLen += ext;
        } while (ext == 255);
      }

      // ── Decode match extra (low nibble) — will be used after the offset ──
      var matchExtra = token & 0x0F;

      // ── Copy literal bytes ───────────────────────────────────────────────
      if (litLen > 0) {
        if (inPos + litLen > data.Length)
          ThrowTruncated();

        data.Slice(inPos, litLen).CopyTo(output.AsSpan(outPos));
        inPos += litLen;
        outPos += litLen;
      }

      // ── End-of-stream check ──────────────────────────────────────────────
      // A token with low nibble 0 that has no offset following it marks the
      // end of the stream.  The compressor always places this as the very last
      // token, so if we have consumed all input we are done.
      if (matchExtra == 0)
        if (inPos >= data.Length)
          break;

      // If there is still data it must be a real 4-byte match (matchLen = 4).
      // Fall through to read the offset.
      // ── Read 2-byte LE offset ────────────────────────────────────────────
      if (inPos + 2 > data.Length)
        ThrowTruncated();

      var distance = data[inPos] | (data[inPos + 1] << 8);
      inPos += 2;

      if (distance == 0)
        ThrowInvalidDistance();

      // ── Read match-length extension (if low nibble == 15) ────────────────
      var matchLen = Lzo1xDecompressor.MinMatch + matchExtra;
      if (matchExtra == 15) {
        int ext;
        do {
          if (inPos >= data.Length)
            ThrowTruncated();
          ext = data[inPos++];
          matchLen += ext;
        } while (ext == 255);
      }

      // ── Copy match from already-decompressed output ──────────────────────
      var matchStart = outPos - distance;
      if (matchStart < 0)
        ThrowInvalidDistance();

      if (outPos + matchLen > output.Length)
        ThrowOutputOverflow();

      // Byte-by-byte copy handles overlapping (run-length expansion) correctly
      for (var i = 0; i < matchLen; ++i)
        output[outPos + i] = output[matchStart + i];

      outPos += matchLen;
    }

    if (outPos != uncompressedSize)
      ThrowSizeMismatch(outPos, uncompressedSize);

    return output;
  }

  private const int MinMatch = 4;

  private static void ThrowTruncated() =>
    throw new InvalidDataException("LZO1X compressed data is truncated.");

  private static void ThrowInvalidDistance() =>
    throw new InvalidDataException("LZO1X compressed data contains an invalid back-reference distance.");

  private static void ThrowOutputOverflow() =>
    throw new InvalidDataException("LZO1X decompressed data exceeds the declared uncompressed size.");

  private static void ThrowSizeMismatch(int actual, int expected) =>
    throw new InvalidDataException($"LZO1X decompressed size mismatch: expected {expected} bytes, got {actual} bytes.");
}
