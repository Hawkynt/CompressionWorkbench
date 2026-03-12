using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Compression.Core.Checksums;

/// <summary>
/// xxHash 32-bit non-cryptographic hash function.
/// Provides both batch (<see cref="Compute"/>) and incremental (<see cref="Update"/>/<see cref="Value"/>) modes.
/// </summary>
public sealed class XxHash32 {
  // xxHash32 prime constants from the specification.
  private const uint Prime1 = 0x9E3779B1U;
  private const uint Prime2 = 0x85EBCA77U;
  private const uint Prime3 = 0xC2B2AE3DU;
  private const uint Prime4 = 0x27D4EB2FU;
  private const uint Prime5 = 0x165667B1U;

  private readonly uint _seed;
  private readonly byte[] _buffer = new byte[16];
  private int _bufferUsed;
  private uint _v1;
  private uint _v2;
  private uint _v3;
  private uint _v4;
  private ulong _totalLength;
  private bool _hasProcessedStripe;

  /// <summary>
  /// Initializes a new <see cref="XxHash32"/> with the specified seed.
  /// </summary>
  /// <param name="seed">The hash seed. Defaults to 0.</param>
  public XxHash32(uint seed = 0) {
    this._seed = seed;
    this.Reset();
  }

  /// <summary>
  /// Gets the current hash value. This finalizes the accumulated state without modifying it.
  /// </summary>
  public uint Value => this.FinalizeHash();

  /// <summary>
  /// Resets the hasher to its initial state.
  /// </summary>
  public void Reset() {
    this._v1 = this._seed + Prime1 + Prime2;
    this._v2 = this._seed + Prime2;
    this._v3 = this._seed;
    this._v4 = this._seed - Prime1;
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
      var toCopy = Math.Min(16 - this._bufferUsed, data.Length);
      data[..toCopy].CopyTo(this._buffer.AsSpan(this._bufferUsed));
      this._bufferUsed += toCopy;
      offset += toCopy;

      if (this._bufferUsed == 16) {
        this.ProcessStripe(this._buffer);
        this._bufferUsed = 0;
        this._hasProcessedStripe = true;
      }
    }

    // Process full 16-byte stripes
    while (offset + 16 <= data.Length) {
      this.ProcessStripe(data.Slice(offset, 16));
      offset += 16;
      this._hasProcessedStripe = true;
    }

    // Store remaining bytes
    if (offset >= data.Length)
      return;

    data[offset..].CopyTo(this._buffer);
    this._bufferUsed = data.Length - offset;
  }

  /// <summary>
  /// Computes the xxHash32 of the given data in a single call.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <param name="seed">The hash seed. Defaults to 0.</param>
  /// <returns>The 32-bit hash value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0) => data.Length >= 16 ? ComputeLarge(data, seed) : ComputeSmall(data, seed);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Round(uint acc, uint input) {
    acc += input * Prime2;
    acc = BitOperations.RotateLeft(acc, 13);
    acc *= Prime1;
    return acc;
  }

  private static uint Avalanche(uint hash) {
    hash ^= hash >> 15;
    hash *= Prime2;
    hash ^= hash >> 13;
    hash *= Prime3;
    hash ^= hash >> 16;
    return hash;
  }

  private static uint ComputeLarge(ReadOnlySpan<byte> data, uint seed) {
    var v1 = seed + Prime1 + Prime2;
    var v2 = seed + Prime2;
    var v3 = seed;
    var v4 = seed - Prime1;

    var offset = 0;
    while (offset + 16 <= data.Length) {
      v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
      v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]));
      v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]));
      v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]));
      offset += 16;
    }

    var hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
    hash += (uint)data.Length;

    return FinalizeTail(hash, data[offset..]);
  }

  private static uint ComputeSmall(ReadOnlySpan<byte> data, uint seed) {
    var hash = seed + Prime5 + (uint)data.Length;
    return FinalizeTail(hash, data);
  }

  private static uint FinalizeTail(uint hash, ReadOnlySpan<byte> remaining) {
    var offset = 0;

    while (offset + 4 <= remaining.Length) {
      hash += BinaryPrimitives.ReadUInt32LittleEndian(remaining[offset..]) * Prime3;
      hash = BitOperations.RotateLeft(hash, 17) * Prime4;
      offset += 4;
    }

    while (offset < remaining.Length) {
      hash += remaining[offset] * Prime5;
      hash = BitOperations.RotateLeft(hash, 11) * Prime1;
      offset++;
    }

    return Avalanche(hash);
  }

  private void ProcessStripe(ReadOnlySpan<byte> stripe) {
    this._v1 = Round(this._v1, BinaryPrimitives.ReadUInt32LittleEndian(stripe));
    this._v2 = Round(this._v2, BinaryPrimitives.ReadUInt32LittleEndian(stripe[4..]));
    this._v3 = Round(this._v3, BinaryPrimitives.ReadUInt32LittleEndian(stripe[8..]));
    this._v4 = Round(this._v4, BinaryPrimitives.ReadUInt32LittleEndian(stripe[12..]));
  }

  private uint FinalizeHash() {
    uint hash;

    if (this._hasProcessedStripe)
      hash = BitOperations.RotateLeft(this._v1, 1) + BitOperations.RotateLeft(this._v2, 7) + BitOperations.RotateLeft(this._v3, 12) + BitOperations.RotateLeft(this._v4, 18);
    else
      hash = this._seed + Prime5;

    hash += (uint)this._totalLength;

    return FinalizeTail(hash, this._buffer.AsSpan(0, this._bufferUsed));
  }
}
