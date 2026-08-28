namespace Hawkynt.Algorithms.Hashing;

/// <summary>PhotonBeetle 256-bit hash from the NIST lightweight-cryptography finalist.</summary>
public static class PhotonBeetleHash {
  private const int StateBytes = 32;
  private const int FirstRate = 16;
  private const int Rate = 4;
  private const int SqueezeRate = 16;

  private static readonly byte[] RoundConstants = [
    1,0,2,6,14,15,13,9,
    3,2,0,4,12,13,15,11,
    7,6,4,0,8,9,11,15,
    14,15,13,9,1,0,2,6,
    13,12,14,10,2,3,1,5,
    11,10,8,12,4,5,7,3,
    6,7,5,1,9,8,10,14,
    12,13,15,11,3,2,0,4,
    9,8,10,14,6,7,5,1,
    2,3,1,5,13,12,14,10,
    5,4,6,2,10,11,9,13,
    10,11,9,13,5,4,6,2
  ];

  private static readonly byte[,] MixColumns = {
    {2,4,2,11,2,8,5,6},
    {12,9,8,13,7,7,5,2},
    {4,4,13,13,9,4,13,9},
    {1,6,5,1,12,13,15,14},
    {15,12,9,13,14,5,14,13},
    {9,14,5,15,4,12,9,6},
    {12,2,2,10,3,1,1,14},
    {15,1,13,10,5,10,2,3}
  };

  private static readonly byte[] SBox = [12,5,6,11,9,0,10,13,3,14,15,8,4,7,1,2];

  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<byte> state = stackalloc byte[StateBytes];
    var offset = 0;
    var phase = 0;

    if (data.Length >= FirstRate) {
      data[..FirstRate].CopyTo(state);
      offset = FirstRate;
      phase = 1;

      while (offset + FirstRate <= data.Length) {
        for (var chunk = 0; chunk < FirstRate; chunk += Rate) {
          Permute(state);
          Xor(state, data.Slice(offset + chunk, Rate));
        }
        offset += FirstRate;
        phase = 2;
      }
    }

    var remaining = data.Length - offset;
    if (phase == 0) {
      if (remaining != 0) {
        data[offset..].CopyTo(state);
        state[remaining] ^= 0x01;
      }
      state[^1] ^= 0x20;
    } else if (phase == 1 && remaining == 0) {
      state[^1] ^= 0x40;
    } else {
      var pos = offset;
      while (remaining >= Rate) {
        Permute(state);
        Xor(state, data.Slice(pos, Rate));
        pos += Rate;
        remaining -= Rate;
      }

      if (remaining != 0) {
        Permute(state);
        Xor(state, data.Slice(pos, remaining));
        state[remaining] ^= 0x01;
        state[^1] ^= 0x40;
      } else {
        state[^1] ^= 0x20;
      }
    }

    var result = new byte[32];
    Permute(state);
    state[..SqueezeRate].CopyTo(result);
    Permute(state);
    state[..SqueezeRate].CopyTo(result.AsSpan(SqueezeRate));
    return result;
  }

  private static void Xor(Span<byte> state, ReadOnlySpan<byte> data) {
    for (var i = 0; i < data.Length; ++i)
      state[i] ^= data[i];
  }

  private static void Permute(Span<byte> state) {
    var cells = new byte[8, 8];
    for (var i = 0; i < 64; ++i)
      cells[i >> 3, i & 7] = (byte)((state[i >> 1] >> (4 * (i & 1))) & 0x0F);

    Span<byte> row = stackalloc byte[8];
    Span<byte> column = stackalloc byte[8];
    for (var round = 0; round < 12; ++round) {
      var rcOffset = round * 8;
      for (var i = 0; i < 8; ++i)
        cells[i, 0] ^= RoundConstants[rcOffset + i];

      for (var i = 0; i < 8; ++i)
        for (var j = 0; j < 8; ++j)
          cells[i, j] = SBox[cells[i, j]];

      for (var i = 1; i < 8; ++i) {
        for (var j = 0; j < 8; ++j)
          row[j] = cells[i, j];
        for (var j = 0; j < 8; ++j)
          cells[i, j] = row[(j + i) & 7];
      }

      for (var j = 0; j < 8; ++j) {
        for (var i = 0; i < 8; ++i) {
          var sum = 0;
          for (var k = 0; k < 8; ++k) {
            var x = MixColumns[i, k];
            var b = cells[k, j];
            sum ^= x * (b & 1);
            sum ^= x * (b & 2);
            sum ^= x * (b & 4);
            sum ^= x * (b & 8);
          }

          var t = sum >> 4;
          sum = (sum & 15) ^ t ^ (t << 1);
          t = sum >> 4;
          sum = (sum & 15) ^ t ^ (t << 1);
          column[i] = (byte)sum;
        }
        for (var i = 0; i < 8; ++i)
          cells[i, j] = column[i];
      }
    }

    for (var i = 0; i < 64; i += 2)
      state[i >> 1] = (byte)(cells[i >> 3, i & 7] | (cells[i >> 3, (i + 1) & 7] << 4));
  }
}
