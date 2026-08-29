using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>MD6-512 variant emitted by the DarkCrypt Total Commander plugin.</summary>
/// <remarks>
/// This preserves DarkCrypt's pre-April-2009 MD6 finalization behaviour: the digest is the first
/// 512 bits of the final 1024-bit root chaining value. It uses d=512, r=168, L=64 and no key.
/// </remarks>
public static class DarkCryptMd6 {
  private const int WordBits = 64;
  private const int InputWords = 89;
  private const int ChunkWords = 16;
  private const int BlockWords = 64;
  private const int KeyWords = 8;
  private const int QWords = 15;
  private const int MaxStackHeight = 29;
  private const int DefaultL = 64;
  private const int DigestBits = 512;
  private const int Rounds = 168;

  private const int T0 = 17;
  private const int T1 = 18;
  private const int T2 = 21;
  private const int T3 = 31;
  private const int T4 = 67;
  private const int T5 = 89;

  private const ulong S0 = 0x0123456789ABCDEFUL;
  private const ulong SMask = 0x7311C2812425CFA0UL;

  private static readonly (int Right, int Left)[] Shifts = [
    (10,11),(5,24),(13,9),(10,16),(11,15),(12,9),(2,27),(7,15),
    (14,6),(15,2),(7,29),(13,8),(11,15),(7,5),(6,31),(12,9)
  ];

  private static readonly ulong[] Q = [
    0x7311C2812425CFA0UL,0x6432286434AAC8E7UL,0xB60450E9EF68B7C1UL,
    0xE8FB23908D9F06F1UL,0xDD2E76CBA691E5BFUL,0x0CD0D63B2C30BC41UL,
    0x1F8CCF6823058F8AUL,0x54E5ED5B88E3775DUL,0x4AD12AAE0A6D6031UL,
    0x3E7F16BB88222E0DUL,0x8AF8671D3FB50C2CUL,0x995AD1178BD25C31UL,
    0xC878C1DD04C4B633UL,0x3B72066C7A1552ACUL,0x0D6F3522631EFFCBUL
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var state = new State();
    state.Update(data);
    return state.Final();
  }

  private static ulong[] Compress(ReadOnlySpan<ulong> input) {
    var total = Rounds * ChunkWords + InputWords;
    var a = new ulong[total];
    input.CopyTo(a);
    var s = S0;
    var index = InputWords;

    for (var roundBase = 0; roundBase < Rounds * ChunkWords; roundBase += ChunkWords) {
      for (var step = 0; step < ChunkWords; ++step) {
        var (right, left) = Shifts[step];
        var i = index + step;
        var x = s;
        x ^= a[i - T5];
        x ^= a[i - T0];
        x ^= a[i - T1] & a[i - T2];
        x ^= a[i - T3] & a[i - T4];
        x ^= x >> right;
        a[i] = unchecked(x ^ (x << left));
      }
      s = unchecked((s << 1) ^ (s >> 63) ^ (s & SMask));
      index += ChunkWords;
    }

    return a.AsSpan(total - ChunkWords, ChunkWords).ToArray();
  }

  private static ulong[] StandardCompress(
    ReadOnlySpan<ulong> key,
    int level,
    ulong node,
    int z,
    int paddingBits,
    ReadOnlySpan<ulong> block
  ) {
    var input = new ulong[InputWords];
    var index = 0;
    Q.CopyTo(input, index);
    index += QWords;
    key.CopyTo(input.AsSpan(index));
    index += KeyWords;
    input[index++] = ((ulong)level << 56) | node;
    input[index++] = ((ulong)Rounds << 48)
                   | ((ulong)DefaultL << 40)
                   | ((ulong)z << 36)
                   | ((ulong)paddingBits << 20)
                   | DigestBits;
    block.CopyTo(input.AsSpan(index));
    return Compress(input);
  }

  private sealed class State {
    private readonly ulong[] _key = new ulong[KeyWords];
    private readonly int[] _bits = new int[MaxStackHeight];
    private readonly ulong[] _nodes = new ulong[MaxStackHeight];
    private readonly ulong[][] _blocks = new ulong[MaxStackHeight][];
    private readonly byte[] _level1Bytes = new byte[BlockWords * 8];
    private int _top = 1;
    private ulong[]? _hashValue;
    private byte[]? _digest;

    public State() {
      for (var i = 0; i < _blocks.Length; ++i)
        _blocks[i] = new ulong[BlockWords];
    }

    public void Update(ReadOnlySpan<byte> data) {
      var source = 0;
      while (source < data.Length) {
        var bytesFree = BlockWords * 8 - _bits[1] / 8;
        var portion = Math.Min(data.Length - source, bytesFree);
        data.Slice(source, portion).CopyTo(_level1Bytes.AsSpan(_bits[1] / 8));
        source += portion;
        _bits[1] += portion * 8;
        if (_bits[1] == BlockWords * WordBits && source < data.Length)
          Process(1, false);
      }
    }

    public byte[] Final() {
      if (_digest is not null)
        return _digest.ToArray();

      var level = 1;
      if (_top != 1) {
        for (level = 1; level <= _top; ++level)
          if (_bits[level] > 0)
            break;
      }
      Process(level, true);
      if (_hashValue is null)
        throw new InvalidOperationException("MD6 finalization did not produce a root chaining value.");

      var result = new byte[DigestBits / 8];
      for (var word = 0; word < result.Length / 8; ++word)
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(word * 8, 8), _hashValue[word]);
      _digest = result;
      return result.ToArray();
    }

    private ulong[] CompressBlock(int level, int z) {
      Span<ulong> block = stackalloc ulong[BlockWords];
      if (level == 1) {
        for (var word = 0; word < BlockWords; ++word)
          block[word] = BinaryPrimitives.ReadUInt64BigEndian(_level1Bytes.AsSpan(word * 8, 8));
      } else {
        _blocks[level].CopyTo(block);
      }

      var paddingBits = BlockWords * WordBits - _bits[level];
      var chunk = StandardCompress(_key, level, _nodes[level], z, paddingBits, block);
      _bits[level] = 0;
      ++_nodes[level];
      if (level == 1)
        Array.Clear(_level1Bytes);
      else
        Array.Clear(_blocks[level]);
      return chunk;
    }

    private void Process(int level, bool final) {
      if (!final) {
        if (_bits[level] < BlockWords * WordBits)
          return;
      } else if (level == _top) {
        if (level == DefaultL + 1) {
          if (_bits[level] == ChunkWords * WordBits && _nodes[level] > 0)
            return;
        } else if (level > 1 && _bits[level] == ChunkWords * WordBits) {
          return;
        }
      }

      var z = final && level == _top ? 1 : 0;
      var chunk = CompressBlock(level, z);
      if (z == 1) {
        _hashValue = chunk;
        return;
      }

      var nextLevel = Math.Min(level + 1, DefaultL + 1);
      if (nextLevel == DefaultL + 1 && _nodes[nextLevel] == 0 && _bits[nextLevel] == 0)
        _bits[nextLevel] = ChunkWords * WordBits;

      var wordOffset = _bits[nextLevel] / WordBits;
      chunk.CopyTo(_blocks[nextLevel], wordOffset);
      _bits[nextLevel] += ChunkWords * WordBits;
      if (nextLevel > _top)
        _top = nextLevel;

      Process(nextLevel, final);
    }
  }
}
