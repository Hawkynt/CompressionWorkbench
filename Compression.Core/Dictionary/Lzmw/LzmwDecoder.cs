using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzmw;

/// <summary>
/// Decodes LZMW-compressed data from a stream using variable-width codes.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LzmwEncoder"/>'s dictionary-update rule: after emitting the word for a
/// code, the decoder adds the concatenation of the previously emitted word and the word just
/// emitted as one new dictionary entry — the decoder-side counterpart of the encoder adding
/// "previous match + entire next match". Unlike LZW, this never asks the decoder to resolve a
/// code that has not been assigned yet: the code for the current word was necessarily assigned
/// during an earlier step (it had to already exist in the dictionary for the encoder to have
/// found and emitted it), so every code arrives strictly after the entry it names has been
/// added. A defensive check against an unresolvable code is still included as a guard against
/// corrupt/truncated input, matching the sibling LZW decoder's behavior.
/// </remarks>
public sealed class LzmwDecoder {
  private readonly Stream _input;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="LzmwDecoder"/>.
  /// </summary>
  /// <param name="input">The stream to read compressed data from.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">Maximum code width in bits. Defaults to 16.</param>
  /// <param name="bitOrder">The bit ordering used in the input.</param>
  public LzmwDecoder(Stream input, int minBits = 9, int maxBits = 16, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._minBits = minBits;
    this._maxBits = maxBits;
    this._bitOrder = bitOrder;
  }

  /// <summary>
  /// Decodes LZMW-compressed data from the input stream.
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

      if (previousEntry != null && nextCode < maxCode) {
        var newEntry = new byte[previousEntry.Length + entry.Length];
        previousEntry.CopyTo(newEntry, 0);
        entry.CopyTo(newEntry, previousEntry.Length);
        dictionary.Add(newEntry);
        ++nextCode;

        // This naturally lands one insertion behind the encoder's own view (the
        // decoder cannot perform "this" insertion until it has decoded the code
        // that supplies its second half) — which is exactly the width the
        // encoder's two-write-delayed pipeline is designed to hand back. See
        // the remarks on LzmwEncoder.Encode.
        currentBits = ComputeWidth(nextCode, this._minBits, this._maxBits);
      }

      previousEntry = entry;
    }

    return output.ToArray();
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidCode(int code, int dictSize) =>
    throw new InvalidDataException($"Invalid LZMW code {code} encountered (dictSize={dictSize}).");

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
