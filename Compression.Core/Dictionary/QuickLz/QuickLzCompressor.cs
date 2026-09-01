using System.Buffers.Binary;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>Encodes the QuickLZ 1.5.0 level-1 payload format.</summary>
/// <remarks>
/// This is the payload below the QuickLZ 3/9-byte packet header. Level 1 references a 4096-entry
/// destination hash table by index; it does not store a byte distance. Control words describe up to
/// 31 tokens from least-significant bit upward and keep bit 31 set as the group sentinel.
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
  private const uint ControlSentinel = 1u << 31;

  /// <summary>Compresses <paramref name="data"/> as a QuickLZ 1.5.0 level-1 payload.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
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
            TryFindMatch(data, sourceOffset, hashTable, out var hash, out var matchLength)) {
          control |= 1u << token;
          WriteReference(output, hash, matchLength);

          var phraseStart = sourceOffset;
          sourceOffset += matchLength;
          UpdateHashes(data, hashTable, ref nextHashed, phraseStart + 1);
          nextHashed = sourceOffset;
          continue;
        }

        output.WriteByte(data[sourceOffset++]);
        if (sourceOffset <= data.Length - TailLiteralCount)
          UpdateHashes(data, hashTable, ref nextHashed, sourceOffset - 2);
      }

      PatchControlWord(output, controlOffset, control);
    }

    return output.ToArray();
  }

  private static bool TryFindMatch(ReadOnlySpan<byte> data, int sourceOffset, int[] hashTable,
      out int hash, out int matchLength) {
    hash = Hash(data, sourceOffset);
    var candidate = hashTable[hash];
    matchLength = 0;

    // QuickLZ level 1 has a special distance-one case. It is an encoder optimization, not a
    // required wire feature. Emitting only ordinary distance >= 3 references is fully compatible
    // and avoids manufacturing matches whose decoder-side hash visibility is less obvious.
    if (candidate < 0 || sourceOffset - candidate < MinMatch ||
        !data.Slice(candidate, MinMatch).SequenceEqual(data.Slice(sourceOffset, MinMatch)))
      return false;

    var maximum = Math.Min(MaxMatch, data.Length - UncompressedEnd - sourceOffset);
    matchLength = MinMatch;
    while (matchLength < maximum && data[candidate + matchLength] == data[sourceOffset + matchLength])
      ++matchLength;
    return true;
  }

  private static void WriteReference(Stream output, int hash, int matchLength) {
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

  private static void UpdateHashes(ReadOnlySpan<byte> data, int[] hashTable, ref int nextHashed,
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

  private static void PatchControlWord(MemoryStream output, long offset, uint value) {
    var saved = output.Position;
    output.Position = offset;
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
    output.Position = saved;
  }
}
