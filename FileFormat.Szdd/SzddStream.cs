using System.Buffers.Binary;

namespace FileFormat.Szdd;

/// <summary>
/// Reader and writer for the Microsoft SZDD / COMPRESS.EXE file format.
/// SZDD uses a custom LZSS variant with a 4096-byte ring buffer, 8-item flag bytes,
/// and packed offset/length pairs encoded LSB-first.
/// </summary>
public static class SzddStream {
  // ── Public API — Stream overloads ───────────────────────────────────────────

  /// <summary>
  /// Compresses <paramref name="input"/> in SZDD format and writes the result to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream containing the raw data to compress.</param>
  /// <param name="output">Stream that receives the SZDD-encoded output.</param>
  /// <param name="missingChar">
  /// The last character of the original filename extension that was replaced with
  /// an underscore (e.g. <c>'e'</c> for <c>SETUP.EX_</c>). Stored in the header
  /// at offset 9. Defaults to <c>'_'</c>.
  /// </param>
  public static void Compress(Stream input, Stream output, char missingChar = '_') {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var compressed = CompressCore(data, missingChar);
    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses an SZDD-encoded stream and writes the raw data to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Stream positioned at the start of the SZDD file.</param>
  /// <param name="output">Stream that receives the decompressed data.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the header is truncated.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAllBytes(input);
    var decompressed = DecompressCore(data);
    output.Write(decompressed);
  }

  /// <summary>
  /// Returns the "missing character" stored in the SZDD header — the last character
  /// of the original filename extension before it was replaced with <c>'_'</c>.
  /// </summary>
  /// <param name="input">Stream positioned at the start of the SZDD file.</param>
  /// <returns>The character at header offset 9, or <c>'\0'</c> if not set.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the header is truncated.
  /// </exception>
  public static char GetMissingChar(Stream input) {
    ArgumentNullException.ThrowIfNull(input);

    Span<byte> header = stackalloc byte[SzddConstants.HeaderSize];
    input.ReadExactly(header);
    ValidateHeader(header);
    return (char)header[9];
  }

  // ── Public API — Span overloads ─────────────────────────────────────────────

  /// <summary>
  /// Compresses <paramref name="data"/> in SZDD format and returns the result as
  /// a new byte array.
  /// </summary>
  /// <param name="data">The raw bytes to compress.</param>
  /// <param name="missingChar">
  /// The last character of the original filename extension replaced by <c>'_'</c>.
  /// </param>
  public static byte[] Compress(ReadOnlySpan<byte> data, char missingChar = '_') =>
    CompressCore(data, missingChar);

  /// <summary>
  /// Decompresses an SZDD-encoded byte array and returns the raw data.
  /// </summary>
  /// <param name="data">The complete SZDD file contents.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid or the header is truncated.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) =>
    DecompressCore(data);

  // ── Core compression ────────────────────────────────────────────────────────

  private static byte[] CompressCore(ReadOnlySpan<byte> input, char missingChar) {
    // Initialise ring buffer
    var window = new byte[SzddConstants.WindowSize];
    window.AsSpan().Fill(SzddConstants.WindowFill);
    var wpos = SzddConstants.WindowInitPos;

    // Hash chain for fast match finding.
    // head[hash] = most recent window position with that hash, or -1.
    // prev[wpos] = previous window position in the same hash chain, or -1.
    var head = new int[SzddConstants.WindowSize];
    var prev = new int[SzddConstants.WindowSize];
    Array.Fill(head, -1);
    Array.Fill(prev, -1);

    // Seed the hash chains for the initial window content (all spaces).
    // The initial window is filled with 0x20 so every 3-byte hash is the same.
    var initHash = ((SzddConstants.WindowFill << 4) ^ (SzddConstants.WindowFill << 2) ^ SzddConstants.WindowFill) & 0xFFF;
    for (var i = 0; i < SzddConstants.WindowSize; ++i) {
      prev[i] = head[initHash];
      head[initHash] = i;
    }

    using var body = new MemoryStream();

    // Process input in groups of up to 8 items per flag byte.
    var srcPos = 0;
    var srcLen = input.Length;

    while (srcPos < srcLen) {
      // We will encode up to 8 items; determine the flag bits afterwards.
      // Reserve space for the flag byte.
      var flagByteOffset = (int)body.Length;
      body.WriteByte(0); // placeholder

      byte flagByte = 0;
      var itemCount = 0;

      while (itemCount < 8 && srcPos < srcLen) {
        // Search for the longest match in the ring buffer starting from wpos
        // going backwards through the window.
        var bestLen = 0;
        var bestOff = 0;
        FindLongestMatch(input, srcPos, srcLen, window, wpos, head, prev, ref bestLen, ref bestOff);

        if (bestLen >= SzddConstants.MinMatchLength) {
          // Back-reference: encode as 2 bytes.
          // Byte 0: low 8 bits of offset.
          // Byte 1: high 4 bits of offset (shifted 4) | (length - MinMatchLength) in low 4 bits.
          var encLen = bestLen - SzddConstants.MinMatchLength; // 0..15
          body.WriteByte((byte)(bestOff & 0xFF));
          body.WriteByte((byte)(((bestOff >> 4) & 0xF0) | (encLen & 0x0F)));

          // Copy matched bytes into ring buffer and advance, updating hash chains.
          for (var i = 0; i < bestLen; ++i) {
            window[wpos] = input[srcPos];
            UpdateHashChain(input, srcPos, srcLen, wpos, head, prev);
            wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
            ++srcPos;
          }
          // flag bit stays 0 — already the default
        } else {
          // Literal byte.
          var b = input[srcPos];
          body.WriteByte(b);
          window[wpos] = b;
          UpdateHashChain(input, srcPos, srcLen, wpos, head, prev);
          wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
          ++srcPos;
          flagByte |= (byte)(1 << itemCount); // bit = 1 for literal
        }

        ++itemCount;
      }

      // Patch the flag byte.
      var savedPos = body.Position;
      body.Position = flagByteOffset;
      body.WriteByte(flagByte);
      body.Position = savedPos;
    }

    var bodyBytes = body.ToArray();

    // Build header + body.
    var result = new byte[SzddConstants.HeaderSize + bodyBytes.Length];
    var hdr = result.AsSpan(0, SzddConstants.HeaderSize);

    SzddConstants.Magic.CopyTo(hdr);                                // 0-3
    SzddConstants.MagicSuffix.CopyTo(hdr[4..]);                     // 4-7
    hdr[8] = SzddConstants.CompressionModeA;                        // 8
    hdr[9] = (byte)missingChar;                                      // 9
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[10..], (uint)input.Length); // 10-13

    bodyBytes.AsSpan().CopyTo(result.AsSpan(SzddConstants.HeaderSize));
    return result;
  }

  /// <summary>
  /// Hash-chain accelerated longest-match search in the ring buffer.
  /// Hashes the first 3 bytes at the current source position and walks the
  /// chain (up to 32 steps) to find the longest match of at least
  /// <see cref="SzddConstants.MinMatchLength"/> bytes, up to
  /// <see cref="SzddConstants.MaxMatchLength"/> bytes.
  /// </summary>
  private static void FindLongestMatch(
    ReadOnlySpan<byte> input, int srcPos, int srcLen,
    byte[] window, int wpos,
    int[] head, int[] prev,
    ref int bestLen, ref int bestOff) {

    var maxMatch = Math.Min(SzddConstants.MaxMatchLength, srcLen - srcPos);
    if (maxMatch < SzddConstants.MinMatchLength)
      return;

    var hash = ((input[srcPos] << 4) ^ (input[srcPos + 1] << 2) ^ input[srcPos + 2]) & 0xFFF;
    var candidate = head[hash];
    const int maxChainSteps = 32;

    for (var step = 0; step < maxChainSteps && candidate >= 0; ++step) {
      // Skip the current write position — it has not been filled with new data yet.
      if (candidate == wpos) {
        candidate = prev[candidate];
        continue;
      }

      // How many bytes match starting at window[candidate]?
      var matchLen = 0;
      while (matchLen < maxMatch &&
             window[(candidate + matchLen) & (SzddConstants.WindowSize - 1)] == input[srcPos + matchLen]) {
        ++matchLen;
      }

      if (matchLen > bestLen) {
        bestLen = matchLen;
        bestOff = candidate;
        if (bestLen == maxMatch)
          break; // can't do better
      }

      candidate = prev[candidate];
    }
  }

  /// <summary>
  /// Inserts the current window position into the hash chain. Uses input
  /// lookahead bytes so the hash matches what <see cref="FindLongestMatch"/>
  /// will compute when searching for these bytes later.
  /// </summary>
  private static void UpdateHashChain(
    ReadOnlySpan<byte> input, int srcPos, int srcLen,
    int wpos, int[] head, int[] prev) {
    // Need at least 3 bytes of lookahead (current + 2 more) to form a valid hash.
    if (srcPos + 2 >= srcLen)
      return;

    var b0 = input[srcPos];
    var b1 = input[srcPos + 1];
    var b2 = input[srcPos + 2];
    var hash = ((b0 << 4) ^ (b1 << 2) ^ b2) & 0xFFF;
    prev[wpos] = head[hash];
    head[hash] = wpos;
  }

  // ── Core decompression ──────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < SzddConstants.HeaderSize)
      throw new InvalidDataException("Input is shorter than the SZDD header.");

    ValidateHeader(data);

    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);

    // Initialise ring buffer.
    var window = new byte[SzddConstants.WindowSize];
    window.AsSpan().Fill(SzddConstants.WindowFill);
    var wpos = SzddConstants.WindowInitPos;

    var output = new byte[uncompressedSize];
    var outPos = 0;
    var srcPos = SzddConstants.HeaderSize;
    var srcLen = data.Length;

    while (srcPos < srcLen && outPos < (int)uncompressedSize) {
      if (srcPos >= srcLen)
        break;

      var flagByte = data[srcPos++];

      for (var bit = 0; bit < 8 && srcPos < srcLen && outPos < (int)uncompressedSize; ++bit) {
        if ((flagByte & (1 << bit)) != 0) {
          // Literal byte.
          var b = data[srcPos++];
          if (outPos < output.Length)
            output[outPos++] = b;
          window[wpos] = b;
          wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
        } else {
          // Back-reference: 2 bytes encode offset and length.
          if (srcPos + 1 >= srcLen)
            break;

          var lo = data[srcPos++];
          var hi = data[srcPos++];

          var offset = lo | ((hi & 0xF0) << 4);
          var length = (hi & 0x0F) + SzddConstants.MinMatchLength;

          for (var i = 0; i < length && outPos < (int)uncompressedSize; ++i) {
            var b = window[(offset + i) & (SzddConstants.WindowSize - 1)];
            output[outPos++] = b;
            window[wpos] = b;
            wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
          }
        }
      }
    }

    return output;
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────

  private static void ValidateHeader(ReadOnlySpan<byte> header) {
    if (!header[..4].SequenceEqual(SzddConstants.Magic) ||
        !header[4..8].SequenceEqual(SzddConstants.MagicSuffix)) {
      throw new InvalidDataException("Invalid SZDD magic bytes.");
    }

    if (header[8] != SzddConstants.CompressionModeA)
      throw new InvalidDataException(
        $"Unsupported SZDD compression mode: 0x{header[8]:X2}.");
  }

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
