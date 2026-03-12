using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Decompresses data compressed with the XPRESS (LZ Xpress plain) algorithm.
/// </summary>
/// <remarks>
/// <para>
/// Format: repeated flag groups. Each group starts with a 32-bit LE flag word.
/// Bit 0 (LSB) is the flag for the first item; bit 31 for the last.
/// A 0 bit = literal byte; a 1 bit = LZ match descriptor.
/// </para>
/// <para>
/// Match descriptor (16-bit LE):
/// <code>
///   offset = value >> 3   (1-based distance into output history)
///   length_header = value &amp; 7
///   if length_header &lt; 7: length = length_header + 3
///   if length_header == 7:
///     extra = next byte
///     if extra != 255: length = extra + 3
///     else:
///       len16 = next 16-bit LE
///       if len16 != 0: length = len16
///       else: length = next 32-bit LE
/// </code>
/// </para>
/// </remarks>
public static class XpressDecompressor {
  /// <summary>
  /// Decompresses XPRESS-encoded data from a byte span.
  /// </summary>
  /// <param name="input">The compressed input data.</param>
  /// <param name="uncompressedSize">Expected uncompressed output size in bytes.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    if (uncompressedSize == 0)
      return [];

    var output = new byte[uncompressedSize];
    var inputPos = 0;
    var outputPos = 0;

    while (outputPos < uncompressedSize) {
      // Read 32-bit flag word
      if (inputPos + 4 > input.Length)
        ThrowTruncated();

      var flagWord = BinaryPrimitives.ReadUInt32LittleEndian(input[inputPos..]);
      inputPos += 4;

      for (var bit = 0; bit < XpressConstants.FlagGroupSize && outputPos < uncompressedSize; ++bit)
        if ((flagWord & (1u << bit)) == 0) {
          // Literal
          if (inputPos >= input.Length)
            ThrowTruncated();

          output[outputPos++] = input[inputPos++];
        } else {
          // Match
          if (inputPos + 2 > input.Length)
            ThrowTruncated();

          var value = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPos..]);
          inputPos += 2;

          var offset = value >> 3; // 1-based
          var lengthHeader = value & 7;
          int length;

          if (offset == 0)
            ThrowInvalidMatch();

          if (lengthHeader < 7)
            length = lengthHeader + XpressConstants.MinMatch;
          else {
            // Read extra length byte
            if (inputPos >= input.Length)
              ThrowTruncated();

            var extra = input[inputPos++];
            if (extra != XpressConstants.LengthSentinel8)
              length = extra + XpressConstants.MinMatch;
            else {
              // Read 16-bit length
              if (inputPos + 2 > input.Length)
                ThrowTruncated();

              var len16 = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPos..]);
              inputPos += 2;

              if (len16 != XpressConstants.LengthSentinel16)
                length = len16;
              else {
                // Read 32-bit length
                if (inputPos + 4 > input.Length)
                  ThrowTruncated();

                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(input[inputPos..]);
                inputPos += 4;
              }
            }
          }

          // Copy from output history (offset is 1-based)
          var copyFrom = outputPos - offset;
          if (copyFrom < 0)
            ThrowInvalidMatch();

          // Handle overlapping copy byte-by-byte
          var copyEnd = Math.Min(outputPos + length, uncompressedSize);
          while (outputPos < copyEnd)
            output[outputPos++] = output[copyFrom++];
        }
    }

    return output;
  }

  /// <summary>
  /// Decompresses XPRESS-encoded data from a stream.
  /// </summary>
  /// <param name="input">The stream containing compressed data.</param>
  /// <param name="uncompressedSize">Expected uncompressed output size in bytes.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public static byte[] Decompress(Stream input, int uncompressedSize) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    if (uncompressedSize == 0)
      return [];

    var compressed = new byte[input.Length - input.Position];
    input.ReadExactly(compressed);
    return Decompress(compressed.AsSpan(), uncompressedSize);
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowTruncated() =>
    throw new InvalidDataException("XPRESS compressed data is truncated.");

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidMatch() =>
    throw new InvalidDataException("XPRESS compressed data contains an invalid match descriptor.");
}
