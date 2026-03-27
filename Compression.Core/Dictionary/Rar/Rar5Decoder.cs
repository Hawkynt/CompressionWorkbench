using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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

    this._tablesRead = false;

    while (outputPos < uncompressedSize && !reader.IsAtEnd) {
      if (!this._tablesRead) {
        // Byte-aligned block header
        reader.AlignToByte();

        var blockFlags = (int)reader.ReadBits(8);
        reader.ReadBits(8); // checksum byte (0x5A ^ flags ^ XOR(size bytes))

        var byteCount = ((blockFlags >> 3) & 3) + 1;
        if (byteCount == 4)
          ThrowInvalidData("Invalid RAR5 block header byte count.");

        var blockSize = 0;
        for (var b = 0; b < byteCount; ++b)
          blockSize += (int)reader.ReadBits(8) << (b * 8);

        var tablePresent = (blockFlags & 0x80) != 0;
        if (tablePresent)
          this.ReadTables(reader);
        this._tablesRead = true;
      }

      var sym = this._mainDecoder.DecodeSymbol(reader);

      switch (sym) {
        case < Rar5Constants.LiteralCount:
          output[outputPos] = (byte)sym;
          this._window[this._windowPos & this._windowMask] = (byte)sym;
          ++this._windowPos;
          ++outputPos;
          break;

        case >= Rar5Constants.MatchBase and < Rar5Constants.MainTableSize: {
          var lengthSlot = sym - Rar5Constants.MatchBase;
          var matchLength = SlotToLength(reader, lengthSlot);
          var distance = this.DecodeDistance(reader);

          if (distance < 0)
            distance = 0;

          matchLength += Rar5Constants.LengthBonus(distance);

          this.ShiftRepeatOffsets(distance);
          this._lastLength = matchLength;
          outputPos += this.CopyMatch(output, outputPos, uncompressedSize, distance, matchLength);
          break;
        }

        case >= Rar5Constants.RepeatOffset0 and <= Rar5Constants.RepeatOffset3: {
          var repIdx = sym - Rar5Constants.RepeatOffset0;
          var distance = this._repDist[repIdx];

          for (var i = repIdx; i > 0; --i)
            this._repDist[i] = this._repDist[i - 1];
          this._repDist[0] = distance;

          int matchLength;
          if (sym == Rar5Constants.RepeatOffset0) {
            matchLength = this._lastLength;
            if (matchLength == 0)
              matchLength = 2;
          } else {
            var lenSlot = this._lengthDecoder.DecodeSymbol(reader);
            matchLength = SlotToLength(reader, lenSlot);
          }

          this._lastLength = matchLength;
          outputPos += this.CopyMatch(output, outputPos, uncompressedSize, distance, matchLength);
          break;
        }

        default: {
          if (sym == Rar5Constants.EndOfBlock)
            this._tablesRead = false;
          break;
        }
      }
    }

    output = this.ApplyFilters(output);
    return output;
  }

  /// <summary>
  /// Reads the Huffman tables from the bit stream.
  /// </summary>
  private void ReadTables(Rar5BitReader reader) {
    var clLengths = new int[Rar5Constants.CodeLengthTableSize];

    // Read pre-code lengths with special value-15 handling (matching 7-Zip/UnRAR):
    // If value == 15 and next 4 bits (count) > 0, fill count+2 zeros instead.
    // If value == 15 and next 4 bits == 0, store 15 as the code length.
    for (var i = 0; i < Rar5Constants.CodeLengthTableSize;) {
      var len = (int)reader.ReadBits(4);
      if (len == 15) {
        var count = (int)reader.ReadBits(4);
        if (count != 0) {
          for (var j = 0; j < count + 2 && i < Rar5Constants.CodeLengthTableSize; ++j)
            clLengths[i++] = 0;
          continue;
        }
      }
      clLengths[i++] = len;
    }

    var clDecoder = new Rar5HuffmanDecoder();
    clDecoder.Build(clLengths, Rar5Constants.CodeLengthTableSize);

    var mainLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.MainTableSize);
    this._mainDecoder.Build(mainLengths, Rar5Constants.MainTableSize);

    var offsetLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.OffsetTableSize);
    this._offsetDecoder.Build(offsetLengths, Rar5Constants.OffsetTableSize);

    var lowOffsetLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.LowOffsetTableSize);
    this._lowOffsetDecoder.Build(lowOffsetLengths, Rar5Constants.LowOffsetTableSize);

    var lengthLengths = ReadCodeLengths(reader, clDecoder, Rar5Constants.LengthTableSize);
    this._lengthDecoder.Build(lengthLengths, Rar5Constants.LengthTableSize);
  }

  /// <summary>
  /// Reads Huffman code lengths using the code-length decoder.
  /// RAR5 RLE:
  /// 16 repeat prev (3 bits +3), 17 repeat prev (7 bits +11),
  /// 18 fill zeros (3 bits +3), 19 fill zeros (7 bits +11).
  /// </summary>
  private static int[] ReadCodeLengths(Rar5BitReader reader, Rar5HuffmanDecoder clDecoder, int count) {
    var lengths = new int[count];
    var i = 0;

    while (i < count) {
      var sym = clDecoder.DecodeSymbol(reader);
      switch (sym) {
        case < 16:
          lengths[i++] = sym;
          break;

        case 16: {
          if (i == 0) ThrowInvalidData("Code length repeat at start.");
          var repeat = (int)reader.ReadBits(3) + 3;
          var prev = lengths[i - 1];
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = prev;
          break;
        }

        case 17: {
          if (i == 0) ThrowInvalidData("Code length repeat at start.");
          var repeat = (int)reader.ReadBits(7) + 11;
          var prev = lengths[i - 1];
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = prev;
          break;
        }

        case 18: {
          var repeat = (int)reader.ReadBits(3) + 3;
          for (var j = 0; j < repeat && i < count; ++j)
            lengths[i++] = 0;
          break;
        }

        case 19: {
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
  /// SlotToLength: converts a slot to a match length.
  /// Slot 0-7: Length = 2 + Slot. Slot 8+: LBits = Slot/4 - 1, base = 2 + ((4|(Slot and 3)) shl LBits).
  /// </summary>
  private static int SlotToLength(Rar5BitReader reader, int slot) {
    if (slot < 8)
      return slot + 2;

    var lBits = slot / 4 - 1;
    var length = 2 + ((4 | (slot & 3)) << lBits);
    if (lBits > 0)
      length += (int)reader.ReadBits(lBits);
    return length;
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
    if (extraBits >= 4) {
      var highBits = extraBits > 4 ? (int)reader.ReadBits(extraBits - 4) : 0;
      var lowBits = this._lowOffsetDecoder.DecodeSymbol(reader);
      distance = baseDist + (highBits << 4) + lowBits + 1;
    } else {
      var extra = (int)reader.ReadBits(extraBits);
      distance = baseDist + extra + 1;
    }

    return distance;
  }

  private void ShiftRepeatOffsets(int distance) =>
    (this._repDist[3], this._repDist[2], this._repDist[1], this._repDist[0])
    = (this._repDist[2], this._repDist[1], this._repDist[0], distance);

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
