using System.Buffers.Binary;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>Decodes the QuickLZ 1.5.0 level-1 payload format.</summary>
public static class QuickLzDecompressor {
  private const int HashSize = 4096;
  private const int HashMask = HashSize - 1;
  private const int MinMatch = 3;
  private const int TailLiteralCount = 10;
  private const uint ControlSentinel = 1u << 31;

  /// <summary>Decompresses a QuickLZ level-1 payload to exactly <paramref name="originalLength"/> bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
    if (originalLength == 0) {
      if (!data.IsEmpty)
        throw new InvalidDataException("QuickLZ empty payload has trailing compressed bytes.");
      return [];
    }

    var output = new byte[originalLength];
    var hashTable = new int[HashSize];
    hashTable.AsSpan().Fill(-1);
    var nextHashed = 0;
    var sourceOffset = 0;
    var outputOffset = 0;
    uint control = 1;

    while (outputOffset < output.Length) {
      if (control == 1) {
        if (sourceOffset + 4 > data.Length)
          throw new InvalidDataException("QuickLZ payload is truncated before its next control word.");
        control = BinaryPrimitives.ReadUInt32LittleEndian(data[sourceOffset..]);
        sourceOffset += 4;
        if ((control & ControlSentinel) == 0)
          throw new InvalidDataException("QuickLZ control word is missing its bit-31 sentinel.");
      }

      if ((control & 1) != 0) {
        control >>= 1;
        if (sourceOffset + 2 > data.Length)
          throw new InvalidDataException("QuickLZ payload is truncated inside a reference token.");

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
        var hash = (value >> 4) & HashMask;
        var lengthCode = value & 0x0F;
        int matchLength;
        if (lengthCode != 0) {
          matchLength = lengthCode + 2;
          sourceOffset += 2;
        } else {
          if (sourceOffset + 3 > data.Length)
            throw new InvalidDataException("QuickLZ payload is truncated inside a long reference token.");
          matchLength = data[sourceOffset + 2];
          sourceOffset += 3;
          if (matchLength < MinMatch)
            throw new InvalidDataException($"QuickLZ long reference declares invalid length {matchLength}.");
        }

        var candidate = hashTable[hash];
        if (candidate < 0 || candidate >= outputOffset)
          throw new InvalidDataException($"QuickLZ reference uses undefined hash slot {hash}.");
        if (outputOffset + matchLength > output.Length)
          throw new InvalidDataException("QuickLZ reference expands beyond the declared output length.");

        var phraseStart = outputOffset;
        for (var index = 0; index < matchLength; ++index)
          output[outputOffset++] = output[candidate + index];

        UpdateHashes(output, hashTable, ref nextHashed, phraseStart + 1, outputOffset);
        nextHashed = outputOffset;
        continue;
      }

      control >>= 1;
      if (sourceOffset >= data.Length)
        throw new InvalidDataException("QuickLZ payload is truncated inside a literal token.");

      var wasTail = outputOffset >= output.Length - TailLiteralCount;
      output[outputOffset++] = data[sourceOffset++];
      if (!wasTail)
        UpdateHashes(output, hashTable, ref nextHashed, outputOffset - 2, outputOffset);
    }

    if (sourceOffset != data.Length)
      throw new InvalidDataException($"QuickLZ payload has {data.Length - sourceOffset} trailing byte(s) after the declared output.");
    return output;
  }

  private static void UpdateHashes(ReadOnlySpan<byte> output, int[] hashTable, ref int nextHashed,
      int endExclusive, int materializedLength) {
    var maximumEnd = Math.Min(endExclusive, materializedLength - 2);
    while (nextHashed < maximumEnd) {
      hashTable[Hash(output, nextHashed)] = nextHashed;
      ++nextHashed;
    }
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (value ^ (value >> 12)) & HashMask;
  }
}
