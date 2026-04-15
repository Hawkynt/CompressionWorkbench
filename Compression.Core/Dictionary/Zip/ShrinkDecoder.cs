using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Shrink (method 1) compressed data.
/// </summary>
/// <remarks>
/// ZIP Shrink is LZW with 9-13 bit variable-width codes, a partial clear mechanism
/// via control code 256, and code width increase via sub-command 1 after code 256.
/// Max dictionary size is 8192 entries.
/// </remarks>
public static class ShrinkDecoder {
  private const int MinBits = 9;
  private const int MaxBits = 13;
  private const int MaxCode = 1 << MaxBits; // 8192
  private const int ControlCode = 256;
  private const byte SubCmdIncrease = 1;
  private const byte SubCmdPartialClear = 2;

  /// <summary>
  /// Decompresses ZIP Shrink data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize)
    => Decode(compressed.ToArray(), originalSize);

  /// <summary>
  /// Decompresses ZIP Shrink data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    using var ms = new MemoryStream(compressed);
    var reader = new BitReader(ms, BitOrder.LsbFirst);
    var output = new byte[originalSize];
    var outputPos = 0;

    // Dictionary entries: prefix code + suffix byte
    var prefix = new int[MaxCode];
    var suffix = new byte[MaxCode];
    var isUsed = new bool[MaxCode];

    // Initialize single-byte entries
    for (var i = 0; i < 256; ++i) {
      prefix[i] = -1;
      suffix[i] = (byte)i;
      isUsed[i] = true;
    }

    // Code 256 is the control code
    isUsed[ControlCode] = true;

    var currentBits = MinBits;
    var nextCode = 257; // First usable code after 256 (control)
    var prevCode = -1;
    var decodeStack = new byte[MaxCode];

    while (outputPos < originalSize) {
      int code;
      try {
        code = (int)reader.ReadBits(currentBits);
      } catch (EndOfStreamException) {
        break;
      }

      if (code == ControlCode) {
        // Read sub-command
        int subCmd;
        try {
          subCmd = (int)reader.ReadBits(currentBits);
        } catch (EndOfStreamException) {
          break;
        }

        if (subCmd == SubCmdIncrease) {
          if (currentBits < MaxBits)
            ++currentBits;
        }
        else if (subCmd == SubCmdPartialClear) {
          PartialClear(prefix, isUsed, nextCode);
        }
        continue;
      }

      // Decode the code to a string
      var stackPos = 0;
      var c = code;

      if (c >= nextCode || (c >= 257 && !isUsed[c])) {
        // KwKwK case: code not yet in dictionary
        if (prevCode < 0)
          throw new InvalidDataException("Invalid Shrink stream: KwKwK with no previous code.");
        decodeStack[stackPos++] = GetFirstByte(prevCode, prefix, suffix);
        c = prevCode;
      }

      // Walk the chain
      while (c >= 257) {
        decodeStack[stackPos++] = suffix[c];
        c = prefix[c];
      }
      decodeStack[stackPos++] = suffix[c]; // single-byte code

      // Write in reverse order
      for (var i = stackPos - 1; i >= 0 && outputPos < originalSize; --i)
        output[outputPos++] = decodeStack[i];

      // Add new dictionary entry
      if (prevCode >= 0 && nextCode < MaxCode) {
        // Find first unused code slot from nextCode
        while (nextCode < MaxCode && isUsed[nextCode])
          ++nextCode;

        if (nextCode < MaxCode) {
          prefix[nextCode] = prevCode;
          suffix[nextCode] = GetFirstByte(code, prefix, suffix);
          isUsed[nextCode] = true;
          ++nextCode;
        }
      }

      prevCode = code;
    }

    return output;
  }

  private static byte GetFirstByte(int code, int[] prefix, byte[] suffix) {
    while (code >= 257)
      code = prefix[code];
    return suffix[code];
  }

  private static void PartialClear(int[] prefix, bool[] isUsed, int nextCode) {
    // Mark codes that are referenced as prefixes
    var isReferenced = new bool[MaxCode];
    for (var i = 257; i < MaxCode; ++i) {
      if (isUsed[i] && prefix[i] >= 257)
        isReferenced[prefix[i]] = true;
    }

    // Clear codes that are not referenced by any other code
    for (var i = 257; i < MaxCode; ++i) {
      if (isUsed[i] && !isReferenced[i])
        isUsed[i] = false;
    }
  }
}
