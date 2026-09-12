#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Cso;

/// <summary>
/// Minimal raw-LZ4 block codec used by ZSO and CSO v2.
/// </summary>
/// <remarks>
/// Clean-room implementation from the published LZ4 block format. This is deliberately a block
/// codec, not an LZ4 frame codec: CSO/ZSO supply block boundaries and the decoded size themselves.
/// </remarks>
internal static class Lz4BlockCodec {
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int MinimumMatch = 4;
  private const int MaximumOffset = ushort.MaxValue;
  private const int LastLiterals = 5;
  private const int MatchFindLimit = 12;

  /// <summary>Compresses one independent LZ4 block.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    if (source.IsEmpty)
      return [];

    var table = new int[HashSize];
    Array.Fill(table, -1);
    using var output = new MemoryStream(source.Length);

    var anchor = 0;
    var current = 0;
    var searchLimit = source.Length - MatchFindLimit;
    while (current <= searchLimit) {
      var hash = Hash(source, current);
      var candidate = table[hash];
      table[hash] = current;

      if (candidate < 0 || current - candidate > MaximumOffset ||
          !source.Slice(candidate, MinimumMatch).SequenceEqual(source.Slice(current, MinimumMatch))) {
        ++current;
        continue;
      }

      // A compliant LZ4 block keeps its final five source bytes literal and starts the final
      // match at least twelve bytes before the end. The search limit above enforces the latter;
      // this boundary enforces the former.
      var matchEnd = current + MinimumMatch;
      var candidateEnd = candidate + MinimumMatch;
      var maximumMatchEnd = source.Length - LastLiterals;
      while (matchEnd < maximumMatchEnd && source[candidateEnd] == source[matchEnd]) {
        ++matchEnd;
        ++candidateEnd;
      }

      var literalLength = current - anchor;
      var matchLength = matchEnd - current;
      WriteSequence(output, source.Slice(anchor, literalLength), current - candidate, matchLength);

      var previous = current;
      current = matchEnd;
      anchor = current;

      // Seed hashes inside the match. This is not required for validity, but makes repeated
      // structured sectors compress usefully without introducing a heavyweight search chain.
      for (var position = previous + 1; position < current && position <= searchLimit; ++position)
        table[Hash(source, position)] = position;
    }

    WriteLastLiterals(output, source[anchor..]);
    return output.ToArray();
  }

  /// <summary>Decompresses one raw LZ4 block to exactly <paramref name="expectedSize"/> bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize) {
    if (expectedSize < 0)
      throw new ArgumentOutOfRangeException(nameof(expectedSize));
    if (expectedSize == 0)
      return [];

    var output = new byte[expectedSize];
    var inputOffset = 0;
    var outputOffset = 0;

    while (outputOffset < expectedSize) {
      if (inputOffset >= source.Length)
        throw new InvalidDataException("Truncated LZ4 block: missing sequence token.");

      var token = source[inputOffset++];
      var literalLength = ReadLength(source, ref inputOffset, token >> 4);
      if (literalLength > source.Length - inputOffset || literalLength > expectedSize - outputOffset)
        throw new InvalidDataException("Invalid LZ4 block: literal run exceeds its input or output boundary.");

      source.Slice(inputOffset, literalLength).CopyTo(output.AsSpan(outputOffset));
      inputOffset += literalLength;
      outputOffset += literalLength;

      // A final sequence consists solely of literals. CSO/ZSO alignment padding may follow it in
      // the indexed span, so once the requested decoded block is complete the remaining bytes are
      // intentionally ignored.
      if (outputOffset == expectedSize)
        return output;

      if (source.Length - inputOffset < 2)
        throw new InvalidDataException("Truncated LZ4 block: missing match offset.");
      var matchOffset = BinaryPrimitives.ReadUInt16LittleEndian(source[inputOffset..]);
      inputOffset += 2;
      if (matchOffset == 0 || matchOffset > outputOffset)
        throw new InvalidDataException("Invalid LZ4 block: match offset points before the decoded prefix.");

      var matchLength = ReadLength(source, ref inputOffset, token & 0x0F) + MinimumMatch;
      if (matchLength > expectedSize - outputOffset)
        throw new InvalidDataException("Invalid LZ4 block: match run exceeds the decoded block size.");

      var matchSource = outputOffset - matchOffset;
      for (var i = 0; i < matchLength; ++i)
        output[outputOffset++] = output[matchSource + i];
    }

    return output;
  }

  private static void WriteSequence(Stream output, ReadOnlySpan<byte> literals, int offset, int matchLength) {
    var matchCode = matchLength - MinimumMatch;
    var token = (byte)((Math.Min(literals.Length, 15) << 4) | Math.Min(matchCode, 15));
    output.WriteByte(token);
    if (literals.Length >= 15)
      WriteExtendedLength(output, literals.Length - 15);
    output.Write(literals);

    Span<byte> offsetBytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(offsetBytes, checked((ushort)offset));
    output.Write(offsetBytes);
    if (matchCode >= 15)
      WriteExtendedLength(output, matchCode - 15);
  }

  private static void WriteLastLiterals(Stream output, ReadOnlySpan<byte> literals) {
    output.WriteByte((byte)(Math.Min(literals.Length, 15) << 4));
    if (literals.Length >= 15)
      WriteExtendedLength(output, literals.Length - 15);
    output.Write(literals);
  }

  private static void WriteExtendedLength(Stream output, int remaining) {
    while (remaining >= 255) {
      output.WriteByte(255);
      remaining -= 255;
    }
    output.WriteByte((byte)remaining);
  }

  private static int ReadLength(ReadOnlySpan<byte> source, ref int offset, int nibble) {
    if (nibble < 15)
      return nibble;

    var result = 15;
    while (true) {
      if (offset >= source.Length)
        throw new InvalidDataException("Truncated LZ4 block: missing extended length byte.");
      var value = source[offset++];
      result = checked(result + value);
      if (value != 255)
        return result;
    }
  }

  private static int Hash(ReadOnlySpan<byte> source, int offset) {
    var value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, sizeof(uint)));
    return (int)(value * 2654435761u >> (32 - HashBits));
  }
}
