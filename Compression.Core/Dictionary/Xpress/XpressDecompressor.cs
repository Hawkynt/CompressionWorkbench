using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Decompresses the plain LZ77 variant of XPRESS ([MS-XCA] section 2.4), as RtlCompressBuffer
/// with COMPRESSION_FORMAT_XPRESS writes it.
/// </summary>
/// <remarks>
/// <para>
/// A 32-bit little-endian flag word governs the next 32 items, highest bit first: 0 is a literal
/// byte, 1 a 16-bit match word holding (distance - 1) in its upper 13 bits and (length - 3) in its
/// lower 3. A length field of 7 continues in a half byte; two consecutive long matches share one
/// byte, the first taking its low nibble and the second its high nibble. A half byte of 15 continues
/// in a full byte, and a byte of 255 in a 16-bit (then, if that is 0, a 32-bit) value that holds
/// length - 3 outright.
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
    var flags = 0u;
    var flagCount = 0;
    var halfByte = -1; // input position of a length byte whose high nibble is still unused

    while (outputPos < uncompressedSize) {
      if (flagCount == 0) {
        if (inputPos + 4 > input.Length)
          ThrowTruncated();

        flags = BinaryPrimitives.ReadUInt32LittleEndian(input[inputPos..]);
        inputPos += 4;
        flagCount = 32;
      }

      --flagCount;
      if ((flags & (1u << flagCount)) == 0) {
        if (inputPos >= input.Length)
          ThrowTruncated();

        output[outputPos++] = input[inputPos++];
        continue;
      }

      if (inputPos + 2 > input.Length)
        ThrowTruncated();

      var matchWord = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPos..]);
      inputPos += 2;
      var length = matchWord & 7;
      var distance = (matchWord >> 3) + 1;
      if (length == 7) {
        if (halfByte < 0) {
          if (inputPos >= input.Length)
            ThrowTruncated();

          halfByte = inputPos++;
          length = input[halfByte] & 0x0F;
        } else {
          length = input[halfByte] >> 4;
          halfByte = -1;
        }

        if (length == 15) {
          if (inputPos >= input.Length)
            ThrowTruncated();

          length = input[inputPos++];
          if (length == 255) {
            if (inputPos + 2 > input.Length)
              ThrowTruncated();

            length = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPos..]);
            inputPos += 2;
            if (length == 0) {
              if (inputPos + 4 > input.Length)
                ThrowTruncated();

              var length32 = BinaryPrimitives.ReadUInt32LittleEndian(input[inputPos..]);
              inputPos += 4;
              length = length32 > int.MaxValue ? int.MaxValue : (int)length32;
            }

            if (length < 15 + 7)
              ThrowInvalidMatch();

            length -= 15 + 7;
          }

          length += 15;
        }

        length += 7;
      }

      length += XpressConstants.MinMatch;
      var copyFrom = outputPos - distance;
      if (copyFrom < 0)
        ThrowInvalidMatch();

      var copyEnd = (int)Math.Min((long)outputPos + length, uncompressedSize);
      while (outputPos < copyEnd)
        output[outputPos++] = output[copyFrom++];
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
