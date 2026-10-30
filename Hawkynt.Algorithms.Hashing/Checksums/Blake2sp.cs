using System.Buffers.Binary;
using System.Numerics;

namespace Compression.Core.Checksums;

/// <summary>
/// BLAKE2sp: eight BLAKE2s-256 leaves hashing interleaved 64-byte blocks, combined by a BLAKE2s
/// root (the BLAKE2 specification, "BLAKE2sp", parallelism degree 8). RAR 5 stores it as the
/// optional file hash. Incremental: <see cref="Update"/> any number of times, then
/// <see cref="Finish"/>.
/// </summary>
/// <remarks>
/// Written from the BLAKE2 paper (Aumasson, Neves, Wilcox-O'Hearn, Winnerlein, 2013,
/// section 2.10) and RFC 7693 for the BLAKE2s compression function; the IV and the message
/// schedule are copied from RFC 7693.
/// </remarks>
public sealed class Blake2sp {
  private const int BlockBytes = 64;
  private const int Leaves = 8;
  private const int OutBytes = 32;

  private static readonly uint[] Iv = [
    0x6A09E667U, 0xBB67AE85U, 0x3C6EF372U, 0xA54FF53AU,
    0x510E527FU, 0x9B05688CU, 0x1F83D9ABU, 0x5BE0CD19U,
  ];

  private static readonly byte[] Sigma = [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
    11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
    7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
    9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
    2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
    12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
    13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
    6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
    10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
  ];

  /// <summary>One BLAKE2s state with its pending block.</summary>
  private sealed class State {
    public readonly uint[] H = new uint[8];
    public readonly byte[] Buf = new byte[BlockBytes];
    public int BufLen;
    public ulong Counter;
    public bool LastNode;

    public State(uint nodeOffset, byte nodeDepth, bool lastNode) {
      Iv.CopyTo(this.H, 0);
      // Parameter block: digest length 32, key length 0, fanout 8, depth 2, leaf length 0,
      // node offset, node depth, inner length 32.
      this.H[0] ^= OutBytes | (0u << 8) | ((uint)Leaves << 16) | (2u << 24);
      this.H[2] ^= nodeOffset;
      this.H[3] ^= ((uint)nodeDepth << 16) | ((uint)OutBytes << 24);
      this.LastNode = lastNode;
    }

    public void Update(ReadOnlySpan<byte> data) {
      while (data.Length > 0) {
        if (this.BufLen == BlockBytes) {
          this.Counter += BlockBytes;
          Compress(this.H, this.Buf, this.Counter, false, false);
          this.BufLen = 0;
        }
        var take = Math.Min(BlockBytes - this.BufLen, data.Length);
        data[..take].CopyTo(this.Buf.AsSpan(this.BufLen));
        this.BufLen += take;
        data = data[take..];
      }
    }

    public byte[] Final() {
      this.Counter += (ulong)this.BufLen;
      this.Buf.AsSpan(this.BufLen).Clear();
      Compress(this.H, this.Buf, this.Counter, true, this.LastNode);
      var result = new byte[OutBytes];
      for (var i = 0; i < 8; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), this.H[i]);
      return result;
    }
  }

  private readonly State[] _leaves = new State[Leaves];
  private readonly byte[] _stripe = new byte[Leaves * BlockBytes];
  private int _stripeLen;
  private bool _finished;

  /// <summary>Creates a fresh BLAKE2sp state.</summary>
  public Blake2sp() {
    for (var i = 0; i < Leaves; ++i)
      this._leaves[i] = new State((uint)i, 0, i == Leaves - 1);
  }

  /// <summary>Computes BLAKE2sp-256 of <paramref name="data"/>.</summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var h = new Blake2sp();
    h.Update(data);
    return h.Finish();
  }

  /// <summary>Adds bytes to the hash.</summary>
  public void Update(ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("The hash is already finished.");
    while (data.Length > 0) {
      if (this._stripeLen == this._stripe.Length) {
        // A full stripe is only handed to the leaves once more data follows, so the last
        // block each leaf sees is the one it finalises.
        for (var i = 0; i < Leaves; ++i)
          this._leaves[i].Update(this._stripe.AsSpan(i * BlockBytes, BlockBytes));
        this._stripeLen = 0;
      }
      var take = Math.Min(this._stripe.Length - this._stripeLen, data.Length);
      data[..take].CopyTo(this._stripe.AsSpan(this._stripeLen));
      this._stripeLen += take;
      data = data[take..];
    }
  }

  /// <summary>Returns the 32-byte digest.</summary>
  public byte[] Finish() {
    if (this._finished)
      throw new InvalidOperationException("The hash is already finished.");
    this._finished = true;
    var root = new State(0, 1, true);
    for (var i = 0; i < Leaves; ++i) {
      var start = i * BlockBytes;
      if (this._stripeLen > start)
        this._leaves[i].Update(this._stripe.AsSpan(start, Math.Min(BlockBytes, this._stripeLen - start)));
      root.Update(this._leaves[i].Final());
    }
    return root.Final();
  }

  private static void Compress(uint[] h, byte[] block, ulong counter, bool last, bool lastNode) {
    Span<uint> m = stackalloc uint[16];
    Span<uint> v = stackalloc uint[16];
    for (var i = 0; i < 16; ++i)
      m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
    h.AsSpan().CopyTo(v);
    Iv.AsSpan().CopyTo(v[8..]);
    v[12] ^= (uint)counter;
    v[13] ^= (uint)(counter >> 32);
    if (last) v[14] = ~v[14];
    if (lastNode) v[15] = ~v[15];
    for (var r = 0; r < 10; ++r) {
      var s = r * 16;
      G(v, 0, 4, 8, 12, m[Sigma[s + 0]], m[Sigma[s + 1]]);
      G(v, 1, 5, 9, 13, m[Sigma[s + 2]], m[Sigma[s + 3]]);
      G(v, 2, 6, 10, 14, m[Sigma[s + 4]], m[Sigma[s + 5]]);
      G(v, 3, 7, 11, 15, m[Sigma[s + 6]], m[Sigma[s + 7]]);
      G(v, 0, 5, 10, 15, m[Sigma[s + 8]], m[Sigma[s + 9]]);
      G(v, 1, 6, 11, 12, m[Sigma[s + 10]], m[Sigma[s + 11]]);
      G(v, 2, 7, 8, 13, m[Sigma[s + 12]], m[Sigma[s + 13]]);
      G(v, 3, 4, 9, 14, m[Sigma[s + 14]], m[Sigma[s + 15]]);
    }
    for (var i = 0; i < 8; ++i)
      h[i] ^= v[i] ^ v[i + 8];
  }

  private static void G(Span<uint> v, int a, int b, int c, int d, uint x, uint y) {
    v[a] = unchecked(v[a] + v[b] + x);
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
    v[a] = unchecked(v[a] + v[b] + y);
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
  }
}
