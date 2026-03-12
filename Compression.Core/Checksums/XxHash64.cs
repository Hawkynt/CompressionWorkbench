using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Compression.Core.Checksums;

/// <summary>
/// xxHash 64-bit non-cryptographic hash function.
/// Provides both batch (<see cref="Compute"/>) and incremental (<see cref="Update"/>/<see cref="Value"/>) modes.
/// </summary>
public sealed class XxHash64 {
  // xxHash64 prime constants from the specification.
  private const ulong Prime1 = 0x9E3779B185EBCA87UL;
  private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
  private const ulong Prime3 = 0x165667B19E3779F9UL;
  private const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
  private const ulong Prime5 = 0x27D4EB2F165667C5UL;

  private readonly ulong _seed;
  private readonly byte[] _buffer = new byte[32];
  private int _bufferUsed;
  private ulong _v1;
  private ulong _v2;
  private ulong _v3;
  private ulong _v4;
  private ulong _totalLength;
  private bool _hasProcessedStripe;

  /// <summary>
  /// Initializes a new <see cref="XxHash64"/> with the specified seed.
  /// </summary>
  /// <param name="seed">The hash seed. Defaults to 0.</param>
  public XxHash64(ulong seed = 0) {
    this._seed = seed;
    this.Reset();
  }

  /// <summary>
  /// Gets the current hash value. This finalizes the accumulated state without modifying it.
  /// </summary>
  public ulong Value => this.FinalizeHash();

  /// <summary>
  /// Resets the hasher to its initial state.
  /// </summary>
  public void Reset() {
    this._v1 = this._seed + XxHash64.Prime1 + XxHash64.Prime2;
    this._v2 = this._seed + XxHash64.Prime2;
    this._v3 = this._seed;
    this._v4 = this._seed - XxHash64.Prime1;
    this._bufferUsed = 0;
    this._totalLength = 0;
    this._hasProcessedStripe = false;
  }

  /// <summary>
  /// Updates the hash with additional data.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  public void Update(ReadOnlySpan<byte> data) {
    this._totalLength += (ulong)data.Length;

    var offset = 0;

    // Fill partial buffer
    if (this._bufferUsed > 0) {
      var toCopy = Math.Min(32 - this._bufferUsed, data.Length);
      data[..toCopy].CopyTo(this._buffer.AsSpan(this._bufferUsed));
      this._bufferUsed += toCopy;
      offset += toCopy;

      if (this._bufferUsed == 32) {
        this.ProcessStripe(this._buffer);
        this._bufferUsed = 0;
        this._hasProcessedStripe = true;
      }
    }

    // Process full 32-byte stripes
    while (offset + 32 <= data.Length) {
      this.ProcessStripe(data.Slice(offset, 32));
      offset += 32;
      this._hasProcessedStripe = true;
    }

    // Store remaining bytes
    if (offset >= data.Length)
      return;

    data[offset..].CopyTo(this._buffer);
    this._bufferUsed = data.Length - offset;
  }

  /// <summary>
  /// Computes the xxHash64 of the given data in a single call.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <param name="seed">The hash seed. Defaults to 0.</param>
  /// <returns>The 64-bit hash value.</returns>
  public static ulong Compute(ReadOnlySpan<byte> data, ulong seed = 0) => data.Length >= 32 ? ComputeLarge(data, seed) : ComputeSmall(data, seed);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static ulong Round(ulong acc, ulong input) {
    acc += input * XxHash64.Prime2;
    acc = BitOperations.RotateLeft(acc, 31);
    acc *= XxHash64.Prime1;
    return acc;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static ulong MergeAccumulator(ulong acc, ulong val) {
    val = Round(0, val);
    acc ^= val;
    acc = acc * XxHash64.Prime1 + XxHash64.Prime4;
    return acc;
  }

  private static ulong Avalanche(ulong hash) {
    hash ^= hash >> 33;
    hash *= XxHash64.Prime2;
    hash ^= hash >> 29;
    hash *= XxHash64.Prime3;
    hash ^= hash >> 32;
    return hash;
  }

  private static ulong ComputeLarge(ReadOnlySpan<byte> data, ulong seed) {
    var v1 = seed + XxHash64.Prime1 + XxHash64.Prime2;
    var v2 = seed + XxHash64.Prime2;
    var v3 = seed;
    var v4 = seed - XxHash64.Prime1;

    var offset = 0;
    while (offset + 32 <= data.Length) {
      v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
      v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]));
      v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 16)..]));
      v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 24)..]));
      offset += 32;
    }

    var hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);

    hash = MergeAccumulator(hash, v1);
    hash = MergeAccumulator(hash, v2);
    hash = MergeAccumulator(hash, v3);
    hash = MergeAccumulator(hash, v4);

    hash += (ulong)data.Length;

    return FinalizeTail(hash, data[offset..]);
  }

  private static ulong ComputeSmall(ReadOnlySpan<byte> data, ulong seed) {
    var hash = seed + XxHash64.Prime5 + (ulong)data.Length;
    return FinalizeTail(hash, data);
  }

  private static ulong FinalizeTail(ulong hash, ReadOnlySpan<byte> remaining) {
    var offset = 0;

    while (offset + 8 <= remaining.Length) {
      var k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(remaining[offset..]));
      hash ^= k1;
      hash = BitOperations.RotateLeft(hash, 27) * XxHash64.Prime1 + XxHash64.Prime4;
      offset += 8;
    }

    if (offset + 4 <= remaining.Length) {
      ulong k1 = BinaryPrimitives.ReadUInt32LittleEndian(remaining[offset..]);
      hash ^= k1 * XxHash64.Prime1;
      hash = BitOperations.RotateLeft(hash, 23) * XxHash64.Prime2 + XxHash64.Prime3;
      offset += 4;
    }

    while (offset < remaining.Length) {
      hash ^= remaining[offset] * XxHash64.Prime5;
      hash = BitOperations.RotateLeft(hash, 11) * XxHash64.Prime1;
      offset++;
    }

    return Avalanche(hash);
  }

  private void ProcessStripe(ReadOnlySpan<byte> stripe) {
    this._v1 = Round(this._v1, BinaryPrimitives.ReadUInt64LittleEndian(stripe));
    this._v2 = Round(this._v2, BinaryPrimitives.ReadUInt64LittleEndian(stripe[8..]));
    this._v3 = Round(this._v3, BinaryPrimitives.ReadUInt64LittleEndian(stripe[16..]));
    this._v4 = Round(this._v4, BinaryPrimitives.ReadUInt64LittleEndian(stripe[24..]));
  }

  private ulong FinalizeHash() {
    ulong hash;

    if (this._hasProcessedStripe) {
      hash = BitOperations.RotateLeft(this._v1, 1) + BitOperations.RotateLeft(this._v2, 7) + BitOperations.RotateLeft(this._v3, 12) + BitOperations.RotateLeft(this._v4, 18);

      hash = MergeAccumulator(hash, this._v1);
      hash = MergeAccumulator(hash, this._v2);
      hash = MergeAccumulator(hash, this._v3);
      hash = MergeAccumulator(hash, this._v4);
    }
    else
      hash = this._seed + XxHash64.Prime5;

    hash += this._totalLength;

    return FinalizeTail(hash, this._buffer.AsSpan(0, this._bufferUsed));
  }
}
