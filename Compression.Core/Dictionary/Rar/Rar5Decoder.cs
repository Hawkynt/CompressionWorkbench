using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.DataStructures;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// RAR5 LZ+Huffman decompression engine.
/// Decodes compressed data using adaptive multi-table Huffman coding
/// with LZ77 match references and optional post-processing filters.
/// </summary>
/// <remarks>
/// RAR5 compression uses:
/// <list type="bullet">
///   <item>Main table (306 symbols): literals (0-255) + match-length/distance coding</item>
///   <item>Offset table (64 symbols): distance slot codes</item>
///   <item>Low-offset table (16 symbols): 4-bit distance refinement</item>
///   <item>Length table (16 symbols): additional match-length bits</item>
///   <item>Built-in filters: delta, E8/E9, ARM</item>
/// </list>
/// </remarks>
public sealed class Rar5Decoder {
  private readonly byte[] _window;
  private readonly int _windowMask;
  private int _windowPos;
  private readonly int[] _repDist;
  private bool _tablesRead;

  private readonly Rar5HuffmanDecoder _mainDecoder = new();
  private readonly Rar5HuffmanDecoder _offsetDecoder = new();
  private readonly Rar5HuffmanDecoder _lowOffsetDecoder = new();
  private readonly Rar5HuffmanDecoder _lengthDecoder = new();

  private int _lastLength;
  private readonly List<Rar5Filters.PendingFilter> _filters = [];

  /// <summary>
  /// Initializes a new <see cref="Rar5Decoder"/> with the specified dictionary size.
  /// </summary>
  /// <param name="dictionarySize">Dictionary (window) size in bytes. Must be a power of two.</param>
  public Rar5Decoder(int dictionarySize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, Rar5Constants.MinDictionarySize);

    // Round up to power of two
    var size = 1;
    while (size < dictionarySize && size > 0)
      size <<= 1;

    this._window = new byte[size];
    this._windowMask = size - 1;
    this._windowPos = 0;
    this._repDist = [0, 0, 0, 0];
    this._lastLength = 0;
  }

  /// <summary>
  /// Decompresses RAR5-compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data block.</param>
  /// <param name="uncompressedSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    if (uncompressedSize == 0)
      return [];

    var reader = new Rar5BitReader(compressed.ToArray());
    var output = new byte[uncompressedSize];
    var outputPos = 0;

    // Each compressed block starts with fresh Huffman tables, even in solid mode
    // (window and repeat offsets persist, but tables are per-block)
    this._tablesRead = false;

    while (outputPos < uncompressedSize && !reader.IsAtEnd) {
      // Read Huffman tables at the start or when flagged
      if (!this._tablesRead) {
        this.ReadTables(reader);
        this._tablesRead = true;
      }

      // Decode symbols
      var sym = this._mainDecoder.DecodeSymbol(reader);

      switch (sym) {
        case < Rar5Constants.LiteralCount:
          // Literal byte
          output[outputPos] = (byte)sym;
          this._window[this._windowPos & this._windowMask] = (byte)sym;
          ++this._windowPos;
          ++outputPos;
          break;

        case >= Rar5Constants.MatchBase and < Rar5Constants.MainTableSize: {
          // Match: decode length and distance
          var lengthSlot = sym - Rar5Constants.MatchBase;
          var matchLength = this.DecodeMatchLength(reader, lengthSlot);
          var distance = this.DecodeDistance(reader);

          if (distance < 0)
            distance = 0;

          // Update repeated offsets
          this.ShiftRepeatOffsets(distance);
          this._lastLength = matchLength;

          // Copy match from window
          outputPos += this.CopyMatch(output, outputPos, uncompressedSize, distance, matchLength);
          break;
        }

        case >= Rar5Constants.RepeatOffset0 and <= Rar5Constants.RepeatOffset3: {
          // Repeated offset
          var repIdx = sym - Rar5Constants.RepeatOffset0;
          var distance = this._repDist[repIdx];

          // Move this offset to position 0
          for (var i = repIdx; i > 0; --i)
            this._repDist[i] = this._repDist[i - 1];
          this._repDist[0] = distance;

          // Decode match length for repeated offset
          int matchLength;
          if (sym == Rar5Constants.RepeatOffset0) {
            // Same offset, same length as last match
            matchLength = this._lastLength;
            if (matchLength == 0)
              matchLength = 2;
          } else {
            // Read length from main code extension
            var lenSym = this._lengthDecoder.DecodeSymbol(reader);
            matchLength = DecodeExtraLength(reader, lenSym);
          }

          this._lastLength = matchLength;
          outputPos += this.CopyMatch(output, outputPos, uncompressedSize, distance, matchLength);
          break;
        }

        default: {
          if (sym == Rar5Constants.EndOfBlock)
            // End of block — tables need re-reading
            this._tablesRead = false;

          break;
        }
      }
    }

    // Apply pending filters
    output = this.ApplyFilters(output);

    return output;
  }

  /// <summary>
  /// Reads the Huffman tables from the bit stream.
  /// </summary>
  private void ReadTables(Rar5BitReader reader) {
    // Read code lengths for all four tables packed sequentially.
    // First read the code-length code lengths (up to 20 symbols)
    var clLengths = new int[Rar5Constants.CodeLengthTableSize];

    // Each code-length code length is 4 bits
    for (var i = 0; i < Rar5Constants.CodeLengthTableSize; ++i)
      clLengths[i] = (int)reader.ReadBits(4);

    var clDecoder = new Rar5HuffmanDecoder();
    clDecoder.Build(clLengths, Rar5Constants.CodeLengthTableSize);

    // Read main table
    var mainLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.MainTableSize);
    this._mainDecoder.Build(mainLengths, Rar5Constants.MainTableSize);

    // Read offset table
    var offsetLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.OffsetTableSize);
    this._offsetDecoder.Build(offsetLengths, Rar5Constants.OffsetTableSize);

    // Read low-offset table
    var lowOffsetLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.LowOffsetTableSize);
    this._lowOffsetDecoder.Build(lowOffsetLengths, Rar5Constants.LowOffsetTableSize);

    // Read length table
    var lengthLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.LengthTableSize);
    this._lengthDecoder.Build(lengthLengths, Rar5Constants.LengthTableSize);
  }

  /// <summary>
  /// Reads Huffman code lengths using the code-length decoder.
  /// Supports repeat codes (16, 17, 18).
  /// </summary>
  private static int[] ReadCodeLengths(Rar5BitReader reader, Rar5HuffmanDecoder clDecoder, int count) {
    var lengths = new int[count];
    var i = 0;

    while (i < count) {
      var sym = clDecoder.DecodeSymbol(reader);
      switch (sym) {
        case < 16:
          // Direct code length
          lengths[i++] = sym;
          break;

        case 16: {
          // Repeat previous length 3-6 times
          if (i == 0) 
            ThrowInvalidData("Code length repeat at start.");

          var repeat = (int)reader.ReadBits(2) + 3;
          var prev = lengths[i - 1];
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = prev;

          break;
        }

        case 17: {
          // Repeat zero 3-10 times
          var repeat = (int)reader.ReadBits(3) + 3;
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = 0;

          break;
        }

        case 18: {
          // Repeat zero 11-138 times
          var repeat = (int)reader.ReadBits(7) + 11;
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = 0;

          break;
        }
      }
    }

    return lengths;
  }

  /// <summary>
  /// Decodes match length from a length slot.
  /// </summary>
  private int DecodeMatchLength(Rar5BitReader reader, int lengthSlot) {
    if (lengthSlot < 8)
      // Direct length encoding: slots 0-7 → lengths 2-9
      return lengthSlot + 2;

    // Extended length: use the length table
    var lenSym = this._lengthDecoder.DecodeSymbol(reader);
    return DecodeExtraLength(reader, lenSym) + 8;
  }

  /// <summary>
  /// Decodes extra length value from a length table symbol.
  /// </summary>
  private static int DecodeExtraLength(Rar5BitReader reader, int lenSym) {
    if (lenSym < 8)
      return lenSym + 2;

    var extraBits = lenSym / 2 - 1;
    var baseLen = (2 + (lenSym & 1)) << extraBits;
    return baseLen + (int)reader.ReadBits(extraBits) + 2;
  }

  /// <summary>
  /// Decodes a match distance from the offset and low-offset tables.
  /// </summary>
  private int DecodeDistance(Rar5BitReader reader) {
    var slot = this._offsetDecoder.DecodeSymbol(reader);

    if (slot < 4)
      return slot + 1;

    var extraBits = (slot - 2) >> 1;
    var baseDist = (2 + (slot & 1)) << extraBits;

    int distance;
    if (extraBits > 4) {
      // High bits from stream, low 4 bits from low-offset table
      var highBits = (int)reader.ReadBits(extraBits - 4);
      var lowBits = this._lowOffsetDecoder.DecodeSymbol(reader);
      distance = baseDist + (highBits << 4) + lowBits + 1;
    } else {
      var extra = (int)reader.ReadBits(extraBits);
      distance = baseDist + extra + 1;
    }

    return distance;
  }

  /// <summary>
  /// Shifts the repeat offset array and inserts a new distance at position 0.
  /// </summary>
  private void ShiftRepeatOffsets(int distance) => 
    (this._repDist[3], this._repDist[2], this._repDist[1], this._repDist[0]) 
    = (this._repDist[2], this._repDist[1], this._repDist[0], distance);

  /// <summary>
  /// Copies a match from the sliding window to the output.
  /// </summary>
  private int CopyMatch(byte[] output, int outputPos, int maxOutput, int distance, int length) {
    var copied = 0;
    for (var i = 0; i < length && outputPos + copied < maxOutput; ++i) {
      var b = this._window[(this._windowPos - distance) & this._windowMask];
      output[outputPos + copied] = b;
      this._window[this._windowPos & this._windowMask] = b;
      ++this._windowPos;
      ++copied;
    }
    return copied;
  }

  /// <summary>
  /// Applies any pending filters to the output data.
  /// </summary>
  private byte[] ApplyFilters(byte[] output) {
    if (this._filters.Count == 0)
      return output;

    var result = output;
    foreach (var filter in this._filters) {
      if (filter.BlockStart < 0 || filter.BlockStart + filter.BlockLength > result.Length)
        continue;

      var block = result.AsSpan(filter.BlockStart, filter.BlockLength);
      var transformed = Rar5Filters.Apply(filter.Type, block, filter.Channels);
      transformed.CopyTo(result, filter.BlockStart);
    }

    this._filters.Clear();
    return result;
  }

  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  [StackTraceHidden]
  private static void ThrowInvalidData(string message) => throw new InvalidDataException(message);
}
