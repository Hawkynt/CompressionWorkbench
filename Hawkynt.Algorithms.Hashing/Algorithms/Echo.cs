using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

public static class Echo224 { public static byte[] Compute(ReadOnlySpan<byte> data) => EchoCore.Compute(data, 28); }
public static class Echo256 { public static byte[] Compute(ReadOnlySpan<byte> data) => EchoCore.Compute(data, 32); }
public static class Echo384 { public static byte[] Compute(ReadOnlySpan<byte> data) => EchoCore.Compute(data, 48); }
public static class Echo512 { public static byte[] Compute(ReadOnlySpan<byte> data) => EchoCore.Compute(data, 64); }

internal static class EchoCore {
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes is not (28 or 32 or 48 or 64))
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var outputBits = outputBytes * 8;
    var small = outputBits <= 256;
    var stateCells = small ? 4 : 8;
    var blockSize = small ? 192 : 128;
    var rounds = small ? 8 : 10;

    var chaining = new uint[stateCells][];
    for (var i = 0; i < stateCells; ++i)
      chaining[i] = [(uint)outputBits, 0, 0, 0];

    var counter = new uint[4];
    var offset = 0;
    while (offset + blockSize <= data.Length) {
      Increment(counter, (uint)(blockSize * 8));
      Compress(chaining, counter, data.Slice(offset, blockSize), small, rounds);
      offset += blockSize;
    }

    var trailing = data.Length - offset;
    Increment(counter, (uint)(trailing * 8));

    Span<byte> lengthBytes = stackalloc byte[16];
    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes.Slice(i * 4, 4), counter[i]);

    if (trailing == 0)
      Array.Clear(counter);

    var final = new byte[blockSize];
    data[offset..].CopyTo(final);
    final[trailing] = 0x80;

    if (trailing > blockSize - 18) {
      Compress(chaining, counter, final, small, rounds);
      Array.Clear(counter);
      Array.Clear(final);
    }

    BinaryPrimitives.WriteUInt16LittleEndian(final.AsSpan(blockSize - 18, 2), (ushort)outputBits);
    lengthBytes.CopyTo(final.AsSpan(blockSize - 16));
    Compress(chaining, counter, final, small, rounds);

    var result = new byte[outputBytes];
    for (var wordIndex = 0; wordIndex < outputBytes / 4; ++wordIndex) {
      var cell = wordIndex / 4;
      var word = wordIndex & 3;
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(wordIndex * 4, 4), chaining[cell][word]);
    }
    return result;
  }

  private static void Compress(uint[][] chaining, ReadOnlySpan<uint> counter, ReadOnlySpan<byte> block, bool small, int rounds) {
    var stateCells = small ? 4 : 8;
    var matrix = new uint[16][];
    for (var i = 0; i < 16; ++i)
      matrix[i] = new uint[4];

    for (var i = 0; i < stateCells; ++i)
      chaining[i].CopyTo(matrix[i], 0);

    var messageCells = 16 - stateCells;
    for (var cell = 0; cell < messageCells; ++cell) {
      var target = matrix[stateCells + cell];
      var baseOffset = cell * 16;
      for (var word = 0; word < 4; ++word)
        target[word] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(baseOffset + word * 4, 4));
    }

    var key = counter.ToArray();
    Span<byte> keyBytes = stackalloc byte[16];
    Span<byte> zeroKey = stackalloc byte[16];
    zeroKey.Clear();
    var cellBytes = new byte[16];

    for (var round = 0; round < rounds; ++round) {
      for (var cell = 0; cell < 16; ++cell) {
        for (var word = 0; word < 4; ++word) {
          BinaryPrimitives.WriteUInt32LittleEndian(cellBytes.AsSpan(word * 4, 4), matrix[cell][word]);
          BinaryPrimitives.WriteUInt32LittleEndian(keyBytes.Slice(word * 4, 4), key[word]);
        }

        var first = HarakaCore.AesRound(cellBytes, keyBytes);
        var second = HarakaCore.AesRound(first, zeroKey);
        for (var word = 0; word < 4; ++word)
          matrix[cell][word] = BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(word * 4, 4));
        Increment(key, 1);
      }

      ShiftRow(matrix, 1, 5, 9, 13, 1);
      ShiftRow(matrix, 2, 6, 10, 14, 2);
      ShiftRow(matrix, 3, 7, 11, 15, 3);

      MixColumn(matrix, 0, 1, 2, 3);
      MixColumn(matrix, 4, 5, 6, 7);
      MixColumn(matrix, 8, 9, 10, 11);
      MixColumn(matrix, 12, 13, 14, 15);
    }

    if (small) {
      for (var u = 0; u < 16; ++u) {
        var cell = u / 4;
        var word = u & 3;
        var value = chaining[cell][word]
          ^ BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(u * 4, 4))
          ^ BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(64 + u * 4, 4))
          ^ BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(128 + u * 4, 4))
          ^ matrix[cell][word]
          ^ matrix[cell + 4][word]
          ^ matrix[cell + 8][word]
          ^ matrix[cell + 12][word];
        chaining[cell][word] = value;
      }
    } else {
      for (var u = 0; u < 32; ++u) {
        var cell = u / 4;
        var word = u & 3;
        chaining[cell][word] ^= BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(u * 4, 4))
          ^ matrix[cell][word]
          ^ matrix[cell + 8][word];
      }
    }
  }

  private static void ShiftRow(uint[][] matrix, int a, int b, int c, int d, int amount) {
    for (var i = 0; i < amount; ++i) {
      var temp = matrix[a];
      matrix[a] = matrix[b];
      matrix[b] = matrix[c];
      matrix[c] = matrix[d];
      matrix[d] = temp;
    }
  }

  private static void MixColumn(uint[][] matrix, int ia, int ib, int ic, int id) {
    for (var word = 0; word < 4; ++word) {
      var a = matrix[ia][word];
      var b = matrix[ib][word];
      var c = matrix[ic][word];
      var d = matrix[id][word];
      var ab = a ^ b;
      var bc = b ^ c;
      var cd = c ^ d;
      var abx = MultiplyX(ab);
      var bcx = MultiplyX(bc);
      var cdx = MultiplyX(cd);
      matrix[ia][word] = abx ^ bc ^ d;
      matrix[ib][word] = bcx ^ a ^ cd;
      matrix[ic][word] = cdx ^ ab ^ d;
      matrix[id][word] = abx ^ bcx ^ cdx ^ ab ^ c;
    }
  }

  private static uint MultiplyX(uint value) {
    uint result = 0;
    for (var i = 0; i < 4; ++i) {
      var b = (byte)(value >> (i * 8));
      var x = (byte)((b << 1) ^ (((b >> 7) & 1) * 0x1B));
      result |= (uint)x << (i * 8);
    }
    return result;
  }

  private static void Increment(Span<uint> value, uint amount) {
    var old = value[0];
    value[0] = unchecked(old + amount);
    if (value[0] >= old)
      return;
    for (var i = 1; i < 4; ++i) {
      ++value[i];
      if (value[i] != 0)
        break;
    }
  }
}
