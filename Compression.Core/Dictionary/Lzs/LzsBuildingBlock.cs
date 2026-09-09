using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzs;

/// <summary>Encoder effort used by the managed Stac LZS implementation.</summary>
public enum LzsCompressionLevel {
  /// <summary>Small hash-chain search for throughput-sensitive callers.</summary>
  Fast,
  /// <summary>Balanced hash-chain search; this is the default.</summary>
  Balanced,
  /// <summary>Search the complete LZS history window and use one-byte lazy parsing.</summary>
  Maximum,
}

/// <summary>
/// Exposes Stac LZS (RFC 1967/2395) as a benchmarkable building block.
/// An LZSS variant using 7-bit offsets (1-127) and 11-bit offsets (128-2047),
/// with variable-length match lengths. Used in Cisco IOS and Stac hardware compression.
/// </summary>
public sealed class LzsBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzs";
  /// <inheritdoc/>
  public string DisplayName => "LZS";
  /// <inheritdoc/>
  public string Description => "Stac LZS (RFC 1967/2395), 7/11-bit offset LZSS variant for networking";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MaxOffset = 2047;
  private const int MinMatch = 2;
  private const int FastChain = 16;
  private const int BalancedChain = 128;

  private readonly record struct Match(int Length, int Offset) {
    public bool Exists => this.Length >= MinMatch;
  }

  private readonly record struct CompressionSettings(int MaxChain, bool Lazy);

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => this.Compress(data, LzsCompressionLevel.Balanced);

  /// <summary>Compresses <paramref name="data"/> with the requested encoder effort.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data, LzsCompressionLevel level) {
    var settings = level switch {
      LzsCompressionLevel.Fast => new CompressionSettings(FastChain, Lazy: false),
      LzsCompressionLevel.Balanced => new CompressionSettings(BalancedChain, Lazy: false),
      LzsCompressionLevel.Maximum => new CompressionSettings(MaxOffset, Lazy: true),
      _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    using var ms = new MemoryStream();

    // The file-format wrapper stores the uncompressed size before the RFC bitstream.
    Span<byte> headerBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(headerBuf, data.Length);
    ms.Write(headerBuf);

    var writer = new BitWriter(ms);
    var hashHead = new int[1 << 14];
    var hashPrev = new int[data.Length];
    Array.Fill(hashHead, -1);

    var pos = 0;
    while (pos < data.Length) {
      var best = FindBestMatch(data, pos, hashHead, hashPrev, settings.MaxChain);
      InsertPosition(data, pos, hashHead, hashPrev);

      if (settings.Lazy && best.Exists && pos + 1 < data.Length) {
        var next = FindBestMatch(data, pos + 1, hashHead, hashPrev, settings.MaxChain);
        if (ShouldDefer(best, next))
          best = default;
      }

      if (best.Exists) {
        WriteMatch(writer, best);

        // Make every byte that the decoder will have produced available to later matches.
        for (var j = 1; j < best.Length; ++j)
          InsertPosition(data, pos + j, hashHead, hashPrev);
        pos += best.Length;
      } else {
        writer.WriteBit(0);
        WriteBits(writer, data[pos], 8);
        ++pos;
      }
    }

    // RFC 2395 section 2.2: 1 + short-offset selector + zero 7-bit offset.
    writer.WriteBit(1);
    writer.WriteBit(1);
    WriteBits(writer, 0, 7);
    writer.Flush();
    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < sizeof(int))
      throw new InvalidDataException("LZS stream is missing the uncompressed-size header.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize < 0)
      throw new InvalidDataException("LZS stream declares a negative uncompressed size.");

    var src = data[4..];
    if (originalSize == 0 && src.IsEmpty)
      return []; // Accept the historical empty wrapper, which omitted the RFC end marker.

    var bitIndex = 0L;
    var result = new byte[originalSize];
    var pos = 0;
    var sawEndMarker = false;

    while (!sawEndMarker) {
      var flag = ReadBit(src, ref bitIndex);
      if (flag == 0) {
        if (pos >= originalSize)
          throw new InvalidDataException("LZS stream contains data beyond its declared uncompressed size.");
        result[pos++] = (byte)ReadBits(src, ref bitIndex, 8);
        continue;
      }

      var shortOffset = ReadBit(src, ref bitIndex) == 1;
      var offset = ReadBits(src, ref bitIndex, shortOffset ? 7 : 11);
      if (shortOffset && offset == 0) {
        sawEndMarker = true;
        continue;
      }
      if (offset <= 0 || offset > MaxOffset || offset > pos)
        throw new InvalidDataException("LZS stream contains an invalid match offset.");

      var length = ReadLength(src, ref bitIndex);
      if (length > originalSize - pos)
        throw new InvalidDataException("LZS match exceeds the declared uncompressed size.");

      var srcPos = pos - offset;
      for (var j = 0; j < length; ++j)
        result[pos++] = result[srcPos + j];
    }

    if (pos != originalSize)
      throw new InvalidDataException("LZS end marker occurs before the declared uncompressed size.");

    return result;
  }

  private static Match FindBestMatch(ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashPrev, int maxChain) {
    if (pos + 1 >= data.Length)
      return default;

    var idx = hashHead[HashAt(data, pos)];
    var minPos = Math.Max(0, pos - MaxOffset);
    var maxLength = data.Length - pos;
    var best = default(Match);
    var bestSavings = 0L;
    var chainLength = 0;

    while (idx >= minPos && chainLength++ < maxChain) {
      var length = 0;
      while (length < maxLength && data[idx + length] == data[pos + length])
        ++length;

      if (length >= MinMatch) {
        var candidate = new Match(length, pos - idx);
        var savings = MatchSavings(candidate);
        if (savings > bestSavings
            || savings == bestSavings && candidate.Length > best.Length
            || savings == bestSavings && candidate.Length == best.Length && candidate.Offset < best.Offset) {
          best = candidate;
          bestSavings = savings;
        }

        // Nothing can beat consuming all remaining bytes with the cheaper short-offset form.
        if (length == maxLength && candidate.Offset <= 127)
          break;
      }

      idx = hashPrev[idx];
    }

    return best;
  }

  private static void InsertPosition(ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashPrev) {
    if (pos + 1 >= data.Length)
      return;

    var hash = HashAt(data, pos);
    hashPrev[pos] = hashHead[hash];
    hashHead[hash] = pos;
  }

  private static bool ShouldDefer(Match current, Match next) {
    if (!next.Exists)
      return false;

    // A literal costs nine bits. Only defer when the next match recovers that cost
    // and is not merely a different encoding of the same amount of progress.
    var currentSavings = MatchSavings(current);
    var nextSavings = MatchSavings(next);
    return next.Length > current.Length + 1 && nextSavings > currentSavings;
  }

  private static long MatchSavings(Match match)
    => 9L * match.Length - MatchBitCost(match.Offset, match.Length);

  private static int MatchBitCost(int offset, int length)
    => (offset <= 127 ? 9 : 13) + LengthBitCost(length);

  private static int LengthBitCost(int length) => length switch {
    <= 4 => 2,
    <= 7 => 4,
    _ => 8 + 4 * ((length - 8) / 15),
  };

  private static void WriteMatch(BitWriter writer, Match match) {
    writer.WriteBit(1);
    if (match.Offset <= 127) {
      writer.WriteBit(1);
      WriteBits(writer, match.Offset, 7);
    } else {
      writer.WriteBit(0);
      WriteBits(writer, match.Offset, 11);
    }
    WriteLength(writer, match.Length);
  }

  private static void WriteLength(BitWriter writer, int length) {
    switch (length) {
      case 2:
      case 3:
      case 4:
        WriteBits(writer, length - 2, 2);
        return;
      case 5:
      case 6:
      case 7:
        WriteBits(writer, 0b11, 2);
        WriteBits(writer, length - 5, 2);
        return;
    }

    WriteBits(writer, 0b1111, 4);
    var remaining = length - 8;
    while (remaining >= 15) {
      WriteBits(writer, 0xF, 4);
      remaining -= 15;
    }
    WriteBits(writer, remaining, 4);
  }

  private static int ReadLength(ReadOnlySpan<byte> data, ref long bitIndex) {
    var first = ReadBits(data, ref bitIndex, 2);
    if (first < 3)
      return first + 2;

    var second = ReadBits(data, ref bitIndex, 2);
    if (second < 3)
      return second + 5;

    var length = 8;
    while (true) {
      var nibble = ReadBits(data, ref bitIndex, 4);
      try {
        length = checked(length + nibble);
      } catch (OverflowException ex) {
        throw new InvalidDataException("LZS match length exceeds the supported range.", ex);
      }
      if (nibble != 0xF)
        return length;
    }
  }

  private static int HashAt(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 6) ^ data[pos + 1]) & 0x3FFF;

  private static void WriteBits(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; --i)
      writer.WriteBit((value >> i) & 1);
  }

  private static int ReadBit(ReadOnlySpan<byte> data, ref long bitIndex) {
    var byteIndex = bitIndex >> 3;
    if (byteIndex >= data.Length)
      throw new InvalidDataException("Unexpected end of LZS bitstream.");
    var bit = (data[(int)byteIndex] >> (7 - (int)(bitIndex & 7))) & 1;
    ++bitIndex;
    return bit;
  }

  private static int ReadBits(ReadOnlySpan<byte> data, ref long bitIndex, int count) {
    var value = 0;
    for (var i = 0; i < count; ++i)
      value = (value << 1) | ReadBit(data, ref bitIndex);
    return value;
  }

  private sealed class BitWriter(Stream output) {
    private byte _buffer;
    private int _bitCount;

    public void WriteBit(int bit) {
      this._buffer = (byte)((this._buffer << 1) | (bit & 1));
      if (++this._bitCount != 8)
        return;
      output.WriteByte(this._buffer);
      this._buffer = 0;
      this._bitCount = 0;
    }

    public void Flush() {
      if (this._bitCount <= 0)
        return;
      this._buffer <<= 8 - this._bitCount;
      output.WriteByte(this._buffer);
      this._buffer = 0;
      this._bitCount = 0;
    }
  }
}
