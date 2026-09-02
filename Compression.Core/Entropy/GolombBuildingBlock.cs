using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Selects how a Golomb stream chooses its parameter M and frames its element count.
/// The unary-plus-truncated-binary coding of each value is identical in every profile;
/// only the parameter policy and the header differ.
/// </summary>
public enum GolombProfile {

  /// <summary>
  /// M is derived from the sample mean as <c>max(1, round(mean × ln 2))</c> and the element
  /// count is carried as a 4-byte little-endian field. This is the historical default.
  /// </summary>
  MeanAdaptive = 0,

  /// <summary>
  /// M is pinned by the caller and the element count is carried as an LEB128 varint, which
  /// keeps the header down to two bytes for short inputs. Coding remains Golomb/Rice; with a
  /// power-of-two M the truncated-binary remainder degenerates to plain Rice.
  /// </summary>
  FixedParameter = 1,
}

/// <summary>
/// Exposes Golomb/Rice coding as a benchmarkable building block.
/// </summary>
public sealed class GolombBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Golomb";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Golomb/Rice";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Optimal coding for geometric distributions, Rice when M is power-of-2";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <summary>The parameter pinned by <see cref="GolombProfile.FixedParameter"/> callers that do not supply one.</summary>
  internal const int DefaultFixedParameter = 2;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) => Compress(data, GolombProfile.MeanAdaptive);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) => Decompress(data, GolombProfile.MeanAdaptive);

  /// <summary>
  /// Encodes <paramref name="data"/> under the given <paramref name="profile"/>.
  /// </summary>
  /// <param name="data">The bytes to encode; each byte is coded as one Golomb value.</param>
  /// <param name="profile">The parameter and header policy to apply.</param>
  /// <param name="fixedParameter">
  /// The Golomb parameter M used when <paramref name="profile"/> is
  /// <see cref="GolombProfile.FixedParameter"/>; ignored otherwise. Must be 1..255.
  /// </param>
  /// <returns>The encoded stream.</returns>
  public static byte[] Compress(
    ReadOnlySpan<byte> data,
    GolombProfile profile,
    int fixedParameter = DefaultFixedParameter
  ) {
    if (profile is GolombProfile.FixedParameter && fixedParameter is < 1 or > 255)
      throw new ArgumentOutOfRangeException(nameof(fixedParameter), "Golomb: M must be in 1..255.");

    // A fixed-parameter stream has no distinguished empty encoding — an empty payload is
    // simply an empty stream, which keeps it interchangeable with other varint-framed streams.
    if (profile is GolombProfile.FixedParameter && data.Length == 0)
      return [];

    using var ms = new MemoryStream();

    int m;
    if (profile is GolombProfile.FixedParameter) {
      m = fixedParameter;
    } else {
      // Auto-select M based on data mean: M = max(1, round(mean * ln(2))).
      m = 1;
      if (data.Length > 0) {
        var sum = 0.0;
        foreach (var b in data)
          sum += b;
        var mean = sum / data.Length;
        m = Math.Max(1, (int)Math.Round(mean * Math.Log(2)));
        if (m > 255) m = 255;
      }
    }

    // Write header: 1-byte M, then the element count.
    ms.WriteByte((byte)m);
    if (profile is GolombProfile.FixedParameter) {
      WriteVarInt(ms, (uint)data.Length);
    } else {
      Span<byte> sizeHeader = stackalloc byte[4];
      BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
      ms.Write(sizeHeader);

      if (data.Length == 0)
        return ms.ToArray();
    }

    // Encode data.
    var writer = new BitWriter(ms);
    foreach (var b in data)
      EncodeGolomb(writer, b, m);
    writer.Flush();

    return ms.ToArray();
  }

  /// <summary>
  /// Decodes a stream produced by <see cref="Compress(ReadOnlySpan{byte}, GolombProfile, int)"/>
  /// under the same <paramref name="profile"/>. M is always read back from the stream, so the
  /// profile only selects how the element count is framed.
  /// </summary>
  /// <param name="data">The encoded stream.</param>
  /// <param name="profile">The header policy the stream was written with.</param>
  /// <returns>The decoded bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, GolombProfile profile) {
    if (profile is GolombProfile.FixedParameter && data.Length == 0)
      return [];

    var offset = 0;

    // Read header: 1-byte M.
    var m = (int)data[offset++];

    int originalSize;
    if (profile is GolombProfile.FixedParameter) {
      originalSize = (int)ReadVarInt(data, ref offset);
    } else {
      originalSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
      offset += 4;
    }

    if (originalSize == 0)
      return [];

    // Read remaining bytes as bitstream.
    var bitData = data[offset..].ToArray();
    var result = new byte[originalSize];
    // The unary quotient makes Golomb coding expand badly when the parameter is
    // small relative to the data, so the bit position passes int.MaxValue while
    // the stream is still far short of Array.MaxLength.
    var bitIndex = 0L;

    for (var i = 0; i < originalSize; i++)
      result[i] = (byte)DecodeGolomb(bitData, ref bitIndex, m);

    return result;
  }

  /// <summary>Writes an unsigned LEB128 varint: seven payload bits per byte, high bit set while more follow.</summary>
  private static void WriteVarInt(Stream output, uint value) {
    while (value >= 0x80) {
      output.WriteByte((byte)((value & 0x7F) | 0x80));
      value >>= 7;
    }
    output.WriteByte((byte)value);
  }

  /// <summary>Reads an unsigned LEB128 varint written by <see cref="WriteVarInt"/>.</summary>
  private static uint ReadVarInt(ReadOnlySpan<byte> data, ref int offset) {
    var result = 0u;
    var shift = 0;
    while (true) {
      if (offset >= data.Length)
        throw new InvalidDataException("Golomb: truncated varint in header.");
      if (shift >= 32)
        throw new InvalidDataException("Golomb: varint in header overflows 32 bits.");
      var b = data[offset++];
      result |= (uint)(b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return result;
      shift += 7;
    }
  }

  private static void EncodeGolomb(BitWriter writer, int value, int m) {
    var q = value / m;
    var r = value % m;

    // Unary: q one-bits followed by a zero-bit.
    for (var i = 0; i < q; i++)
      writer.WriteBit(1);
    writer.WriteBit(0);

    // Truncated binary encoding of remainder.
    if (m == 1)
      return;

    var k = FloorLog2(m);
    var c = (1 << (k + 1)) - m;

    if (r < c) {
      WriteBitsHighFirst(writer, r, k);
    } else {
      WriteBitsHighFirst(writer, r + c, k + 1);
    }
  }

  private static int DecodeGolomb(byte[] data, ref long bitIndex, int m) {
    // Read unary quotient: count 1-bits until a 0-bit.
    var q = 0;
    while (ReadBit(data, ref bitIndex) == 1)
      q++;

    // Read truncated binary remainder.
    int r;
    if (m == 1) {
      r = 0;
    } else {
      var k = FloorLog2(m);
      var c = (1 << (k + 1)) - m;

      r = ReadBitsHighFirst(data, ref bitIndex, k);
      if (r >= c) {
        r = (r << 1) | ReadBit(data, ref bitIndex);
        r -= c;
      }
    }

    return q * m + r;
  }

  private static int FloorLog2(int value) {
    var result = 0;
    var v = value;
    while (v > 1) {
      result++;
      v >>= 1;
    }
    return result;
  }

  private static void WriteBitsHighFirst(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int ReadBitsHighFirst(byte[] data, ref long bitIndex, int count) {
    var value = 0;
    for (var i = 0; i < count; i++)
      value = (value << 1) | ReadBit(data, ref bitIndex);
    return value;
  }

  private static int ReadBit(byte[] data, ref long bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Golomb bitstream.");
    var bit = (data[bitIndex / 8] >> (7 - (int)(bitIndex % 8))) & 1;
    bitIndex++;
    return bit;
  }

  private sealed class BitWriter(Stream output) {
    private byte _buffer;
    private int _bitCount;

    public void WriteBit(int bit) {
      _buffer = (byte)((_buffer << 1) | (bit & 1));
      _bitCount++;
      if (_bitCount == 8) {
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }

    public void Flush() {
      if (_bitCount > 0) {
        _buffer <<= (8 - _bitCount);
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }
  }
}
