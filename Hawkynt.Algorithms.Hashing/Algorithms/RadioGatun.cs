using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>RadioGatún[32] belt-and-mill hash function.</summary>
public static class RadioGatun32 {
  /// <summary>
  /// Computes the Radio Gatun-32 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes = 32) {
    if (outputBytes < 0)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var mill = new uint[42];
    var belt = new uint[42];
    var temp = new uint[45];
    var input = 0;

    while (true) {
      for (var c = 0; c < 3; ++c) {
        for (var j = 0; j < 4; ++j) {
          var ended = input >= data.Length;
          var value = ended ? (byte)1 : data[input];
          var word = (uint)value << (8 * j);
          belt[c * 13] ^= word;
          mill[c + 16] ^= word;

          if (ended) {
            for (var i = 0; i < 18; ++i)
              BeltMill(mill, belt, temp);
            return Squeeze(mill, belt, temp, outputBytes);
          }

          ++input;
        }
      }

      BeltMill(mill, belt, temp);
    }
  }

  private static byte[] Squeeze(uint[] mill, uint[] belt, uint[] temp, int outputBytes) {
    var result = new byte[outputBytes];
    var offset = 0;
    var wordIndex = 1;

    while (offset < outputBytes) {
      var word = mill[wordIndex];
      for (var i = 0; i < 4 && offset < outputBytes; ++i)
        result[offset++] = (byte)(word >> (8 * i));

      if (wordIndex == 2) {
        BeltMill(mill, belt, temp);
        wordIndex = 1;
      } else
        wordIndex = 2;
    }

    return result;
  }

  private static void BeltMill(uint[] mill, uint[] belt, uint[] temp) {
    for (var c = 0; c < 12; ++c)
      belt[c + c % 3 * 13] ^= mill[c + 1];

    var rotation = 0;
    for (var c = 0; c < 19; ++c) {
      var i = c * 7 % 19;
      var value = mill[i++] ^ (mill[i % 19] | ~mill[(i + 1) % 19]);
      rotation += c;
      temp[c] = temp[c + 19] = BitOperations.RotateRight(value, rotation & 31);
    }

    for (var i = 39; i > 0; --i) {
      var index = i - 1;
      mill[index] = temp[index] ^ temp[index + 1] ^ temp[index + 4];
      belt[i] = belt[index];
    }

    for (var c = 0; c < 3; ++c) {
      belt[c * 13] = belt[c * 13 + 13];
      mill[c + 13] ^= belt[c * 13];
    }

    mill[0] ^= 1;
  }
}
