using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Decompresses data encoded with the Quantum algorithm (used in Microsoft CAB files).
/// </summary>
/// <remarks>
/// <para>
/// Quantum is an LZ77-based format that uses a 16-bit byte-aligned range coder
/// with adaptive frequency models. It supports window sizes from 1 KB to 2 MB
/// (levels 1–7).
/// </para>
/// <para>Key structures:</para>
/// <list type="bullet">
///   <item>A <b>selector model</b> (7 symbols) determines the next action:
///     literal (0), match with base length 4–7 (selectors 1–4),
///     match with base length 12 (selector 5), or match with base length 24 (selector 6).</item>
///   <item>A <b>literal model</b> (256 symbols) decodes byte values.</item>
///   <item>Five <b>length models</b> (27 symbols each) provide extra length
///     values added to the base length for each match type.</item>
///   <item>Match offsets are read as raw bits (log2 of window size).</item>
/// </list>
/// </remarks>
public static class QuantumDecompressor {
  /// <summary>
  /// Decompresses a single Quantum-compressed block.
  /// </summary>
  /// <param name="compressed">The compressed input data.</param>
  /// <param name="uncompressedSize">The expected uncompressed output size in bytes.</param>
  /// <param name="windowLevel">
  /// Window level (1–7). The window size is 1024 &lt;&lt; (level - 1).
  /// </param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="windowLevel"/> is outside the valid range [1, 7],
  /// or when <paramref name="uncompressedSize"/> is negative.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the compressed data is malformed.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize, int windowLevel) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);
    ArgumentOutOfRangeException.ThrowIfLessThan(windowLevel, QuantumConstants.MinWindowLevel, nameof(windowLevel));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLevel, QuantumConstants.MaxWindowLevel, nameof(windowLevel));

    if (uncompressedSize == 0)
      return [];

    var windowSize = QuantumConstants.WindowSize(windowLevel);
    var window = new byte[windowSize];
    var windowMask = windowSize - 1;
    var windowPos = 0;

    var output = new byte[uncompressedSize];
    var outputPos = 0;

    // Calculate offset bit count from window size
    var offsetBits = 0;
    for (var ws = windowSize; ws > 1; ws >>= 1)
      ++offsetBits;

    // Initialize range decoder
    var decoder = new QuantumRangeDecoder(compressed.ToArray());

    // Create adaptive models
    var selectorModel = new QuantumModel(QuantumConstants.SelectorSymbols);
    var literalModel = new QuantumModel(QuantumConstants.LiteralSymbols);
    var lenModel4 = new QuantumModel(QuantumConstants.MatchLengthSymbols);
    var lenModel5 = new QuantumModel(QuantumConstants.MatchLengthSymbols);
    var lenModel6 = new QuantumModel(QuantumConstants.MatchLengthSymbols);
    var lenModel7 = new QuantumModel(QuantumConstants.MatchLengthSymbols);
    var lenModelLong = new QuantumModel(QuantumConstants.MatchLengthSymbols);

    while (outputPos < uncompressedSize) {
      var selector = decoder.DecodeSymbol(selectorModel);

      switch (selector) {
        case 0: {
          // Literal byte
          var lit = decoder.DecodeSymbol(literalModel);
          var b = (byte)lit;
          output[outputPos] = b;
          window[windowPos & windowMask] = b;
          ++windowPos;
          ++outputPos;
          break;
        }

        case >= 1 and <= 6: {
          // Match: pick the length model based on selector
          var lenModel = selector switch {
            1 => lenModel4,
            2 => lenModel5,
            3 => lenModel6,
            4 => lenModel7,
            _ => lenModelLong
          };

          var extraLen = decoder.DecodeSymbol(lenModel);
          var baseLen = QuantumConstants.BaseMatchLength(selector);
          var matchLen = baseLen + extraLen;

          // Read offset as raw bits
          var offset = decoder.ReadRawBits(offsetBits);
          if (offset == 0)
            ThrowInvalidOffset();

          // Validate offset does not exceed available history
          if (offset > windowPos)
            ThrowInvalidOffset();

          // Copy from window
          for (var i = 0; i < matchLen; ++i) {
            if (outputPos >= uncompressedSize)
              break;
            var b = window[(windowPos - offset) & windowMask];
            output[outputPos] = b;
            window[windowPos & windowMask] = b;
            ++windowPos;
            ++outputPos;
          }

          break;
        }

        default:
          ThrowInvalidSelector();
          break;
      }
    }

    return output;
  }

  [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidOffset() =>
    throw new InvalidDataException("Quantum: invalid match offset.");

  [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidSelector() =>
    throw new InvalidDataException("Quantum: invalid selector symbol.");
}
