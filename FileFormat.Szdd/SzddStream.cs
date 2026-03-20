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
    int wpos = SzddConstants.WindowInitPos;

    using var body = new MemoryStream();

    // Process input in groups of up to 8 items per flag byte.
    int srcPos = 0;
    int srcLen = input.Length;

    while (srcPos < srcLen) {
      // We will encode up to 8 items; determine the flag bits afterwards.
      // Reserve space for the flag byte.
      int flagByteOffset = (int)body.Length;
      body.WriteByte(0); // placeholder

      byte flagByte = 0;
      int itemCount = 0;

      while (itemCount < 8 && srcPos < srcLen) {
        // Search for the longest match in the ring buffer starting from wpos
        // going backwards through the window.
        int bestLen = 0;
        int bestOff = 0;
        FindLongestMatch(input, srcPos, srcLen, window, wpos, ref bestLen, ref bestOff);

        if (bestLen >= SzddConstants.MinMatchLength) {
          // Back-reference: encode as 2 bytes.
          // Byte 0: low 8 bits of offset.
          // Byte 1: high 4 bits of offset (shifted 4) | (length - MinMatchLength) in low 4 bits.
          int encLen = bestLen - SzddConstants.MinMatchLength; // 0..15
          body.WriteByte((byte)(bestOff & 0xFF));
          body.WriteByte((byte)(((bestOff >> 4) & 0xF0) | (encLen & 0x0F)));

          // Copy matched bytes into ring buffer and advance.
          for (int i = 0; i < bestLen; ++i) {
            window[wpos] = input[srcPos];
            wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
            ++srcPos;
          }
          // flag bit stays 0 — already the default
        } else {
          // Literal byte.
          byte b = input[srcPos++];
          body.WriteByte(b);
          window[wpos] = b;
          wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
          flagByte |= (byte)(1 << itemCount); // bit = 1 for literal
        }

        ++itemCount;
      }

      // Patch the flag byte.
      long savedPos = body.Position;
      body.Position = flagByteOffset;
      body.WriteByte(flagByte);
      body.Position = savedPos;
    }

    byte[] bodyBytes = body.ToArray();

    // Build header + body.
    byte[] result = new byte[SzddConstants.HeaderSize + bodyBytes.Length];
    Span<byte> hdr = result.AsSpan(0, SzddConstants.HeaderSize);

    SzddConstants.Magic.CopyTo(hdr);                                // 0-3
    SzddConstants.MagicSuffix.CopyTo(hdr[4..]);                     // 4-7
    hdr[8] = SzddConstants.CompressionModeA;                        // 8
    hdr[9] = (byte)missingChar;                                      // 9
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[10..], (uint)input.Length); // 10-13

    bodyBytes.AsSpan().CopyTo(result.AsSpan(SzddConstants.HeaderSize));
    return result;
  }

  /// <summary>
  /// Greedy longest-match search in the ring buffer.
  /// Scans all <see cref="SzddConstants.WindowSize"/> positions for the longest
  /// match of at least <see cref="SzddConstants.MinMatchLength"/> bytes,
  /// up to <see cref="SzddConstants.MaxMatchLength"/> bytes.
  /// </summary>
  private static void FindLongestMatch(
    ReadOnlySpan<byte> input, int srcPos, int srcLen,
    byte[] window, int wpos,
    ref int bestLen, ref int bestOff) {

    int maxMatch = Math.Min(SzddConstants.MaxMatchLength, srcLen - srcPos);
    if (maxMatch < SzddConstants.MinMatchLength)
      return;

    for (int candidate = 0; candidate < SzddConstants.WindowSize; ++candidate) {
      // How many bytes match starting at window[candidate]?
      int matchLen = 0;
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
    }
  }

  // ── Core decompression ──────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < SzddConstants.HeaderSize)
      throw new InvalidDataException("Input is shorter than the SZDD header.");

    ValidateHeader(data);

    uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);

    // Initialise ring buffer.
    var window = new byte[SzddConstants.WindowSize];
    window.AsSpan().Fill(SzddConstants.WindowFill);
    int wpos = SzddConstants.WindowInitPos;

    var output = new byte[uncompressedSize];
    int outPos = 0;
    int srcPos = SzddConstants.HeaderSize;
    int srcLen = data.Length;

    while (srcPos < srcLen && outPos < (int)uncompressedSize) {
      if (srcPos >= srcLen)
        break;

      byte flagByte = data[srcPos++];

      for (int bit = 0; bit < 8 && srcPos < srcLen && outPos < (int)uncompressedSize; ++bit) {
        if ((flagByte & (1 << bit)) != 0) {
          // Literal byte.
          byte b = data[srcPos++];
          if (outPos < output.Length)
            output[outPos++] = b;
          window[wpos] = b;
          wpos = (wpos + 1) & (SzddConstants.WindowSize - 1);
        } else {
          // Back-reference: 2 bytes encode offset and length.
          if (srcPos + 1 >= srcLen)
            break;

          byte lo = data[srcPos++];
          byte hi = data[srcPos++];

          int offset = lo | ((hi & 0xF0) << 4);
          int length = (hi & 0x0F) + SzddConstants.MinMatchLength;

          for (int i = 0; i < length && outPos < (int)uncompressedSize; ++i) {
            byte b = window[(offset + i) & (SzddConstants.WindowSize - 1)];
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
