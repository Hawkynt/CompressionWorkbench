using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// The least-significant-bit-first bit streams the PKZIP 1.x methods (Shrink, Reduce, Implode)
/// are written in. Converted from hwzip 2.4's bitstream.h (Hans Wennborg, public domain).
/// </summary>
internal ref struct ZipLegacyBitReader {
  private readonly ReadOnlySpan<byte> _source;
  private readonly long _endBit;
  private long _bitPos;

  public ZipLegacyBitReader(ReadOnlySpan<byte> source) {
    this._source = source;
    this._endBit = (long)source.Length * 8;
  }

  /// <summary>The next 57 or more bits, lowest first; zero past the end.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public readonly ulong Bits() {
    var index = (int)(this._bitPos >> 3);
    ulong bits = 0;
    var available = Math.Min(8, this._source.Length - index);
    for (var i = 0; i < available; ++i)
      bits |= (ulong)this._source[index + i] << (i * 8);

    return bits >> (int)(this._bitPos & 7);
  }

  /// <summary>Consumes <paramref name="count"/> bits; false (consuming nothing) when fewer remain.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool Advance(int count) {
    if (this._endBit - this._bitPos < count)
      return false;

    this._bitPos += count;
    return true;
  }

  /// <summary>Bytes consumed, rounding a partial byte up.</summary>
  public readonly long BytesRead => (this._bitPos + 7) >> 3;

  /// <summary>The low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ulong Low(ulong value, int count) => count >= 64 ? value : value & ((1UL << count) - 1);
}

/// <summary>Least-significant-bit-first bit writer for the PKZIP 1.x methods.</summary>
internal sealed class ZipLegacyBitWriter {
  private byte[] _buffer;
  private long _bitPos;

  public ZipLegacyBitWriter(int capacity) => this._buffer = new byte[Math.Max(16, capacity)];

  /// <summary>Appends the low <paramref name="count"/> (at most 57) bits of <paramref name="bits"/>.</summary>
  public void Write(ulong bits, int count) {
    var index = (int)(this._bitPos >> 3);
    if (index + 9 > this._buffer.Length)
      Array.Resize(ref this._buffer, Math.Max(this._buffer.Length * 2, index + 16));

    var shift = (int)(this._bitPos & 7);
    var value = bits << shift;
    this._buffer[index] |= (byte)value;
    for (var i = 1; i * 8 - shift < count; ++i)
      this._buffer[index + i] |= (byte)(value >> (i * 8));

    this._bitPos += count;
  }

  /// <summary>The bytes written so far, the last one zero-padded.</summary>
  public byte[] ToArray() => this._buffer.AsSpan(0, (int)((this._bitPos + 7) >> 3)).ToArray();
}
