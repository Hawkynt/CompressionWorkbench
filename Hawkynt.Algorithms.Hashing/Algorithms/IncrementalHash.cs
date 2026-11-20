using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// A block hash fed in pieces whose running state can be copied: what HMAC needs to hash its key
/// pads once and start every message from that point (<see cref="Hmac"/>, <see cref="Pbkdf2"/>).
/// </summary>
public interface IBlockHasher {
  /// <summary>The block size in bytes (the HMAC key block).</summary>
  int BlockSize { get; }

  /// <summary>The digest size in bytes.</summary>
  int HashSize { get; }

  /// <summary>Feeds more data.</summary>
  void Append(ReadOnlySpan<byte> data);

  /// <summary>Writes the digest of everything appended to <paramref name="digest"/>; the state must then be reset or copied over.</summary>
  void Finish(Span<byte> digest);

  /// <summary>Makes this hasher continue exactly where <paramref name="other"/> (the same kind) stands.</summary>
  void CopyFrom(IBlockHasher other);

  /// <summary>A new hasher of the same kind in its initial state.</summary>
  IBlockHasher CreateEmpty();
}

/// <summary>Whirlpool (ISO/IEC 10118-3) fed in pieces.</summary>
public sealed class WhirlpoolHasher : IBlockHasher {
  private readonly ulong[] _state = new ulong[8];
  private readonly byte[] _buffer = new byte[64];
  private int _buffered;
  private long _length;

  /// <inheritdoc />
  public int BlockSize => 64;

  /// <inheritdoc />
  public int HashSize => 64;

  /// <inheritdoc />
  public void Append(ReadOnlySpan<byte> data) {
    this._length += data.Length;
    if (this._buffered > 0) {
      var take = Math.Min(64 - this._buffered, data.Length);
      data[..take].CopyTo(this._buffer.AsSpan(this._buffered));
      this._buffered += take;
      data = data[take..];
      if (this._buffered < 64) return;
      Whirlpool.Compress(this._state, this._buffer);
      this._buffered = 0;
    }
    while (data.Length >= 64) {
      Whirlpool.Compress(this._state, data[..64]);
      data = data[64..];
    }
    data.CopyTo(this._buffer);
    this._buffered = data.Length;
  }

  /// <inheritdoc />
  public void Finish(Span<byte> digest) {
    // 0x80, zeros up to 32 mod 64, then the 256-bit big-endian bit length
    var b = this._buffer;
    b[this._buffered] = 0x80;
    b.AsSpan(this._buffered + 1).Clear();
    if (this._buffered + 1 > 32) {
      Whirlpool.Compress(this._state, b);
      b.AsSpan().Clear();
    }
    BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(56), checked((ulong)this._length * 8));
    Whirlpool.Compress(this._state, b);
    for (var i = 0; i < 8; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(digest.Slice(i * 8, 8), this._state[i]);
  }

  /// <inheritdoc />
  public void CopyFrom(IBlockHasher other) {
    var o = (WhirlpoolHasher)other;
    o._state.CopyTo(this._state, 0);
    o._buffer.AsSpan(0, o._buffered).CopyTo(this._buffer);
    this._buffered = o._buffered;
    this._length = o._length;
  }

  /// <inheritdoc />
  public IBlockHasher CreateEmpty() => new WhirlpoolHasher();
}

/// <summary>A Keccak sponge (SHA-3, Keccak) fed in pieces.</summary>
public sealed class KeccakHasher : IBlockHasher {
  private readonly ulong[] _state = new ulong[25];
  private readonly byte[] _buffer;
  private readonly int _rate;
  private readonly byte _suffix;
  private readonly int _output;
  private int _buffered;

  private KeccakHasher(int rate, byte suffix, int output) {
    this._rate = rate;
    this._suffix = suffix;
    this._output = output;
    this._buffer = new byte[rate];
  }

  /// <summary>SHA3-512 (FIPS 202): rate 72 bytes, 64-byte digest.</summary>
  public static KeccakHasher Sha3_512() => new(72, 0x06, 64);

  /// <summary>SHA3-384 (FIPS 202): rate 104 bytes, 48-byte digest.</summary>
  public static KeccakHasher Sha3_384() => new(104, 0x06, 48);

  /// <summary>SHA3-256 (FIPS 202): rate 136 bytes, 32-byte digest.</summary>
  public static KeccakHasher Sha3_256() => new(136, 0x06, 32);

  /// <summary>The rate, which HMAC takes as the block size.</summary>
  public int BlockSize => this._rate;

  /// <inheritdoc />
  public int HashSize => this._output;

  /// <inheritdoc />
  public void Append(ReadOnlySpan<byte> data) {
    while (data.Length > 0) {
      var take = Math.Min(this._rate - this._buffered, data.Length);
      data[..take].CopyTo(this._buffer.AsSpan(this._buffered));
      this._buffered += take;
      data = data[take..];
      if (this._buffered == this._rate) {
        this.Absorb();
        this._buffered = 0;
      }
    }
  }

  private void Absorb() {
    for (var i = 0; i < this._rate / 8; ++i)
      this._state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(this._buffer.AsSpan(i * 8));
    Keccak.Permute(this._state);
  }

  /// <inheritdoc />
  public void Finish(Span<byte> digest) {
    this._buffer.AsSpan(this._buffered).Clear();
    this._buffer[this._buffered] ^= this._suffix;
    this._buffer[this._rate - 1] ^= 0x80;
    this.Absorb();
    for (var written = 0; written < this._output;) {
      var take = Math.Min(this._rate, this._output - written);
      for (var i = 0; i < take; ++i) digest[written + i] = (byte)(this._state[i >> 3] >> ((i & 7) * 8));
      written += take;
      if (written < this._output) Keccak.Permute(this._state);
    }
  }

  /// <inheritdoc />
  public void CopyFrom(IBlockHasher other) {
    var o = (KeccakHasher)other;
    o._state.CopyTo(this._state, 0);
    o._buffer.AsSpan(0, o._buffered).CopyTo(this._buffer);
    this._buffered = o._buffered;
  }

  /// <inheritdoc />
  public IBlockHasher CreateEmpty() => new KeccakHasher(this._rate, this._suffix, this._output);
}

/// <summary>
/// HMAC (RFC 2104) over a <see cref="IBlockHasher"/>, with the key pads hashed once: every message
/// starts from copies of the two padded states.
/// </summary>
public sealed class Hmac {
  private readonly IBlockHasher _inner;
  private readonly IBlockHasher _outer;
  private readonly IBlockHasher _work;
  private readonly byte[] _innerDigest;

  /// <summary>Keys an HMAC over hashers like <paramref name="hasher"/> (its state is not used).</summary>
  public Hmac(IBlockHasher hasher, ReadOnlySpan<byte> key) {
    ArgumentNullException.ThrowIfNull(hasher);
    var block = hasher.BlockSize;
    Span<byte> k = stackalloc byte[Math.Max(block, hasher.HashSize)];
    k.Clear();
    if (key.Length > block) {
      var h = hasher.CreateEmpty();
      h.Append(key);
      h.Finish(k);
    } else key.CopyTo(k);

    Span<byte> pad = stackalloc byte[block];
    this._inner = hasher.CreateEmpty();
    for (var i = 0; i < block; ++i) pad[i] = (byte)(k[i] ^ 0x36);
    this._inner.Append(pad);
    this._outer = hasher.CreateEmpty();
    for (var i = 0; i < block; ++i) pad[i] = (byte)(k[i] ^ 0x5C);
    this._outer.Append(pad);
    this._work = hasher.CreateEmpty();
    this._innerDigest = new byte[hasher.HashSize];
  }

  /// <summary>The MAC size in bytes.</summary>
  public int HashSize => this._innerDigest.Length;

  /// <summary>Writes the MAC of <paramref name="message"/> to <paramref name="mac"/>.</summary>
  public void Compute(ReadOnlySpan<byte> message, Span<byte> mac) {
    this._work.CopyFrom(this._inner);
    this._work.Append(message);
    this._work.Finish(this._innerDigest);
    this._work.CopyFrom(this._outer);
    this._work.Append(this._innerDigest);
    this._work.Finish(mac);
  }
}

/// <summary>PBKDF2 (RFC 8018, section 5.2) over <see cref="Hmac"/>.</summary>
public static class Pbkdf2 {
  /// <summary>Derives <paramref name="length"/> bytes from a password and salt.</summary>
  /// <param name="hasher">A hasher of the underlying hash (its state is not used).</param>
  /// <param name="password">The password bytes.</param>
  /// <param name="salt">The salt.</param>
  /// <param name="iterations">The iteration count (at least 1).</param>
  /// <param name="length">The number of bytes to derive (at least 1).</param>
  public static byte[] Derive(IBlockHasher hasher, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int length) {
    ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
    var hmac = new Hmac(hasher, password);
    var size = hmac.HashSize;
    var first = new byte[salt.Length + 4];
    salt.CopyTo(first);
    var u = new byte[size];
    var t = new byte[size];
    var result = new byte[length];
    for (int block = 1, offset = 0; offset < length; ++block) {
      BinaryPrimitives.WriteInt32BigEndian(first.AsSpan(salt.Length), block);
      hmac.Compute(first, u);
      u.CopyTo(t, 0);
      for (var n = 1; n < iterations; ++n) {
        hmac.Compute(u, u);
        for (var i = 0; i < size; ++i) t[i] ^= u[i];
      }
      var take = Math.Min(size, length - offset);
      t.AsSpan(0, take).CopyTo(result.AsSpan(offset));
      offset += take;
    }
    return result;
  }
}
