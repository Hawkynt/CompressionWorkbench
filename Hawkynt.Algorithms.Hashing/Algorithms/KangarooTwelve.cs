using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>KangarooTwelve extendable-output function using Keccak-p[1600,12].</summary>
public static class KangarooTwelve {
  private const int ChunkBytes = 8192;
  private const int ChainingBytes = 32;
  private static readonly byte[] FirstMarker = [3,0,0,0,0,0,0,0];
  private static readonly byte[] FinalMarker = [0xFF,0xFF,0x06];

  /// <summary>
  /// Computes the Kangaroo Twelve hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes = 32, ReadOnlySpan<byte> personalization = default) {
    if (outputBytes < 1)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var personalEncoding = RightEncode((ulong)personalization.Length);
    var combined = new byte[checked(data.Length + personalization.Length + personalEncoding.Length)];
    data.CopyTo(combined);
    personalization.CopyTo(combined.AsSpan(data.Length));
    personalEncoding.CopyTo(combined.AsSpan(data.Length + personalization.Length));

    var tree = new KeccakP12Sponge();
    if (combined.Length <= ChunkBytes) {
      tree.Absorb(combined);
      tree.Absorb([0x07]);
      tree.FinalizeAbsorb();
      return tree.Squeeze(outputBytes);
    }

    tree.Absorb(combined.AsSpan(0, ChunkBytes));
    tree.Absorb(FirstMarker);

    var nodeCount = 0UL;
    var offset = ChunkBytes;
    while (offset < combined.Length) {
      var take = Math.Min(ChunkBytes, combined.Length - offset);
      var leaf = new KeccakP12Sponge();
      leaf.Absorb(combined.AsSpan(offset, take));
      leaf.Absorb([0x0B]);
      leaf.FinalizeAbsorb();
      tree.Absorb(leaf.Squeeze(ChainingBytes));
      ++nodeCount;
      offset += take;
    }

    tree.Absorb(RightEncode(nodeCount));
    tree.Absorb(FinalMarker);
    tree.FinalizeAbsorb();
    return tree.Squeeze(outputBytes);
  }

  private static byte[] RightEncode(ulong value) {
    if (value == 0)
      return [0];
    var count = 0;
    var temp = value;
    while (temp != 0) {
      ++count;
      temp >>= 8;
    }
    var result = new byte[count + 1];
    for (var i = 0; i < count; ++i)
      result[i] = (byte)(value >> (8 * (count - i - 1)));
    result[count] = (byte)count;
    return result;
  }

  private sealed class KeccakP12Sponge {
    private const int RateBytes = 168;
    private readonly ulong[] _state = new ulong[25];
    private readonly byte[] _queue = new byte[RateBytes];
    private int _queued;
    private int _squeezeOffset = RateBytes;
    private bool _finalized;

    /// <summary>
    /// Performs the absorb operation provided by <see cref="KangarooTwelve"/>.
    /// </summary>
    public void Absorb(ReadOnlySpan<byte> data) {
      if (_finalized)
        throw new InvalidOperationException("Cannot absorb after squeezing has started.");

      var offset = 0;
      while (offset < data.Length) {
        var take = Math.Min(RateBytes - _queued, data.Length - offset);
        data.Slice(offset, take).CopyTo(_queue.AsSpan(_queued));
        _queued += take;
        offset += take;
        if (_queued == RateBytes) {
          AbsorbQueue();
          _queued = 0;
          Array.Clear(_queue);
        }
      }
    }

    /// <summary>
    /// Performs the finalize absorb operation provided by <see cref="KangarooTwelve"/>.
    /// </summary>
    public void FinalizeAbsorb() {
      if (_finalized)
        return;
      _queue.AsSpan(_queued).Clear();
      _queue[^1] ^= 0x80;
      AbsorbQueue();
      ExtractQueue();
      _squeezeOffset = 0;
      _finalized = true;
    }

    /// <summary>
    /// Performs the squeeze operation provided by <see cref="KangarooTwelve"/>.
    /// </summary>
    public byte[] Squeeze(int length) {
      if (!_finalized)
        FinalizeAbsorb();
      var result = new byte[length];
      var written = 0;
      while (written < length) {
        if (_squeezeOffset == RateBytes) {
          Permute12(_state);
          ExtractQueue();
          _squeezeOffset = 0;
        }
        var take = Math.Min(RateBytes - _squeezeOffset, length - written);
        _queue.AsSpan(_squeezeOffset, take).CopyTo(result.AsSpan(written));
        _squeezeOffset += take;
        written += take;
      }
      return result;
    }

    private void AbsorbQueue() {
      for (var lane = 0; lane < RateBytes / 8; ++lane)
        _state[lane] ^= BinaryPrimitives.ReadUInt64LittleEndian(_queue.AsSpan(lane * 8, 8));
      Permute12(_state);
    }

    private void ExtractQueue() {
      for (var lane = 0; lane < RateBytes / 8; ++lane)
        BinaryPrimitives.WriteUInt64LittleEndian(_queue.AsSpan(lane * 8, 8), _state[lane]);
    }
  }

  private static readonly ulong[] RoundConstants = [
    0x0000000000000001UL,0x0000000000008082UL,0x800000000000808AUL,0x8000000080008000UL,
    0x000000000000808BUL,0x0000000080000001UL,0x8000000080008081UL,0x8000000000008009UL,
    0x000000000000008AUL,0x0000000000000088UL,0x0000000080008009UL,0x000000008000000AUL,
    0x000000008000808BUL,0x800000000000008BUL,0x8000000000008089UL,0x8000000000008003UL,
    0x8000000000008002UL,0x8000000000000080UL,0x000000000000800AUL,0x800000008000000AUL,
    0x8000000080008081UL,0x8000000000008080UL,0x0000000080000001UL,0x8000000080008008UL
  ];
  private static readonly int[] RotationOffsets = [
    0,1,62,28,27,36,44,6,55,20,3,10,43,25,39,41,45,15,21,8,18,2,61,56,14
  ];

  private static void Permute12(Span<ulong> state) {
    Span<ulong> c = stackalloc ulong[5];
    Span<ulong> d = stackalloc ulong[5];
    Span<ulong> b = stackalloc ulong[25];
    for (var round = 12; round < 24; ++round) {
      for (var x = 0; x < 5; ++x)
        c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
      for (var x = 0; x < 5; ++x)
        d[x] = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] ^= d[x];
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x) {
          var source = x + 5 * y;
          b[y + 5 * ((2 * x + 3 * y) % 5)] = BitOperations.RotateLeft(state[source], RotationOffsets[source]);
        }
      for (var y = 0; y < 5; ++y)
        for (var x = 0; x < 5; ++x)
          state[x + 5 * y] = b[x + 5 * y] ^ (~b[(x + 1) % 5 + 5 * y] & b[(x + 2) % 5 + 5 * y]);
      state[0] ^= RoundConstants[round];
    }
  }
}
