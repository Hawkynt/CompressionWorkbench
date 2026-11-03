using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Codec.TrackerXmIt;

/// <summary>
/// Decoder for Impulse Tracker compressed samples (IT 2.14 single-delta and IT 2.15
/// double-delta packing, 8- and 16-bit), written from <c>docs/IT-SAMPLE-COMPRESSION-SPEC.md</c>.
/// </summary>
/// <remarks>
/// The stream is a sequence of blocks, each a little-endian <c>u16</c> byte count followed by an
/// LSB-first bit stream that decodes to at most 0x8000 bytes of sample data. Codes are deltas of
/// a variable bit width; three escape forms change the width without producing a sample. Every
/// block restarts at the maximum width with both integrators cleared. Bits read past a block's
/// own byte count are zero (encoders legitimately stop short of a final code's high bits), but a
/// block whose header or byte count runs past the end of the input is refused with
/// <see cref="InvalidDataException"/> rather than invented.
/// </remarks>
public static class ItSampleDecompressor {

  /// <summary>Decodes an IT214/IT215-compressed 8-bit sample.</summary>
  /// <param name="compressed">Data starting at the sample pointer; may extend past the sample.</param>
  /// <param name="lengthSamples">Sample count from the sample header.</param>
  /// <param name="it215"><see langword="true"/> for IT 2.15 double-delta packing.</param>
  /// <returns>Exactly <paramref name="lengthSamples"/> samples (empty when it is not positive).</returns>
  /// <exception cref="InvalidDataException">The stream is truncated or selects an impossible width.</exception>
  public static sbyte[] Decompress8(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215)
    => Decode<sbyte>(compressed, lengthSamples, it215);

  /// <summary>Decodes an IT214/IT215-compressed 16-bit sample.</summary>
  /// <param name="compressed">Data starting at the sample pointer; may extend past the sample.</param>
  /// <param name="lengthSamples">Sample count from the sample header.</param>
  /// <param name="it215"><see langword="true"/> for IT 2.15 double-delta packing.</param>
  /// <returns>Exactly <paramref name="lengthSamples"/> samples (empty when it is not positive).</returns>
  /// <exception cref="InvalidDataException">The stream is truncated or selects an impossible width.</exception>
  public static short[] Decompress16(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215)
    => Decode<short>(compressed, lengthSamples, it215);

  /// <summary>Bytes of decoded sample data one block carries at most.</summary>
  private const int BlockBytes = 0x8000;

  private static T[] Decode<T>(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215)
    where T : unmanaged, IBinaryInteger<T> {
    if (lengthSamples <= 0)
      return [];

    var samplesPerBlock = BlockBytes / Unsafe.SizeOf<T>();
    var blockCount = (int)(((long)lengthSamples + samplesPerBlock - 1) / samplesPerBlock);

    // Walk the block headers first: a truncated stream (or a header length nowhere near the
    // data) is refused before the output — up to 4 GiB for a hostile length — is allocated.
    var position = 0;
    for (var block = 0; block < blockCount; ++block)
      position += 2 + PackedLength(compressed, position);

    var output = new T[lengthSamples];
    position = 0;
    for (var produced = 0; produced < lengthSamples;) {
      var packedBytes = PackedLength(compressed, position);
      position += 2;
      var count = Math.Min(samplesPerBlock, lengthSamples - produced);
      DecodeBlock(compressed.Slice(position, packedBytes), output.AsSpan(produced, count), it215);
      position += packedBytes;
      produced += count;
    }

    return output;
  }

  /// <summary>The packed byte count of the block at <paramref name="position"/>, which must lie wholly inside the data.</summary>
  private static int PackedLength(ReadOnlySpan<byte> compressed, int position) {
    if (compressed.Length - position < 2)
      throw new InvalidDataException($"IT sample block header at offset {position} lies past the end of the data.");

    int packedBytes = BinaryPrimitives.ReadUInt16LittleEndian(compressed[position..]);
    if (compressed.Length - position - 2 < packedBytes)
      throw new InvalidDataException($"IT sample block at offset {position} claims {packedBytes} bytes, only {compressed.Length - position - 2} remain.");

    return packedBytes;
  }

  /// <summary>Decodes one block into <paramref name="target"/>, which it fills completely.</summary>
  private static void DecodeBlock<T>(ReadOnlySpan<byte> packed, Span<T> target, bool it215)
    where T : unmanaged, IBinaryInteger<T> {
    // Per-size constants (spec §4). The type test folds away when the JIT specialises T.
    var is8Bit = typeof(T) == typeof(sbyte);
    var sampleBits = is8Bit ? 8 : 16;
    var maxWidth = sampleBits + 1;              // W
    var widthFieldBits = is8Bit ? 3 : 4;        // k, the low-width escape's payload
    var bandHalf = is8Bit ? 4 : 8;              // c, half the mid-width escape band
    var sampleMask = (1u << sampleBits) - 1;    // M
    var escapeFlag = 1u << sampleBits;          // bit W-1, the full-width escape

    var bits = new LsbBitReader(packed);
    var width = maxWidth;
    T first = T.Zero, second = T.Zero;

    for (var index = 0; index < target.Length;) {
      var value = bits.Read(width);

      if (width < 7) {
        if (value == 1u << (width - 1)) {
          width = NextWidth((int)bits.Read(widthFieldBits) + 1, width);
          continue;
        }
      } else if (width < maxWidth) {
        var bandBase = (sampleMask >> (maxWidth - width)) - (uint)bandHalf;
        if (value > bandBase && value <= bandBase + (uint)(2 * bandHalf)) {
          width = NextWidth((int)(value - bandBase), width);
          continue;
        }
      } else if ((value & escapeFlag) != 0) {
        width = (int)(value & sampleMask) + 1;
        if (width > maxWidth)
          throw new InvalidDataException($"IT sample stream selects bit width {width}; at most {maxWidth} is possible.");
        continue;
      }

      // A delta: sign-extend from the code width, capped at the sample size (the full-width
      // code carries the sample in its low bits under the escape flag).
      var shift = 32 - Math.Min(width, sampleBits);
      var delta = (int)(value << shift) >> shift;

      first += T.CreateTruncating(delta);
      second += first;
      target[index++] = it215 ? second : first;
    }
  }

  /// <summary>An escape never re-selects the current width, so codes at or above it are shifted up by one.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int NextWidth(int code, int current) => code < current ? code : code + 1;

  /// <summary>LSB-first bit reader over one block; reads past the block's end yield zero bits.</summary>
  private ref struct LsbBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    private ulong _buffer;
    private int _available;

    /// <summary>Reads <paramref name="count"/> (1..17) bits; the first bit read becomes bit 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int count) {
      if (this._available < count)
        this.Refill();

      var value = (uint)this._buffer & ((1u << count) - 1);
      this._buffer >>= count;
      this._available -= count;
      return value;
    }

    private void Refill() {
      var room = (64 - this._available) >> 3;
      if (this._data.Length - this._position >= 8) {
        var chunk = BinaryPrimitives.ReadUInt64LittleEndian(this._data[this._position..]);
        if (room < 8)
          chunk &= (1UL << (room * 8)) - 1;
        this._buffer |= chunk << this._available;
        this._position += room;
        this._available += room * 8;
        return;
      }

      // Tail of the block: take what is left, then pad with zero bits.
      for (; room > 0; --room) {
        if (this._position < this._data.Length)
          this._buffer |= (ulong)this._data[this._position] << this._available;
        ++this._position;
        this._available += 8;
      }
    }
  }
}
