using System.Buffers.Binary;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>Encodes QuickLZ 1.5.0 level-1 and level-3 payloads.</summary>
/// <remarks>
/// Level 1 references a 4096-entry destination hash table by index. Level 3 instead writes relative
/// offsets and searches a configurable ring of candidates per hash; the QuickLZ 1.5.0 reference
/// configuration uses 16 candidates. Control words describe up to 31 tokens from least-significant
/// bit upward and keep bit 31 set as the group sentinel.
/// </remarks>
public static class QuickLzCompressor {
  private const int HashSize = 4096;
  private const int HashMask = HashSize - 1;
  private const int MinMatch = 3;
  private const int LongMatchThreshold = 18;
  private const int MaxMatch = 255;
  private const int TailLiteralCount = 10;
  private const int UncompressedEnd = 4;
  private const int TokensPerControlWord = 31;
  private const int Level3MaxOffsetExclusive = 0x1FFFF;
  private const uint ControlSentinel = 1u << 31;

  /// <summary>Maximum number of match candidates searched per hash in level 3.</summary>
  public const int Level3MaxSearchDepth = 16;

  /// <summary>Compresses <paramref name="data"/> as a QuickLZ 1.5.0 level-1 payload.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data) => Compress(data, QuickLzCompressionLevel.Level1);

  /// <summary>
  /// Compresses <paramref name="data"/> using the selected QuickLZ level.
  /// </summary>
  /// <param name="level3SearchDepth">
  /// Number of recent hash candidates considered by level 3. QuickLZ 1.5.0 defines 16 as its
  /// best-ratio reference setting; smaller values trade ratio for compression speed. Ignored by level 1.
  /// </param>
  public static byte[] Compress(ReadOnlySpan<byte> data, QuickLzCompressionLevel level,
      int level3SearchDepth = Level3MaxSearchDepth) => level switch {
    QuickLzCompressionLevel.Level1 => CompressLevel1(data),
    QuickLzCompressionLevel.Level3 => CompressLevel3(data, ValidateLevel3SearchDepth(level3SearchDepth)),
    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "QuickLZ supports compression levels 1 and 3."),
  };

  private static byte[] CompressLevel1(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    using var output = new MemoryStream(data.Length);
    var hashTable = new int[HashSize];
    hashTable.AsSpan().Fill(-1);
    var nextHashed = 0;
    var sourceOffset = 0;
    Span<byte> emptyControl = stackalloc byte[4];
    emptyControl.Clear();

    while (sourceOffset < data.Length) {
      var controlOffset = output.Position;
      output.Write(emptyControl);
      uint control = ControlSentinel;

      for (var token = 0; token < TokensPerControlWord && sourceOffset < data.Length; ++token) {
        if (sourceOffset < data.Length - TailLiteralCount &&
            TryFindLevel1Match(data, sourceOffset, hashTable, out var hash, out var matchLength)) {
          control |= 1u << token;
          WriteLevel1Reference(output, hash, matchLength);

          var phraseStart = sourceOffset;
          sourceOffset += matchLength;
          UpdateLevel1Hashes(data, hashTable, ref nextHashed, phraseStart + 1);
          nextHashed = sourceOffset;
          continue;
        }

        output.WriteByte(data[sourceOffset++]);
        if (sourceOffset <= data.Length - TailLiteralCount)
          UpdateLevel1Hashes(data, hashTable, ref nextHashed, sourceOffset - 2);
      }

      PatchControlWord(output, controlOffset, control);
    }

    return output.ToArray();
  }

  private static byte[] CompressLevel3(ReadOnlySpan<byte> data, int searchDepth) {
    if (data.IsEmpty)
      return [];

    using var output = new MemoryStream(data.Length);
    var candidates = new int[HashSize * searchDepth];
    candidates.AsSpan().Fill(-1);
    var candidateCounts = new int[HashSize];
    var sourceOffset = 0;
    Span<byte> emptyControl = stackalloc byte[4];
    emptyControl.Clear();

    while (sourceOffset < data.Length) {
      var controlOffset = output.Position;
      output.Write(emptyControl);
      uint control = ControlSentinel;

      for (var token = 0; token < TokensPerControlWord && sourceOffset < data.Length; ++token) {
        var matchLength = 0;
        var matchDistance = 0;
        if (sourceOffset < data.Length - TailLiteralCount) {
          FindLevel3Match(data, sourceOffset, candidates, candidateCounts, searchDepth,
            out matchDistance, out matchLength);
          AddLevel3Candidate(data, sourceOffset, candidates, candidateCounts, searchDepth);
        }

        if (matchLength >= MinMatch) {
          control |= 1u << token;
          WriteLevel3Reference(output, matchDistance, matchLength);

          for (var index = 1; index < matchLength; ++index)
            AddLevel3Candidate(data, sourceOffset + index, candidates, candidateCounts, searchDepth);
          sourceOffset += matchLength;
          continue;
        }

        output.WriteByte(data[sourceOffset++]);
      }

      PatchControlWord(output, controlOffset, control);
    }

    return output.ToArray();
  }

  private static bool TryFindLevel1Match(ReadOnlySpan<byte> data, int sourceOffset, int[] hashTable,
      out int hash, out int matchLength) {
    hash = Hash(data, sourceOffset);
    var candidate = hashTable[hash];
    matchLength = 0;

    if (candidate < 0 || sourceOffset - candidate < MinMatch ||
        !data.Slice(candidate, MinMatch).SequenceEqual(data.Slice(sourceOffset, MinMatch)))
      return false;

    var maximum = Math.Min(MaxMatch, data.Length - UncompressedEnd - sourceOffset);
    matchLength = MinMatch;
    while (matchLength < maximum && data[candidate + matchLength] == data[sourceOffset + matchLength])
      ++matchLength;
    return true;
  }

  private static void FindLevel3Match(ReadOnlySpan<byte> data, int sourceOffset, int[] candidates,
      int[] candidateCounts, int searchDepth, out int matchDistance, out int matchLength) {
    var hash = Hash(data, sourceOffset);
    var count = Math.Min(candidateCounts[hash], searchDepth);
    var candidateBase = hash * searchDepth;
    var maximum = Math.Min(MaxMatch, data.Length - UncompressedEnd - sourceOffset);
    var bestPosition = -1;
    matchLength = 0;

    for (var slot = 0; slot < count; ++slot) {
      var candidate = candidates[candidateBase + slot];
      var distance = sourceOffset - candidate;
      if (candidate < 0 || distance < MinMatch || distance >= Level3MaxOffsetExclusive)
        continue;
      if (data[candidate] != data[sourceOffset] ||
          data[candidate + 1] != data[sourceOffset + 1] ||
          data[candidate + 2] != data[sourceOffset + 2])
        continue;

      var length = MinMatch;
      while (length < maximum && data[candidate + length] == data[sourceOffset + length])
        ++length;

      if (length > matchLength || length == matchLength && candidate > bestPosition) {
        matchLength = length;
        bestPosition = candidate;
      }
    }

    matchDistance = bestPosition < 0 ? 0 : sourceOffset - bestPosition;
  }

  private static void AddLevel3Candidate(ReadOnlySpan<byte> data, int sourceOffset, int[] candidates,
      int[] candidateCounts, int searchDepth) {
    var hash = Hash(data, sourceOffset);
    var count = candidateCounts[hash];
    candidates[hash * searchDepth + count % searchDepth] = sourceOffset;
    candidateCounts[hash] = count + 1;
  }

  private static void WriteLevel1Reference(Stream output, int hash, int matchLength) {
    Span<byte> token = stackalloc byte[3];
    if (matchLength < LongMatchThreshold) {
      var value = (hash << 4) | (matchLength - 2);
      BinaryPrimitives.WriteUInt16LittleEndian(token, checked((ushort)value));
      output.Write(token[..2]);
      return;
    }

    var longValue = hash << 4;
    BinaryPrimitives.WriteUInt16LittleEndian(token, checked((ushort)longValue));
    token[2] = checked((byte)matchLength);
    output.Write(token);
  }

  private static void WriteLevel3Reference(Stream output, int distance, int matchLength) {
    Span<byte> token = stackalloc byte[4];
    if (matchLength == MinMatch && distance < 1 << 6) {
      output.WriteByte(checked((byte)(distance << 2)));
      return;
    }

    if (matchLength == MinMatch && distance < 1 << 14) {
      var value = (distance << 2) | 1;
      BinaryPrimitives.WriteUInt16LittleEndian(token, checked((ushort)value));
      output.Write(token[..2]);
      return;
    }

    if (matchLength - 3 < 1 << 4 && distance < 1 << 10) {
      var value = (distance << 6) | ((matchLength - 3) << 2) | 2;
      BinaryPrimitives.WriteUInt16LittleEndian(token, checked((ushort)value));
      output.Write(token[..2]);
      return;
    }

    if (matchLength - 2 < 1 << 5) {
      var value = ((uint)distance << 7) | ((uint)(matchLength - 2) << 2) | 3u;
      BinaryPrimitives.WriteUInt32LittleEndian(token, value);
      output.Write(token[..3]);
      return;
    }

    var longValue = ((uint)distance << 15) | ((uint)(matchLength - 3) << 7) | 3u;
    BinaryPrimitives.WriteUInt32LittleEndian(token, longValue);
    output.Write(token);
  }

  private static void UpdateLevel1Hashes(ReadOnlySpan<byte> data, int[] hashTable, ref int nextHashed,
      int endExclusive) {
    var maximumEnd = Math.Min(endExclusive, data.Length - 2);
    while (nextHashed < maximumEnd) {
      hashTable[Hash(data, nextHashed)] = nextHashed;
      ++nextHashed;
    }
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (value ^ (value >> 12)) & HashMask;
  }

  private static int ValidateLevel3SearchDepth(int searchDepth) {
    if (searchDepth is < 1 or > Level3MaxSearchDepth)
      throw new ArgumentOutOfRangeException(nameof(searchDepth), searchDepth,
        $"QuickLZ level-3 search depth must be between 1 and {Level3MaxSearchDepth}.");
    return searchDepth;
  }

  private static void PatchControlWord(MemoryStream output, long offset, uint value) {
    var saved = output.Position;
    output.Position = offset;
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
    output.Position = saved;
  }
}
