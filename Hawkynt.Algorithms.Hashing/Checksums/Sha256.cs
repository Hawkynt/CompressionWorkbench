using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Compression.Core.Checksums;

/// <summary>
/// FIPS 180-4 SHA-256 cryptographic hash function.
/// Provides both batch (<see cref="Compute"/>) and incremental (<see cref="Update"/>/<see cref="Finish"/>) modes.
/// </summary>
public sealed class Sha256 {
  /// <summary>
  /// The size of the SHA-256 hash output in bytes (32 bytes / 256 bits).
  /// </summary>
  public const int HashSize = 32;

  private const int BlockSize = 64;

  // Initial hash values (first 32 bits of the fractional parts of the square roots of the first 8 primes).
  private const uint H0Init = 0x6A09E667U;
  private const uint H1Init = 0xBB67AE85U;
  private const uint H2Init = 0x3C6EF372U;
  private const uint H3Init = 0xA54FF53AU;
  private const uint H4Init = 0x510E527FU;
  private const uint H5Init = 0x9B05688CU;
  private const uint H6Init = 0x1F83D9ABU;
  private const uint H7Init = 0x5BE0CD19U;

  // Round constants (first 32 bits of the fractional parts of the cube roots of the first 64 primes).
  private static readonly uint[] K =
  [
    0x428A2F98U, 0x71374491U, 0xB5C0FBCFU, 0xE9B5DBA5U,
    0x3956C25BU, 0x59F111F1U, 0x923F82A4U, 0xAB1C5ED5U,
    0xD807AA98U, 0x12835B01U, 0x243185BEU, 0x550C7DC3U,
    0x72BE5D74U, 0x80DEB1FEU, 0x9BDC06A7U, 0xC19BF174U,
    0xE49B69C1U, 0xEFBE4786U, 0x0FC19DC6U, 0x240CA1CCU,
    0x2DE92C6FU, 0x4A7484AAU, 0x5CB0A9DCU, 0x76F988DAU,
    0x983E5152U, 0xA831C66DU, 0xB00327C8U, 0xBF597FC7U,
    0xC6E00BF3U, 0xD5A79147U, 0x06CA6351U, 0x14292967U,
    0x27B70A85U, 0x2E1B2138U, 0x4D2C6DFCU, 0x53380D13U,
    0x650A7354U, 0x766A0ABBU, 0x81C2C92EU, 0x92722C85U,
    0xA2BFE8A1U, 0xA81A664BU, 0xC24B8B70U, 0xC76C51A3U,
    0xD192E819U, 0xD6990624U, 0xF40E3585U, 0x106AA070U,
    0x19A4C116U, 0x1E376C08U, 0x2748774CU, 0x34B0BCB5U,
    0x391C0CB3U, 0x4ED8AA4AU, 0x5B9CCA4FU, 0x682E6FF3U,
    0x748F82EEU, 0x78A5636FU, 0x84C87814U, 0x8CC70208U,
    0x90BEFFFAU, 0xA4506CEBU, 0xBEF9A3F7U, 0xC67178F2U,
  ];

  private uint _h0, _h1, _h2, _h3, _h4, _h5, _h6, _h7;
  private readonly byte[] _buffer = new byte[Sha256.BlockSize];
  private int _bufferLength;
  private ulong _totalLength;
  private bool _finished;

  /// <summary>
  /// Initializes a new <see cref="Sha256"/> instance.
  /// </summary>
  public Sha256() {
    this.Reset();
    this.Hash = [];
  }

  /// <summary>
  /// Gets the computed hash. Available after <see cref="Finish"/> has been called.
  /// Returns an empty array before finalization.
  /// </summary>
  public byte[] Hash { get; private set; }

  /// <summary>
  /// Resets the hasher to its initial state.
  /// </summary>
  public void Reset() {
    this._h0 = Sha256.H0Init;
    this._h1 = Sha256.H1Init;
    this._h2 = Sha256.H2Init;
    this._h3 = Sha256.H3Init;
    this._h4 = Sha256.H4Init;
    this._h5 = Sha256.H5Init;
    this._h6 = Sha256.H6Init;
    this._h7 = Sha256.H7Init;
    this._bufferLength = 0;
    this._totalLength = 0;
    this._finished = false;
    this.Hash = [];
  }

  /// <summary>
  /// Updates the hash with additional data.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <exception cref="InvalidOperationException">Thrown if called after <see cref="Finish"/>.</exception>
  public void Update(ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot update after Finish() has been called. Call Reset() first.");

    this._totalLength += (ulong)data.Length;
    var offset = 0;

    // Fill partial buffer
    if (this._bufferLength > 0) {
      var toCopy = Math.Min(Sha256.BlockSize - this._bufferLength, data.Length);
      data[..toCopy].CopyTo(this._buffer.AsSpan(this._bufferLength));
      this._bufferLength += toCopy;
      offset += toCopy;

      if (this._bufferLength == Sha256.BlockSize) {
        this.ProcessBlock(this._buffer);
        this._bufferLength = 0;
      }
    }

    // Process full blocks
    while (offset + Sha256.BlockSize <= data.Length) {
      this.ProcessBlock(data.Slice(offset, Sha256.BlockSize));
      offset += Sha256.BlockSize;
    }

    // Store remaining bytes
    if (offset >= data.Length)
      return;

    data[offset..].CopyTo(this._buffer);
    this._bufferLength = data.Length - offset;
  }

  /// <summary>
  /// Finalizes the hash computation. After calling this method, the <see cref="Hash"/> property
  /// contains the 32-byte SHA-256 digest.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Compute bit length before padding
    var bitLength = this._totalLength * 8;

    // Append padding byte
    this._buffer[this._bufferLength++] = 0x80;

    // If not enough room for 8-byte length, pad and process
    if (this._bufferLength > 56) {
      this._buffer.AsSpan(this._bufferLength, Sha256.BlockSize - this._bufferLength).Clear();
      this.ProcessBlock(this._buffer);
      this._bufferLength = 0;
    }

    // Pad with zeros up to length field
    this._buffer.AsSpan(this._bufferLength, 56 - this._bufferLength).Clear();

    // Append length in bits as big-endian 64-bit
    BinaryPrimitives.WriteUInt64BigEndian(this._buffer.AsSpan(56), bitLength);
    this.ProcessBlock(this._buffer);

    // Write final hash
    this.Hash = new byte[Sha256.HashSize];
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(0), this._h0);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(4), this._h1);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(8), this._h2);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(12), this._h3);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(16), this._h4);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(20), this._h5);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(24), this._h6);
    BinaryPrimitives.WriteUInt32BigEndian(this.Hash.AsSpan(28), this._h7);
  }

  /// <summary>
  /// Computes the SHA-256 hash of the given data in a single call.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <returns>The 32-byte SHA-256 hash.</returns>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var sha = new Sha256();
    sha.Update(data);
    sha.Finish();
    return sha.Hash;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Ch(uint x, uint y, uint z) => (x & y) ^ (~x & z);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Maj(uint x, uint y, uint z) => (x & y) ^ (x & z) ^ (y & z);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Sigma0(uint x) => BitOperations.RotateRight(x, 2) ^ BitOperations.RotateRight(x, 13) ^ BitOperations.RotateRight(x, 22);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Sigma1(uint x) => BitOperations.RotateRight(x, 6) ^ BitOperations.RotateRight(x, 11) ^ BitOperations.RotateRight(x, 25);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint LittleSigma0(uint x) => BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint LittleSigma1(uint x) => BitOperations.RotateRight(x, 17) ^ BitOperations.RotateRight(x, 19) ^ (x >> 10);

  private void ProcessBlock(ReadOnlySpan<byte> block) {
    // Message schedule
    var w = new uint[64];

    for (var t = 0; t < 16; ++t)
      w[t] = BinaryPrimitives.ReadUInt32BigEndian(block[(t * 4)..]);

    for (var t = 16; t < 64; ++t)
      w[t] = LittleSigma1(w[t - 2]) + w[t - 7] + LittleSigma0(w[t - 15]) + w[t - 16];

    // Initialize working variables
    var a = this._h0;
    var b = this._h1;
    var c = this._h2;
    var d = this._h3;
    var e = this._h4;
    var f = this._h5;
    var g = this._h6;
    var h = this._h7;

    // 64 rounds
    for (var t = 0; t < 64; ++t) {
      var t1 = h + Sigma1(e) + Ch(e, f, g) + Sha256.K[t] + w[t];
      var t2 = Sigma0(a) + Maj(a, b, c);

      h = g;
      g = f;
      f = e;
      e = d + t1;
      d = c;
      c = b;
      b = a;
      a = t1 + t2;
    }

    // Update hash values
    this._h0 += a;
    this._h1 += b;
    this._h2 += c;
    this._h3 += d;
    this._h4 += e;
    this._h5 += f;
    this._h6 += g;
    this._h7 += h;
  }
}
