using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Compression.Core.Checksums;

/// <summary>
/// FIPS 180-4 SHA-1 cryptographic hash function.
/// Provides both batch (<see cref="Compute"/>) and incremental (<see cref="Update"/>/<see cref="Finish"/>) modes.
/// </summary>
public sealed class Sha1 {
  /// <summary>
  /// The size of the SHA-1 hash output in bytes (20 bytes / 160 bits).
  /// </summary>
  public const int HashSize = 20;

  private const int BlockSize = 64;

  private const uint H0Init = 0x67452301U;
  private const uint H1Init = 0xEFCDAB89U;
  private const uint H2Init = 0x98BADCFEU;
  private const uint H3Init = 0x10325476U;
  private const uint H4Init = 0xC3D2E1F0U;

  private uint _h0, _h1, _h2, _h3, _h4;
  private readonly byte[] _buffer = new byte[Sha1.BlockSize];
  private int _bufferLength;
  private ulong _totalLength;
  private bool _finished;

  /// <summary>
  /// Initializes a new <see cref="Sha1"/> instance.
  /// </summary>
  public Sha1() {
    this.Reset();
    this.Hash = [];
  }

  /// <summary>
  /// Gets the computed hash. Available after <see cref="Finish"/> has been called.
  /// </summary>
  public byte[] Hash { get; private set; }

  /// <summary>
  /// Resets the hasher to its initial state.
  /// </summary>
  public void Reset() {
    this._h0 = Sha1.H0Init;
    this._h1 = Sha1.H1Init;
    this._h2 = Sha1.H2Init;
    this._h3 = Sha1.H3Init;
    this._h4 = Sha1.H4Init;
    this._bufferLength = 0;
    this._totalLength = 0;
    this._finished = false;
    this.Hash = [];
  }

  /// <summary>
  /// Creates a copy of the current hasher state, allowing independent continuation.
  /// </summary>
  public Sha1 Clone() {
    var clone = new Sha1();
    clone._h0 = this._h0;
    clone._h1 = this._h1;
    clone._h2 = this._h2;
    clone._h3 = this._h3;
    clone._h4 = this._h4;
    this._buffer.CopyTo(clone._buffer, 0);
    clone._bufferLength = this._bufferLength;
    clone._totalLength = this._totalLength;
    clone._finished = this._finished;
    clone.Hash = this.Hash;
    return clone;
  }

  /// <summary>
  /// Updates the hash with additional data.
  /// </summary>
  public void Update(ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot update after Finish() has been called. Call Reset() first.");

    this._totalLength += (ulong)data.Length;
    var offset = 0;

    if (this._bufferLength > 0) {
      var toCopy = Math.Min(Sha1.BlockSize - this._bufferLength, data.Length);
      data[..toCopy].CopyTo(this._buffer.AsSpan(this._bufferLength));
      this._bufferLength += toCopy;
      offset += toCopy;

      if (this._bufferLength == Sha1.BlockSize) {
        this.ProcessBlock(this._buffer);
        this._bufferLength = 0;
      }
    }

    while (offset + Sha1.BlockSize <= data.Length) {
      this.ProcessBlock(data.Slice(offset, Sha1.BlockSize));
      offset += Sha1.BlockSize;
    }

    if (offset >= data.Length)
      return;

    data[offset..].CopyTo(this._buffer);
    this._bufferLength = data.Length - offset;
  }

  /// <summary>
  /// Finalizes the hash computation.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;
    var bitLength = this._totalLength * 8;

    this._buffer[this._bufferLength++] = 0x80;

    if (this._bufferLength > 56) {
      this._buffer.AsSpan(this._bufferLength, Sha1.BlockSize - this._bufferLength).Clear();
      this.ProcessBlock(this._buffer);
      this._bufferLength = 0;
    }

    this._buffer.AsSpan(this._bufferLength, 56 - this._bufferLength).Clear();
    BinaryPrimitives.WriteUInt64BigEndian(this._buffer.AsSpan(56), bitLength);
    this.ProcessBlock(this._buffer);

    this.Hash = new byte[Sha1.HashSize];
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(0), this._h0);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(4), this._h1);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(8), this._h2);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(12), this._h3);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(16), this._h4);
  }

  /// <summary>
  /// Computes the SHA-1 hash of the given data in a single call.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var sha = new Sha1();
    sha.Update(data);
    sha.Finish();
    return sha.Hash;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Ch(uint x, uint y, uint z) => (x & y) ^ (~x & z);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Parity(uint x, uint y, uint z) => x ^ y ^ z;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Maj(uint x, uint y, uint z) => (x & y) ^ (x & z) ^ (y & z);

  private void ProcessBlock(ReadOnlySpan<byte> block) {
    var w = new uint[80];

    for (var t = 0; t < 16; ++t)
      w[t] = BinaryPrimitives.ReadUInt32BigEndian(block[(t * 4)..]);

    for (var t = 16; t < 80; ++t)
      w[t] = BitOperations.RotateLeft(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);

    var a = this._h0;
    var b = this._h1;
    var c = this._h2;
    var d = this._h3;
    var e = this._h4;

    for (var t = 0; t < 80; ++t) {
      uint f, k;
      if (t < 20) {
        f = Ch(b, c, d);
        k = 0x5A827999U;
      }
      else if (t < 40) {
        f = Parity(b, c, d);
        k = 0x6ED9EBA1U;
      }
      else if (t < 60) {
        f = Maj(b, c, d);
        k = 0x8F1BBCDCU;
      }
      else {
        f = Parity(b, c, d);
        k = 0xCA62C1D6U;
      }

      var temp = BitOperations.RotateLeft(a, 5) + f + e + k + w[t];
      e = d;
      d = c;
      c = BitOperations.RotateLeft(b, 30);
      b = a;
      a = temp;
    }

    this._h0 += a;
    this._h1 += b;
    this._h2 += c;
    this._h3 += d;
    this._h4 += e;
  }
}
