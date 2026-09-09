using System.Buffers.Binary;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>Decodes QuickLZ 1.5.0 level-1 and level-3 payloads.</summary>
public static class QuickLzDecompressor {
  private const int HashSize = 4096;
  private const int HashMask = HashSize - 1;
  private const int MinMatch = 3;
  private const int TailLiteralCount = 10;
  private const uint ControlSentinel = 1u << 31;

  /// <summary>Decompresses a QuickLZ level-1 payload to exactly <paramref name="originalLength"/> bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) =>
    Decompress(data, originalLength, QuickLzCompressionLevel.Level1);

  /// <summary>Decompresses a QuickLZ payload at the selected compression level.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength, QuickLzCompressionLevel level) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
    return level switch {
      QuickLzCompressionLevel.Level1 => DecompressLevel1(data, originalLength),
      QuickLzCompressionLevel.Level3 => DecompressLevel3(data, originalLength),
      _ => throw new ArgumentOutOfRangeException(nameof(level), level, "QuickLZ supports compression levels 1 and 3."),
    };
  }

  private static byte[] DecompressLevel1(ReadOnlySpan<byte> data, int originalLength) {
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
        control = ReadControlWord(data, ref sourceOffset);
      }

      if ((control & 1) != 0) {
        control >>= 1;
        if (sourceOffset + 2 > data.Length)
          throw new InvalidDataException("QuickLZ payload is truncated inside a level-1 reference token.");

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
        var hash = (value >> 4) & HashMask;
        var lengthCode = value & 0x0F;
        int matchLength;
        if (lengthCode != 0) {
          matchLength = lengthCode + 2;
          sourceOffset += 2;
        } else {
          if (sourceOffset + 3 > data.Length)
            throw new InvalidDataException("QuickLZ payload is truncated inside a long level-1 reference token.");
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

        UpdateLevel1Hashes(output, hashTable, ref nextHashed, phraseStart + 1, outputOffset);
        nextHashed = outputOffset;
        continue;
      }

      control >>= 1;
      if (sourceOffset >= data.Length)
        throw new InvalidDataException("QuickLZ payload is truncated inside a literal token.");

      var wasTail = outputOffset >= output.Length - TailLiteralCount;
      output[outputOffset++] = data[sourceOffset++];
      if (!wasTail)
        UpdateLevel1Hashes(output, hashTable, ref nextHashed, outputOffset - 2, outputOffset);
    }

    EnsureFullyConsumed(data, sourceOffset);
    return output;
  }

  private static byte[] DecompressLevel3(ReadOnlySpan<byte> data, int originalLength) {
    if (originalLength == 0) {
      if (!data.IsEmpty)
        throw new InvalidDataException("QuickLZ empty payload has trailing compressed bytes.");
      return [];
    }

    var output = new byte[originalLength];
    var sourceOffset = 0;
    var outputOffset = 0;
    uint control = 1;

    while (outputOffset < output.Length) {
      if (control == 1)
        control = ReadControlWord(data, ref sourceOffset);

      if ((control & 1) != 0) {
        control >>= 1;
        ReadLevel3Reference(data, ref sourceOffset, out var distance, out var matchLength);

        if (distance <= 0 || distance > outputOffset)
          throw new InvalidDataException($"QuickLZ level-3 reference uses invalid distance {distance} at output offset {outputOffset}.");
        if (outputOffset + matchLength > output.Length)
          throw new InvalidDataException("QuickLZ level-3 reference expands beyond the declared output length.");

        var candidate = outputOffset - distance;
        for (var index = 0; index < matchLength; ++index)
          output[outputOffset++] = output[candidate + index];
        continue;
      }

      control >>= 1;
      if (sourceOffset >= data.Length)
        throw new InvalidDataException("QuickLZ payload is truncated inside a literal token.");
      output[outputOffset++] = data[sourceOffset++];
    }

    EnsureFullyConsumed(data, sourceOffset);
    return output;
  }

  private static void ReadLevel3Reference(ReadOnlySpan<byte> data, ref int sourceOffset,
      out int distance, out int matchLength) {
    if (sourceOffset >= data.Length)
      throw new InvalidDataException("QuickLZ payload is truncated inside a level-3 reference token.");

    var first = data[sourceOffset];
    switch (first & 0x03) {
      case 0:
        distance = first >> 2;
        matchLength = MinMatch;
        ++sourceOffset;
        return;

      case 1: {
        EnsureTokenBytes(data, sourceOffset, 2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
        distance = value >> 2;
        matchLength = MinMatch;
        sourceOffset += 2;
        return;
      }

      case 2: {
        EnsureTokenBytes(data, sourceOffset, 2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
        distance = value >> 6;
        matchLength = MinMatch + ((value >> 2) & 0x0F);
        sourceOffset += 2;
        return;
      }
    }

    if ((first & 0x7F) == 0x03) {
      EnsureTokenBytes(data, sourceOffset, 4);
      var value = BinaryPrimitives.ReadUInt32LittleEndian(data[sourceOffset..]);
      distance = checked((int)(value >> 15));
      matchLength = MinMatch + checked((int)((value >> 7) & 0xFF));
      sourceOffset += 4;
      return;
    }

    EnsureTokenBytes(data, sourceOffset, 3);
    var threeByteValue = (uint)(data[sourceOffset] |
      data[sourceOffset + 1] << 8 |
      data[sourceOffset + 2] << 16);
    distance = checked((int)(threeByteValue >> 7));
    matchLength = 2 + checked((int)((threeByteValue >> 2) & 0x1F));
    sourceOffset += 3;
  }

  private static uint ReadControlWord(ReadOnlySpan<byte> data, ref int sourceOffset) {
    if (sourceOffset + 4 > data.Length)
      throw new InvalidDataException("QuickLZ payload is truncated before its next control word.");
    var control = BinaryPrimitives.ReadUInt32LittleEndian(data[sourceOffset..]);
    sourceOffset += 4;
    if ((control & ControlSentinel) == 0)
      throw new InvalidDataException("QuickLZ control word is missing its bit-31 sentinel.");
    return control;
  }

  private static void EnsureTokenBytes(ReadOnlySpan<byte> data, int sourceOffset, int count) {
    if (sourceOffset > data.Length - count)
      throw new InvalidDataException("QuickLZ payload is truncated inside a level-3 reference token.");
  }

  private static void EnsureFullyConsumed(ReadOnlySpan<byte> data, int sourceOffset) {
    if (sourceOffset != data.Length)
      throw new InvalidDataException($"QuickLZ payload has {data.Length - sourceOffset} trailing byte(s) after the declared output.");
  }

  private static void UpdateLevel1Hashes(ReadOnlySpan<byte> output, int[] hashTable, ref int nextHashed,
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
