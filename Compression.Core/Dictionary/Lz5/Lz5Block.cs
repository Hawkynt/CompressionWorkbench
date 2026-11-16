using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lz5;

/// <summary>
/// The LZ5 1.5 block format (Przemysław Skibiński's LZ4 derivative, the coder 7-Zip-zstd registers
/// as 04 F7 11 05): sequences of a token, literals, an offset and a match length, with three
/// offset widths and a repeat-offset code.
/// </summary>
/// <remarks>
/// <para>Converted from lz5 1.5 <c>lib/lz5.c</c> (<c>LZ5_decompress_generic</c>) and
/// <c>lib/lz5common.h</c> — see THIRD-PARTY-NOTICE.lz5.txt. The token:</para>
/// <list type="bullet">
///   <item><description><c>1hh LL MMM</c>: 2-bit literal length, a 10-bit offset whose high bits are
///   <c>hh</c> and whose low byte follows the literals.</description></item>
///   <item><description><c>00 LLL MMM</c>: 3-bit literal length, a 16-bit little-endian offset.</description></item>
///   <item><description><c>010 LL MMM</c>: 2-bit literal length, a 24-bit little-endian offset.</description></item>
///   <item><description><c>011 LL MMM</c>: 2-bit literal length, the previous offset again (1 at the start of a block).</description></item>
/// </list>
/// <para>A literal length field at its maximum (3 or 7) and a match length field of 7 continue in
/// bytes that add up while they are 255, as in LZ4. Matches are at least 3 bytes and reach back at
/// most 4 MiB − 1. The last sequence of a block is literals only and ends the block.</para>
/// </remarks>
public static class Lz5Block {
  /// <summary>The shortest match.</summary>
  public const int MinMatch = 3;

  /// <summary>The farthest a match reaches back: 4 MiB − 1.</summary>
  public const int MaxDistance = (1 << 22) - 1;

  // The parsing restrictions the reference decoder's fast paths rely on: the last 5 bytes of a
  // block are literals, and no match starts in its last 12 bytes.
  private const int LastLiterals = 5;
  private const int MatchStartLimit = 12;

  private const int ShortOffsetLimit = 1 << 10;
  private const int MidOffsetLimit = 1 << 16;
  private const int HashLog = 16;

  /// <summary>
  /// Decodes one block from <paramref name="source"/> into <paramref name="window"/> after its
  /// first <paramref name="prefixLength"/> bytes, which hold the history matches may reach into.
  /// Returns the number of bytes decoded.
  /// </summary>
  /// <exception cref="InvalidDataException">The block is corrupt or does not fit.</exception>
  public static int Decode(ReadOnlySpan<byte> source, Span<byte> window, int prefixLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, window.Length);
    var ip = 0;
    var op = prefixLength;
    var lastOffset = 1;
    while (true) {
      if (ip >= source.Length)
        throw new InvalidDataException("LZ5: the block ends inside a sequence.");
      int token = source[ip++];

      var wide = (token >> 6) == 0;
      var literals = wide ? (token >> 3) & 7 : (token >> 3) & 3;
      if (literals == (wide ? 7 : 3))
        literals += ReadLength(source, ref ip);
      if (literals > source.Length - ip)
        throw new InvalidDataException("LZ5: literals run past the end of the block.");
      if (literals > window.Length - op)
        throw new InvalidDataException("LZ5: the block decodes to more than its maximum size.");
      source.Slice(ip, literals).CopyTo(window[op..]);
      ip += literals;
      op += literals;
      if (ip == source.Length)
        return op - prefixLength;

      int offset;
      if ((token & 0x80) != 0) {
        offset = source[ip++] | (((token >> 5) & 3) << 8);
      } else if (wide) {
        if (source.Length - ip < 2)
          throw new InvalidDataException("LZ5: truncated offset.");
        offset = BinaryPrimitives.ReadUInt16LittleEndian(source[ip..]);
        ip += 2;
      } else if ((token >> 5) == 2) {
        if (source.Length - ip < 3)
          throw new InvalidDataException("LZ5: truncated offset.");
        offset = source[ip] | (source[ip + 1] << 8) | (source[ip + 2] << 16);
        ip += 3;
      } else
        offset = lastOffset;
      lastOffset = offset;
      if (offset == 0 || offset > op)
        throw new InvalidDataException("LZ5: a match reaches before the start of the data.");

      var length = token & 7;
      if (length == 7)
        length += ReadLength(source, ref ip);
      length += MinMatch;
      if (length > window.Length - op)
        throw new InvalidDataException("LZ5: the block decodes to more than its maximum size.");

      var from = op - offset;
      if (offset >= length)
        window.Slice(from, length).CopyTo(window[op..]);
      else
        for (var i = 0; i < length; ++i)
          window[op + i] = window[from + i];
      op += length;
    }
  }

  private static int ReadLength(ReadOnlySpan<byte> source, ref int ip) {
    var total = 0;
    int b;
    do {
      if (ip >= source.Length)
        throw new InvalidDataException("LZ5: truncated length.");
      b = source[ip++];
      total += b;
      if (total > int.MaxValue / 2)
        throw new InvalidDataException("LZ5: length out of range.");
    } while (b == 255);
    return total;
  }

  /// <summary>The largest block <see cref="Encode"/> can write for <paramref name="length"/> input bytes.</summary>
  public static int MaxEncodedLength(int length) => length + length / 255 + 16;

  /// <summary>
  /// Encodes <paramref name="source"/> as one independent block into <paramref name="destination"/>
  /// (at least <see cref="MaxEncodedLength"/> bytes) and returns its length: a greedy parse over a
  /// hash of 4-byte prefixes, choosing the shortest offset code for every match.
  /// </summary>
  public static int Encode(ReadOnlySpan<byte> source, Span<byte> destination) {
    if (destination.Length < MaxEncodedLength(source.Length))
      throw new ArgumentException("LZ5: the destination is smaller than the worst case.", nameof(destination));
    var op = 0;
    var anchor = 0;
    var lastOffset = 1;
    var n = source.Length;
    if (n > MatchStartLimit) {
      var table = new int[1 << HashLog];
      table.AsSpan().Fill(-1);
      var matchEndLimit = n - LastLiterals;
      for (var pos = 0; pos <= n - MatchStartLimit;) {
        var word = BinaryPrimitives.ReadUInt32LittleEndian(source[pos..]);
        var h = (int)((word * 2654435761u) >> (32 - HashLog));
        var candidate = table[h];
        table[h] = pos;
        if (candidate < 0 || pos - candidate > MaxDistance || BinaryPrimitives.ReadUInt32LittleEndian(source[candidate..]) != word) {
          ++pos;
          continue;
        }

        var length = 4;
        while (pos + length < matchEndLimit && source[candidate + length] == source[pos + length])
          ++length;
        var offset = pos - candidate;
        op = WriteSequence(destination, op, source[anchor..pos], offset, length, lastOffset);
        lastOffset = offset;
        pos += length;
        anchor = pos;
        // Index one position inside the match so a following repeat is found.
        if (pos - 2 > candidate && pos - 2 <= n - 4)
          table[(int)((BinaryPrimitives.ReadUInt32LittleEndian(source[(pos - 2)..]) * 2654435761u) >> (32 - HashLog))] = pos - 2;
      }
    }

    // The last literals: a 00-form token whose sequence ends the block.
    var tail = source[anchor..];
    op = WriteLiteralToken(destination, op, 0x00, 7, tail.Length);
    tail.CopyTo(destination[op..]);
    return op + tail.Length;
  }

  private static int WriteSequence(Span<byte> destination, int op, ReadOnlySpan<byte> literals, int offset, int length, int lastOffset) {
    var code = length - MinMatch;
    var matchField = Math.Min(code, 7);
    int token, literalMax, offsetBytes;
    if (offset == lastOffset) {
      (token, literalMax, offsetBytes) = (0x60, 3, 0);
    } else if (offset < ShortOffsetLimit) {
      (token, literalMax, offsetBytes) = (0x80 | ((offset >> 8) << 5), 3, 1);
    } else if (offset < MidOffsetLimit) {
      (token, literalMax, offsetBytes) = (0x00, 7, 2);
    } else {
      (token, literalMax, offsetBytes) = (0x40, 3, 3);
    }

    op = WriteLiteralToken(destination, op, token | matchField, literalMax, literals.Length);
    literals.CopyTo(destination[op..]);
    op += literals.Length;
    for (var i = 0; i < offsetBytes; ++i)
      destination[op++] = (byte)(offset >> (8 * i));
    if (matchField == 7)
      op = WriteLength(destination, op, code - 7);
    return op;
  }

  /// <summary>Writes the token with its literal-length field and the field's continuation bytes.</summary>
  private static int WriteLiteralToken(Span<byte> destination, int op, int token, int literalMax, int literals) {
    var field = Math.Min(literals, literalMax);
    destination[op++] = (byte)(token | (field << 3));
    return field == literalMax ? WriteLength(destination, op, literals - literalMax) : op;
  }

  private static int WriteLength(Span<byte> destination, int op, int value) {
    while (value >= 255) {
      destination[op++] = 255;
      value -= 255;
    }
    destination[op++] = (byte)value;
    return op;
  }
}
