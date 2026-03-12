using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Compression.Core.Checksums;

/// <summary>
/// BLAKE2b cryptographic hash function (RFC 7693).
/// Produces digests of 1–64 bytes (default 32). Operates on 64-bit words
/// and is optimised for 64-bit platforms.
/// Provides both batch (<see cref="Compute"/>) and incremental
/// (<see cref="Update"/>/<see cref="Finish"/>) modes.
/// </summary>
public sealed class Blake2b {
  /// <summary>Default hash size in bytes (256 bits).</summary>
  public const int DefaultHashSize = 32;

  /// <summary>Maximum hash size in bytes (512 bits).</summary>
  public const int MaxHashSize = 64;

  private const int BlockSize = 128;

  // IV: first 64 bits of the fractional parts of the square roots of the first 8 primes.
  private static readonly ulong[] Iv = [
    0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL,
    0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
    0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL,
    0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL
  ];

  // Sigma permutation table (10 rounds × 16 entries).
  private static readonly byte[,] Sigma = {
    { 0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15 },
    { 14, 10,  4,  8,  9, 15, 13,  6,  1, 12,  0,  2, 11,  7,  5,  3 },
    { 11,  8, 12,  0,  5,  2, 15, 13, 10, 14,  3,  6,  7,  1,  9,  4 },
    {  7,  9,  3,  1, 13, 12, 11, 14,  2,  6,  5, 10,  4,  0, 15,  8 },
    {  9,  0,  5,  7,  2,  4, 10, 15, 14,  1, 11, 12,  6,  8,  3, 13 },
    {  2, 12,  6, 10,  0, 11,  8,  3,  4, 13,  7,  5, 15, 14,  1,  9 },
    { 12,  5,  1, 15, 14, 13,  4, 10,  0,  7,  6,  3,  9,  2,  8, 11 },
    { 13, 11,  7, 14, 12,  1,  3,  9,  5,  0, 15,  4,  8,  6,  2, 10 },
    {  6, 15, 14,  9, 11,  3,  0,  8, 12,  2, 13,  7,  1,  4, 10,  5 },
    { 10,  2,  8,  4,  7,  6,  1,  5, 15, 11,  9, 14,  3, 12, 13,  0 }
  };

  private readonly ulong[] _h = new ulong[8];
  private readonly byte[] _buffer = new byte[Blake2b.BlockSize];
  private int _bufferLength;
  private ulong _totalLen;
  private readonly int _hashSize;

  /// <summary>
  /// Initializes a new <see cref="Blake2b"/> instance.
  /// </summary>
  /// <param name="hashSize">Desired hash output size in bytes (1–64). Defaults to 32.</param>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="hashSize"/> is outside [1, 64].
  /// </exception>
  public Blake2b(int hashSize = Blake2b.DefaultHashSize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(hashSize, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(hashSize, Blake2b.MaxHashSize);
    this._hashSize = hashSize;
    this.Reset();
  }

  /// <summary>
  /// Resets the hash state for reuse.
  /// </summary>
  public void Reset() {
    Blake2b.Iv.AsSpan().CopyTo(this._h);
    // XOR the first IV word with parameters: fan-out=1, depth=1, hash size
    this._h[0] ^= 0x01010000UL | (uint)this._hashSize;
    this._bufferLength = 0;
    this._totalLen = 0;
  }

  /// <summary>
  /// Feeds data into the hash.
  /// </summary>
  /// <param name="data">The bytes to hash.</param>
  public void Update(ReadOnlySpan<byte> data) {
    var offset = 0;
    var remaining = data.Length;

    // If buffer has data and we can fill a block, process it
    if (this._bufferLength > 0 && this._bufferLength + remaining > Blake2b.BlockSize) {
      var fill = Blake2b.BlockSize - this._bufferLength;
      data.Slice(offset, fill).CopyTo(this._buffer.AsSpan(this._bufferLength));
      this._totalLen += (ulong)fill;
      this.Compress(this._buffer, false);
      this._bufferLength = 0;
      offset += fill;
      remaining -= fill;
    }

    // Process full blocks directly from the input
    while (remaining > Blake2b.BlockSize) {
      this._totalLen += Blake2b.BlockSize;
      this.CompressSpan(data.Slice(offset, Blake2b.BlockSize), false);
      offset += Blake2b.BlockSize;
      remaining -= Blake2b.BlockSize;
    }

    if (remaining <= 0)
      return;

    // Buffer the remainder
    data.Slice(offset, remaining).CopyTo(this._buffer.AsSpan(this._bufferLength));
    this._bufferLength += remaining;
    this._totalLen += (ulong)remaining;
  }

  /// <summary>
  /// Finalises the hash and returns the digest.
  /// </summary>
  /// <returns>The BLAKE2b hash of the accumulated data.</returns>
  public byte[] Finish() {
    // Pad the remaining buffer with zeros
    this._buffer.AsSpan(this._bufferLength).Clear();
    this.Compress(this._buffer, true);

    var hash = new byte[this._hashSize];
    for (var i = 0; i < this._hashSize; i++)
      hash[i] = (byte)(this._h[i / 8] >> (8 * (i % 8)));

    return hash;
  }

  /// <summary>
  /// Computes a BLAKE2b hash over the given data in one shot.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <param name="hashSize">Desired hash output size in bytes (1–64). Defaults to 32.</param>
  /// <returns>The BLAKE2b hash.</returns>
  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSize = Blake2b.DefaultHashSize) {
    var hasher = new Blake2b(hashSize);
    hasher.Update(data);
    return hasher.Finish();
  }

  // ---- Compression function ----

  private void Compress(byte[] block, bool lastBlock) => this.CompressSpan(block.AsSpan(0, Blake2b.BlockSize), lastBlock);

  private void CompressSpan(ReadOnlySpan<byte> block, bool lastBlock) {
    // Initialise working vector v[0..15]
    Span<ulong> v = stackalloc ulong[16];
    this._h.AsSpan().CopyTo(v);
    Blake2b.Iv.AsSpan().CopyTo(v[8..]);

    v[12] ^= this._totalLen;         // low 64 bits of counter
    // v[13] ^= 0;                   // high 64 bits (not needed for < 2^64 bytes)
    if (lastBlock)
      v[14] = ~v[14];                // invert all bits to signal last block

    // Read message words
    Span<ulong> m = stackalloc ulong[16];
    for (var i = 0; i < 16; i++)
      m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);

    // 12 rounds of mixing
    for (var round = 0; round < 12; round++) {
      var s = round % 10;
      G(ref v[0], ref v[4], ref v[8],  ref v[12], m[Blake2b.Sigma[s, 0]], m[Blake2b.Sigma[s, 1]]);
      G(ref v[1], ref v[5], ref v[9],  ref v[13], m[Blake2b.Sigma[s, 2]], m[Blake2b.Sigma[s, 3]]);
      G(ref v[2], ref v[6], ref v[10], ref v[14], m[Blake2b.Sigma[s, 4]], m[Blake2b.Sigma[s, 5]]);
      G(ref v[3], ref v[7], ref v[11], ref v[15], m[Blake2b.Sigma[s, 6]], m[Blake2b.Sigma[s, 7]]);
      G(ref v[0], ref v[5], ref v[10], ref v[15], m[Blake2b.Sigma[s, 8]], m[Blake2b.Sigma[s, 9]]);
      G(ref v[1], ref v[6], ref v[11], ref v[12], m[Blake2b.Sigma[s, 10]], m[Blake2b.Sigma[s, 11]]);
      G(ref v[2], ref v[7], ref v[8],  ref v[13], m[Blake2b.Sigma[s, 12]], m[Blake2b.Sigma[s, 13]]);
      G(ref v[3], ref v[4], ref v[9],  ref v[14], m[Blake2b.Sigma[s, 14]], m[Blake2b.Sigma[s, 15]]);
    }

    // Finalise: h[i] ^= v[i] ^ v[i+8]
    for (var i = 0; i < 8; i++)
      this._h[i] ^= v[i] ^ v[i + 8];
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void G(ref ulong a, ref ulong b, ref ulong c, ref ulong d, ulong x, ulong y) {
    a = a + b + x;
    d = ulong.RotateRight(d ^ a, 32);
    c += d;
    b = ulong.RotateRight(b ^ c, 24);
    a = a + b + y;
    d = ulong.RotateRight(d ^ a, 16);
    c += d;
    b = ulong.RotateRight(b ^ c, 63);
  }
}
