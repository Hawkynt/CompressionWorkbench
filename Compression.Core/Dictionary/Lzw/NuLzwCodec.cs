using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>NuFX/ShrinkIt LZW dialect.</summary>
public enum NuLzwVariant {
  /// <summary>Original ProDOS ShrinkIt LZW/1: dictionary resets for every 4096-byte chunk and the stream carries CRC-16/XMODEM.</summary>
  Lzw1,
  /// <summary>GS/ShrinkIt LZW/2: dictionary may persist between 4096-byte chunks and integrity is supplied by the NuFX thread header.</summary>
  Lzw2,
}

/// <summary>
/// Apple II NuFX/ShrinkIt RLE + LZW codec.
/// </summary>
/// <remarks>
/// The native stream has no expanded-length field. Callers therefore provide the logical
/// expanded length when decoding; the codec trims the zero-filled tail of the final 4096-byte
/// chunk. LZW/1 includes a CRC-16/XMODEM over the padded chunks, while LZW/2 deliberately does
/// not because NuFX record version 3 stores the uncompressed CRC in the thread header.
/// </remarks>
public static class NuLzwCodec {
  private const int ChunkSize = 4096;
  private const int ClearCode = 0x100;
  private const int FirstCode = 0x101;
  private const int LastCode = 0x0FFD;
  private const int TableSize = 4096;
  private const byte DefaultVolume = 254;
  private const byte DefaultDelimiter = 0xDB;

  /// <summary>Compresses a native ShrinkIt LZW/1 or LZW/2 stream.</summary>
  public static byte[] Compress(
    ReadOnlySpan<byte> data,
    NuLzwVariant variant,
    byte volumeNumber = DefaultVolume,
    byte rleDelimiter = DefaultDelimiter
  ) {
    using var output = new MemoryStream();
    var encoder = new EncoderState();
    ushort crc = 0;

    if (variant == NuLzwVariant.Lzw1) {
      output.WriteByte(0);
      output.WriteByte(0);
    }
    output.WriteByte(volumeNumber);
    output.WriteByte(rleDelimiter);

    if (data.IsEmpty)
      return output.ToArray();

    var chunk = new byte[ChunkSize];
    for (var sourceOffset = 0; sourceOffset < data.Length; sourceOffset += ChunkSize) {
      chunk.AsSpan().Clear();
      var logicalLength = Math.Min(ChunkSize, data.Length - sourceOffset);
      data.Slice(sourceOffset, logicalLength).CopyTo(chunk);

      if (variant == NuLzwVariant.Lzw1)
        crc = Crc16Xmodem(chunk, crc);

      var rle = CompressRle(chunk, rleDelimiter);
      var rleLength = Math.Min(rle.Length, ChunkSize);
      var lzwSource = rle.Length < ChunkSize ? rle : chunk;
      var lzw = CompressLzw(lzwSource, encoder);

      if (variant == NuLzwVariant.Lzw2) {
        if (lzw.Length + 2 < rle.Length) {
          WriteUInt16(output, (ushort)(rleLength | 0x8000));
          WriteUInt16(output, checked((ushort)(lzw.Length + 4)));
          output.Write(lzw);
        } else if (rle.Length < ChunkSize) {
          WriteUInt16(output, checked((ushort)rle.Length));
          output.Write(rle);
          encoder.Reset();
        } else {
          WriteUInt16(output, ChunkSize);
          output.Write(chunk);
          encoder.Reset();
        }
      } else {
        WriteUInt16(output, checked((ushort)rleLength));
        if (lzw.Length < rle.Length) {
          output.WriteByte(1);
          output.Write(lzw);
        } else if (rle.Length < ChunkSize) {
          output.WriteByte(0);
          output.Write(rle);
        } else {
          output.WriteByte(0);
          output.Write(chunk);
        }
        encoder.Reset();
      }
    }

    var result = output.ToArray();
    if (variant == NuLzwVariant.Lzw1)
      BinaryPrimitives.WriteUInt16LittleEndian(result, crc);
    return result;
  }

  /// <summary>Expands a native ShrinkIt stream to exactly <paramref name="expandedLength"/> logical bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, NuLzwVariant variant, int expandedLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(expandedLength);
    var minimumHeader = variant == NuLzwVariant.Lzw1 ? 4 : 2;
    if (data.Length < minimumHeader)
      throw new InvalidDataException("NuLZW stream is shorter than its header.");

    var sourceOffset = 0;
    ushort storedCrc = 0;
    if (variant == NuLzwVariant.Lzw1) {
      storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(data);
      sourceOffset += 2;
    }

    _ = data[sourceOffset++]; // 5.25-inch volume number; transport metadata only.
    var delimiter = data[sourceOffset++];
    if (expandedLength == 0)
      return [];

    using var result = new MemoryStream(expandedLength);
    var decoder = new DecoderState();
    ushort crc = 0;

    while (result.Length < expandedLength) {
      if (sourceOffset + 2 > data.Length)
        throw new InvalidDataException("NuLZW stream ended before the next chunk header.");

      var postRle = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
      sourceOffset += 2;
      var lzwUsed = false;

      if (variant == NuLzwVariant.Lzw2) {
        lzwUsed = (postRle & 0x8000) != 0;
        postRle &= 0x7FFF;
        if (lzwUsed) {
          if (sourceOffset + 2 > data.Length)
            throw new InvalidDataException("NuLZW/2 stream ended in an LZW chunk header.");
          // This word is a recovery hint, not a framing boundary. Some historical
          // Macintosh-created ShrinkIt archives stored it byte-swapped or otherwise wrong.
          // Decode until the declared expanded output has been produced and advance by the
          // number of bits actually consumed, matching ShrinkIt/NuFX compatibility practice.
          _ = BinaryPrimitives.ReadUInt16LittleEndian(data[sourceOffset..]);
          sourceOffset += 2;
        }
      } else {
        if (sourceOffset >= data.Length)
          throw new InvalidDataException("NuLZW/1 stream ended before its LZW-use flag.");
        lzwUsed = data[sourceOffset++] != 0;
      }

      if (postRle > ChunkSize)
        throw new InvalidDataException($"NuLZW chunk declares an invalid post-RLE length of {postRle}.");

      byte[] rleBytes;
      if (lzwUsed) {
        if (variant == NuLzwVariant.Lzw1)
          decoder.Reset();
        var decoded = ExpandLzw(data[sourceOffset..], postRle, decoder);
        rleBytes = decoded.Data;
        sourceOffset += decoded.BytesConsumed;
      } else {
        if (variant == NuLzwVariant.Lzw2)
          decoder.Reset();
        if (sourceOffset + postRle > data.Length)
          throw new InvalidDataException("NuLZW stream ended inside an RLE/raw chunk.");
        rleBytes = data.Slice(sourceOffset, postRle).ToArray();
        sourceOffset += postRle;
      }

      var expandedChunk = ExpandRle(rleBytes, postRle, delimiter);
      if (variant == NuLzwVariant.Lzw1)
        crc = Crc16Xmodem(expandedChunk, crc);

      var copyLength = Math.Min(ChunkSize, expandedLength - checked((int)result.Length));
      result.Write(expandedChunk, 0, copyLength);
    }

    if (variant == NuLzwVariant.Lzw1 && crc != storedCrc)
      throw new InvalidDataException($"NuLZW/1 CRC mismatch: calculated 0x{crc:X4}, stored 0x{storedCrc:X4}.");
    return result.ToArray();
  }

  /// <summary>Computes CRC-16/XMODEM (poly 0x1021, refin=false, refout=false) from an arbitrary seed.</summary>
  public static ushort Crc16Xmodem(ReadOnlySpan<byte> data, ushort seed = 0) {
    var crc = seed;
    foreach (var value in data) {
      crc ^= (ushort)(value << 8);
      for (var bit = 0; bit < 8; bit++)
        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
    }
    return crc;
  }

  private static byte[] CompressRle(ReadOnlySpan<byte> source, byte delimiter) {
    using var output = new MemoryStream(ChunkSize + 8);
    var offset = 0;
    while (offset < source.Length) {
      var value = source[offset++];
      var count = 1;
      while (offset < source.Length && source[offset] == value && count < 256) {
        count++;
        offset++;
      }

      if (count > 3 || value == delimiter) {
        output.WriteByte(delimiter);
        output.WriteByte(value);
        output.WriteByte((byte)(count - 1));
      } else {
        for (var i = 0; i < count; i++)
          output.WriteByte(value);
      }

      if (output.Length >= ChunkSize)
        return source.ToArray();
    }
    return output.ToArray();
  }

  private static byte[] ExpandRle(ReadOnlySpan<byte> source, int postRleLength, byte delimiter) {
    if (postRleLength == ChunkSize) {
      if (source.Length < ChunkSize)
        throw new InvalidDataException("NuLZW raw chunk is truncated.");
      return source[..ChunkSize].ToArray();
    }

    var output = new byte[ChunkSize];
    var src = 0;
    var dst = 0;
    while (src < postRleLength) {
      if (src >= source.Length)
        throw new InvalidDataException("NuLZW RLE chunk is truncated.");
      var value = source[src++];
      if (value == delimiter) {
        if (src + 2 > source.Length || src + 2 > postRleLength)
          throw new InvalidDataException("NuLZW RLE escape is truncated.");
        value = source[src++];
        var count = source[src++] + 1;
        if (dst + count > output.Length)
          throw new InvalidDataException("NuLZW RLE expansion exceeds one 4096-byte chunk.");
        output.AsSpan(dst, count).Fill(value);
        dst += count;
      } else {
        if (dst >= output.Length)
          throw new InvalidDataException("NuLZW RLE expansion exceeds one 4096-byte chunk.");
        output[dst++] = value;
      }
    }

    if (src != postRleLength || dst != ChunkSize)
      throw new InvalidDataException($"NuLZW RLE chunk expanded to {dst} bytes instead of {ChunkSize}.");
    return output;
  }

  private static byte[] CompressLzw(ReadOnlySpan<byte> source, EncoderState state) {
    if (source.IsEmpty)
      return [];

    var writer = new LsbBitWriter();
    if (state.NeedInitialClear) {
      writer.Write(ClearCode, state.BitWidth);
      state.Reset();
    }

    var sourceOffset = 0;
    while (sourceOffset < source.Length) {
      // a code, not a byte: once the dictionary grows past 255 the prefix is
      // whatever code matched, which no longer fits in the input's width
      int prefix = source[sourceOffset++];
      var specialBlockEndClear = false;

      while (sourceOffset < source.Length) {
        var suffix = source[sourceOffset++];
        var key = ((int)prefix << 8) | suffix;
        if (state.Dictionary.TryGetValue(key, out var existingCode)) {
          prefix = existingCode;
          continue;
        }

        writer.Write(prefix, state.BitWidth);
        state.Dictionary[key] = state.NextCode;
        if (state.NextCode == (1 << state.BitWidth) - 1)
          state.BitWidth++;
        state.NextCode++;
        prefix = suffix;

        if (state.NextCode <= LastCode)
          continue;

        writer.Write(prefix, state.BitWidth);
        if (sourceOffset < source.Length) {
          writer.Write(ClearCode, state.BitWidth);
          state.Reset();
          break;
        }

        state.NeedInitialClear = true;
        specialBlockEndClear = true;
        sourceOffset = source.Length;
        break;
      }

      if (sourceOffset < source.Length)
        continue;

      if (!specialBlockEndClear) {
        writer.Write(prefix, state.BitWidth);
        if (state.NextCode == (1 << state.BitWidth) - 1)
          state.BitWidth++;
        state.NextCode++;
        if (state.NextCode > LastCode)
          state.NeedInitialClear = true;
      }
      break;
    }

    return writer.Finish();
  }

  private static LzwDecodeResult ExpandLzw(ReadOnlySpan<byte> source, int outputLength, DecoderState state) {
    var reader = new LsbBitReader(source);
    var output = new byte[outputLength];
    var outOffset = 0;
    var entry = state.Entry;
    var bitWidth = state.BitWidth;
    var mask = (1 << bitWidth) - 1;

    while (outOffset < output.Length) {
      var code = reader.Read(bitWidth);
      if (entry + 1 == mask) {
        bitWidth++;
        mask = (mask << 1) | 1;
      }

      if (code == ClearCode) {
        entry = FirstCode - 1;
        bitWidth = 9;
        mask = (1 << bitWidth) - 1;
        continue;
      }
      if (code > entry)
        throw new InvalidDataException($"NuLZW stream references future dictionary code 0x{code:X3} (next 0x{entry + 1:X3}).");

      var depth = state.Depth[code];
      if (outOffset + depth >= output.Length)
        throw new InvalidDataException("NuLZW LZW expansion exceeds the declared post-RLE length.");

      var write = outOffset + depth;
      var current = code;
      byte first = 0;
      while (write >= outOffset) {
        first = state.Final[current];
        output[write--] = first;
        current = state.Parent[current];
      }

      state.Final[entry] = first;
      depth++;
      outOffset += depth;
      entry++;
      if (entry >= TableSize)
        throw new InvalidDataException("NuLZW LZW dictionary exceeded 4096 entries.");

      state.Depth[entry] = depth;
      state.Final[entry] = first;
      state.Parent[entry] = code;
    }

    state.Entry = entry;
    state.BitWidth = bitWidth;
    return new LzwDecodeResult(output, reader.BytesConsumed);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private sealed class EncoderState {
    public Dictionary<int, int> Dictionary { get; } = new();
    public int NextCode { get; set; }
    public int BitWidth { get; set; }
    public bool NeedInitialClear { get; set; }

    public EncoderState() => this.Reset();

    public void Reset() {
      this.Dictionary.Clear();
      this.NextCode = FirstCode;
      this.BitWidth = 9;
      this.NeedInitialClear = false;
    }
  }

  private sealed class DecoderState {
    public int[] Parent { get; } = new int[TableSize];
    public byte[] Final { get; } = new byte[TableSize];
    public int[] Depth { get; } = new int[TableSize];
    public int Entry { get; set; }
    public int BitWidth { get; set; }

    public DecoderState() {
      for (var i = 0; i < FirstCode; i++)
        this.Final[i] = (byte)i;
      this.Reset();
    }

    public void Reset() {
      this.Entry = FirstCode - 1;
      this.BitWidth = 9;
    }
  }

  private sealed class LsbBitWriter {
    private readonly List<byte> _bytes = [];
    private ulong _bits;
    private int _bitCount;

    public void Write(int value, int width) {
      this._bits |= (ulong)(uint)value << this._bitCount;
      this._bitCount += width;
      while (this._bitCount >= 8) {
        this._bytes.Add((byte)this._bits);
        this._bits >>= 8;
        this._bitCount -= 8;
      }
    }

    public byte[] Finish() {
      if (this._bitCount > 0)
        this._bytes.Add((byte)this._bits);
      this._bits = 0;
      this._bitCount = 0;
      return this._bytes.ToArray();
    }
  }

  private ref struct LsbBitReader {
    private readonly ReadOnlySpan<byte> _source;
    private int _bitPosition;

    public LsbBitReader(ReadOnlySpan<byte> source) {
      this._source = source;
      this._bitPosition = 0;
    }

    public readonly int BytesConsumed => (this._bitPosition + 7) >> 3;

    public int Read(int width) {
      if (this._bitPosition + width > this._source.Length * 8)
        throw new InvalidDataException("NuLZW LZW bitstream is truncated.");

      var byteOffset = this._bitPosition >> 3;
      var bitOffset = this._bitPosition & 7;
      uint value = 0;
      for (var i = 0; i < 3 && byteOffset + i < this._source.Length; i++)
        value |= (uint)this._source[byteOffset + i] << (8 * i);
      this._bitPosition += width;
      return (int)((value >> bitOffset) & ((1u << width) - 1));
    }
  }

  private readonly record struct LzwDecodeResult(byte[] Data, int BytesConsumed);
}

/// <summary>
/// Benchmarkable raw building block for GS/ShrinkIt LZW/2.
/// </summary>
/// <remarks>
/// Native NuLZW streams omit the expanded length, so the building-block envelope prefixes a
/// little-endian 32-bit expanded length. <see cref="NuLzwCodec"/> itself reads and writes the
/// native bytes used by NuFX archives.
/// </remarks>
public sealed class NuLzwBuildingBlock : IBuildingBlock {
  /// <inheritdoc />
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_NuLzw";
  /// <inheritdoc />
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "NuLZW (ShrinkIt LZW/2)";
  /// <inheritdoc />
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Apple II GS/ShrinkIt 4 KiB RLE + early-change 9-12 bit LZW/2";
  /// <inheritdoc />
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc />
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var native = NuLzwCodec.Compress(data, NuLzwVariant.Lzw2);
    var result = new byte[4 + native.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    native.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc />
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("NuLZW building-block envelope is truncated.");
    var length = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (length < 0)
      throw new InvalidDataException("NuLZW building-block envelope has a negative expanded length.");
    return NuLzwCodec.Decompress(data[4..], NuLzwVariant.Lzw2, length);
  }
}