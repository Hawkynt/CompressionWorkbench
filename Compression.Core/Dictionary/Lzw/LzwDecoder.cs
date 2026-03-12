using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// Decodes LZW-compressed data from a stream using variable-width codes.
/// </summary>
public sealed class LzwDecoder {
  private readonly Stream _input;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly bool _useClearCode;
  private readonly bool _useStopCode;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="LzwDecoder"/>.
  /// </summary>
  /// <param name="input">The stream to read compressed data from.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">Maximum code width in bits. Defaults to 12.</param>
  /// <param name="useClearCode">Whether the stream contains clear codes for dictionary resets.</param>
  /// <param name="useStopCode">Whether the stream contains a stop code at end of data.</param>
  /// <param name="bitOrder">The bit ordering used in the input.</param>
  public LzwDecoder(
    Stream input,
    int minBits = 9,
    int maxBits = 12,
    bool useClearCode = true,
    bool useStopCode = true,
    BitOrder bitOrder = BitOrder.LsbFirst) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._minBits = minBits;
    this._maxBits = maxBits;
    this._useClearCode = useClearCode;
    this._useStopCode = useStopCode;
    this._bitOrder = bitOrder;
  }

  /// <summary>
  /// Decodes LZW-compressed data from the input stream.
  /// </summary>
  /// <param name="expectedLength">
  /// If non-negative, decoding stops after this many bytes have been produced.
  /// If negative, decoding continues until a stop code or end of stream.
  /// </param>
  /// <returns>The decompressed data as a byte array.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when an invalid code is encountered in the stream.
  /// </exception>
  public byte[] Decode(int expectedLength = -1) {
    var reader = new BitReader(this._input, this._bitOrder);
    var output = new MemoryStream();

    var clearCode = 1 << (this._minBits - 1);
    var stopCode = this._useStopCode ? clearCode + (this._useClearCode ? 1 : 0) : -1;
    var firstUsableCode = clearCode + (this._useClearCode ? 1 : 0) + (this._useStopCode ? 1 : 0);

    var currentBits = this._minBits;
    var nextCode = firstUsableCode;
    var maxCode = 1 << this._maxBits;

    // Dictionary: index -> byte sequence.
    var dictionary = new List<byte[]>();
    InitializeDictionary(dictionary, clearCode, this._useClearCode, this._useStopCode);

    byte[]? previousEntry = null;

    while (expectedLength < 0 || output.Length < expectedLength) {
      int code;
      try {
        code = (int)reader.ReadBits(currentBits);
      }
      catch (EndOfStreamException) {
        break;
      }

      // Handle clear code.
      if (this._useClearCode && code == clearCode) {
        dictionary.Clear();
        InitializeDictionary(dictionary, clearCode, this._useClearCode, this._useStopCode);
        currentBits = this._minBits;
        nextCode = firstUsableCode;
        previousEntry = null;
        continue;
      }

      // Handle stop code.
      if (this._useStopCode && code == stopCode)
        break;

      byte[] entry;

      if (code < dictionary.Count) {
        // Code is in the dictionary.
        entry = dictionary[code];
      } else if (code == nextCode && previousEntry != null) {
        // KwKwK case: the new entry is previousEntry + previousEntry[0].
        entry = new byte[previousEntry.Length + 1];
        previousEntry.CopyTo(entry, 0);
        entry[^1] = previousEntry[0];
      } else {
        entry = default!;
        ThrowInvalidCode(code, nextCode, dictionary.Count);
      }

      output.Write(entry, 0, entry.Length);

      // Add new dictionary entry: previousEntry + entry[0].
      if (previousEntry != null && nextCode < maxCode) {
        var newEntry = new byte[previousEntry.Length + 1];
        previousEntry.CopyTo(newEntry, 0);
        newEntry[^1] = entry[0];
        dictionary.Add(newEntry);
        ++nextCode;

        // Check if we need to widen the code width.
        if (nextCode > (1 << currentBits) && currentBits < this._maxBits)
          ++currentBits;
      }

      previousEntry = entry;
    }

    return output.ToArray();
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidCode(int code, int nextCode, int dictSize) =>
    throw new InvalidDataException(
      $"Invalid LZW code {code} encountered (nextCode={nextCode}, dictSize={dictSize}).");

  private static void InitializeDictionary(List<byte[]> dictionary, int clearCode, bool useClearCode, bool useStopCode) {
    for (var i = 0; i < clearCode; ++i)
      dictionary.Add([(byte)i]);

    if (useClearCode)
      dictionary.Add([]); // clear code placeholder

    if (useStopCode)
      dictionary.Add([]); // stop code placeholder
  }
}
