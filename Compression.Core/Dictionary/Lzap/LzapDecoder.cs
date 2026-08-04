using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzap;

/// <summary>
/// Decodes LZAP-compressed data from a stream using variable-width codes.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LzapEncoder"/>'s dictionary-update rule: after emitting the word for a
/// code, the decoder adds the previously emitted word concatenated with EVERY prefix of the word
/// just emitted — one new dictionary entry per prefix length, in increasing length order,
/// matching the encoder's insertion order exactly so both sides assign the same codes to the
/// same strings. As with LZMW, the word a code names was always assigned during an earlier step
/// (it had to already exist in the dictionary for the encoder to have matched and emitted it),
/// so no LZW-style "code not yet in the dictionary" resolution is ever required for correctly
/// encoded input; a defensive check remains for corrupt/truncated streams, matching the sibling
/// LZW/LZMW decoders.
/// </remarks>
public sealed class LzapDecoder {
  private readonly Stream _input;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="LzapDecoder"/>.
  /// </summary>
  /// <param name="input">The stream to read compressed data from.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">Maximum code width in bits. Defaults to 12 (see <see cref="LzapEncoder"/>).</param>
  /// <param name="bitOrder">The bit ordering used in the input.</param>
  public LzapDecoder(Stream input, int minBits = 9, int maxBits = 12, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._minBits = minBits;
    this._maxBits = maxBits;
    this._bitOrder = bitOrder;
  }

  /// <summary>
  /// Decodes LZAP-compressed data from the input stream.
  /// </summary>
  /// <param name="expectedLength">
  /// If non-negative, decoding stops after this many bytes have been produced.
  /// If negative, decoding continues until a stop code or end of stream.
  /// </param>
  /// <returns>The decompressed data as a byte array.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when a code beyond the current dictionary size is encountered in the stream.
  /// </exception>
  public byte[] Decode(int expectedLength = -1) {
    var reader = new BitReader(this._input, this._bitOrder);
    var output = new MemoryStream();

    var clearCode = 1 << (this._minBits - 1);
    var stopCode = clearCode + 1;
    var firstUsableCode = clearCode + 2;
    var maxCode = 1 << this._maxBits;
    var currentBits = this._minBits;
    var nextCode = firstUsableCode;

    var dictionary = new List<byte[]>(firstUsableCode + 16);
    InitializeDictionary(dictionary, clearCode);

    byte[]? previousEntry = null;

    while (expectedLength < 0 || output.Length < expectedLength) {
      int code;
      try {
        code = (int)reader.ReadBits(currentBits);
      }
      catch (EndOfStreamException) {
        break;
      }

      if (code == clearCode) {
        dictionary.Clear();
        InitializeDictionary(dictionary, clearCode);
        currentBits = this._minBits;
        nextCode = firstUsableCode;
        previousEntry = null;
        continue;
      }

      if (code == stopCode)
        break;

      if (code >= dictionary.Count)
        ThrowInvalidCode(code, dictionary.Count);

      var entry = dictionary[code];
      output.Write(entry, 0, entry.Length);

      if (previousEntry != null) {
        var prevLen = previousEntry.Length;
        var completed = true;
        for (var prefixLen = 1; prefixLen <= entry.Length; ++prefixLen) {
          if (nextCode >= maxCode) {
            completed = false;
            break;
          }

          var newEntry = new byte[prevLen + prefixLen];
          previousEntry.CopyTo(newEntry, 0);
          Array.Copy(entry, 0, newEntry, prevLen, prefixLen);
          dictionary.Add(newEntry);
          ++nextCode;
        }

        // Only grow the width when every prefix was added. When the batch is
        // cut short by a full dictionary, the encoder abandons this insertion
        // outright and writes a clear code at whatever width was ALREADY
        // active — it never lets the width grow toward the now-discarded,
        // overflowed state. Mirror that here: leave currentBits untouched so
        // the upcoming clear code is read at the same width it was written
        // with. See the remarks on LzapEncoder.Encode.
        if (completed)
          currentBits = ComputeWidth(nextCode, this._minBits, this._maxBits);
      }

      previousEntry = entry;
    }

    return output.ToArray();
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidCode(int code, int dictSize) =>
    throw new InvalidDataException($"Invalid LZAP code {code} encountered (dictSize={dictSize}).");

  /// <summary>
  /// Computes the code width needed to represent codes up to (but not including) <paramref name="nextCode"/>,
  /// the same monotonic growth rule LZW/LZMW/LZAP all share.
  /// </summary>
  private static int ComputeWidth(int nextCode, int minBits, int maxBits) {
    var w = minBits;
    while (nextCode >= (1 << w) && w < maxBits)
      ++w;
    return w;
  }

  private static void InitializeDictionary(List<byte[]> dictionary, int clearCode) {
    for (var i = 0; i < clearCode; ++i)
      dictionary.Add([(byte)i]);

    dictionary.Add([]); // clear code placeholder
    dictionary.Add([]); // stop code placeholder
  }
}
