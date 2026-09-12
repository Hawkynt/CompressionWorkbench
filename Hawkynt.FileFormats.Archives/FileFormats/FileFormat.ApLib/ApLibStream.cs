using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Aplib;

namespace FileFormat.ApLib;

/// <summary>
/// Reads and writes the standard aPLib AP32 safe-wrapper format.
/// </summary>
/// <remarks>
/// The 24-byte little-endian AP32 header contains the header size, packed size,
/// packed CRC-32, original size and original CRC-32. The payload itself is the
/// standard bare aPLib stream accepted by <c>aP_depack</c>. Streams produced by
/// older CompressionWorkbench versions used a private self-framed dialect with a
/// zero header-size field; those files remain readable for backwards compatibility.
/// </remarks>
public static class ApLibStream {
  private const uint Magic = 0x32335041u; // "AP32" as a little-endian uint.
  private const uint HeaderSize = 24;

  /// <summary>Compresses <paramref name="input"/> into a standard AP32 stream.</summary>
  public static void Compress(Stream input, Stream output) => CompressCore(input, output, optimal: false);

  /// <summary>
  /// Compresses <paramref name="input"/> into a standard AP32 stream using the
  /// cost-based aPLib optimizer.
  /// </summary>
  public static void CompressOptimal(Stream input, Stream output) => CompressCore(input, output, optimal: true);

  /// <summary>Decompresses a standard AP32 stream, or a legacy CompressionWorkbench AP32 stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[(int)HeaderSize];
    input.ReadExactly(header);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (magic != Magic)
      throw new InvalidDataException($"Invalid aPLib magic: 0x{magic:X8}, expected 0x{Magic:X8}.");

    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
    if (headerSize == 0) {
      DecompressLegacy(input, output, header);
      return;
    }

    if (headerSize < HeaderSize)
      throw new InvalidDataException($"Invalid AP32 header size {headerSize}; expected at least {HeaderSize} bytes.");

    var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
    var expectedPackedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    var originalSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
    var expectedOriginalCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

    SkipExactly(input, headerSize - HeaderSize);
    var packed = ReadPayload(input, packedSize, "packed");
    var packedCrc = Crc32.Compute(packed);
    if (packedCrc != expectedPackedCrc)
      throw new InvalidDataException($"Packed aPLib data CRC mismatch: 0x{packedCrc:X8} != 0x{expectedPackedCrc:X8}.");

    if (originalSize > int.MaxValue)
      throw new InvalidDataException($"AP32 original size {originalSize} exceeds the managed decoder limit.");

    byte[] restored;
    if (originalSize == 0) {
      restored = [];
    } else {
      var expectedLength = (int)originalSize;
      var decodeLimit = expectedLength == int.MaxValue ? int.MaxValue : expectedLength + 1;
      restored = AplibBuildingBlock.DecompressRaw(packed, decodeLimit, out var endMarkerHit, out _);
      if (restored.Length != expectedLength)
        throw new InvalidDataException($"AP32 original size mismatch: decoded {restored.Length} bytes, expected {expectedLength}.");
      if (decodeLimit != int.MaxValue && !endMarkerHit)
        throw new InvalidDataException("AP32 aPLib payload did not terminate with an end marker.");
    }

    var originalCrc = Crc32.Compute(restored);
    if (originalCrc != expectedOriginalCrc)
      throw new InvalidDataException($"Decompressed data CRC mismatch: 0x{originalCrc:X8} != 0x{expectedOriginalCrc:X8}.");

    output.Write(restored);
  }

  private static void CompressCore(Stream input, Stream output, bool optimal) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var source = new MemoryStream();
    input.CopyTo(source);
    var original = source.ToArray();
    var packed = optimal
      ? AplibCompressor.CompressOptimal(original)
      : AplibCompressor.Compress(original);

    Span<byte> header = stackalloc byte[(int)HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)packed.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Crc32.Compute(packed));
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..], checked((uint)original.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Crc32.Compute(original));
    output.Write(header);
    output.Write(packed);
  }

  private static byte[] ReadPayload(Stream input, uint size, string label) {
    if (size > int.MaxValue)
      throw new InvalidDataException($"AP32 {label} size {size} exceeds the managed decoder limit.");
    var result = new byte[(int)size];
    input.ReadExactly(result);
    return result;
  }

  private static void SkipExactly(Stream input, uint count) {
    Span<byte> buffer = stackalloc byte[256];
    while (count > 0) {
      var chunk = (int)Math.Min((uint)buffer.Length, count);
      input.ReadExactly(buffer[..chunk]);
      count -= (uint)chunk;
    }
  }

  // CompressionWorkbench's former FileFormat.ApLib codec wrote a zero in the
  // AP32 header-size field and used an unrelated LSB-first token grammar. Keep a
  // decoder for those already-produced files, but never emit the dialect again.
  private static void DecompressLegacy(Stream input, Stream output, ReadOnlySpan<byte> header) {
    var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
    var originalSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    var expectedOriginalCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
    if (originalSize > int.MaxValue)
      throw new InvalidDataException($"Legacy AP32 original size {originalSize} exceeds the managed decoder limit.");

    var packed = ReadPayload(input, packedSize, "legacy packed");
    var restored = DecompressLegacyBlock(packed, (int)originalSize);
    var originalCrc = Crc32.Compute(restored);
    if (originalCrc != expectedOriginalCrc)
      throw new InvalidDataException($"Legacy decompressed data CRC mismatch: 0x{originalCrc:X8} != 0x{expectedOriginalCrc:X8}.");
    output.Write(restored);
  }

  private static byte[] DecompressLegacyBlock(byte[] compressed, int originalSize) {
    if (originalSize == 0)
      return [];

    var reader = new LegacyBitReader(compressed);
    var output = new byte[originalSize];
    var position = 0;
    var lastOffset = -1;
    output[position++] = reader.ReadByte();

    while (position < originalSize) {
      if (reader.ReadBit() == 1) {
        output[position++] = reader.ReadByte();
        continue;
      }

      if (reader.ReadBit() == 1) {
        if (reader.ReadBit() == 1) {
          var highBits = ReadLegacyGamma(reader);
          int offset;
          if (highBits == 2) {
            offset = reader.ReadByte();
            if (offset == 0)
              break;
          } else {
            offset = checked(((highBits - 2) << 8) | reader.ReadByte());
          }

          var length = ReadLegacyGamma(reader);
          if (offset < 128)
            length += 2;
          else if (offset < 1280)
            length += 1;
          else if (offset >= 32000)
            length -= 1;
          length = Math.Max(1, length);

          var distance = checked(offset + 1);
          if (distance > position)
            throw new InvalidDataException("Legacy aPLib match points before the start of the output.");
          lastOffset = offset;
          CopyLegacyMatch(output, ref position, distance, length);
        } else {
          if (lastOffset < 0)
            throw new InvalidDataException("Legacy aPLib repeat match appears before an offset was established.");
          var length = checked(ReadLegacyGamma(reader) + 2);
          var distance = checked(lastOffset + 1);
          if (distance > position)
            throw new InvalidDataException("Legacy aPLib repeat match points before the start of the output.");
          CopyLegacyMatch(output, ref position, distance, length);
        }
        continue;
      }

      var code = (reader.ReadBit() << 1) | reader.ReadBit();
      if (code == 0) {
        output[position++] = 0;
      } else {
        if (code > position)
          throw new InvalidDataException("Legacy aPLib byte match points before the start of the output.");
        output[position] = output[position - code];
        ++position;
      }
    }

    if (position != originalSize)
      throw new InvalidDataException($"Legacy AP32 original size mismatch: decoded {position} bytes, expected {originalSize}.");
    return output;
  }

  private static void CopyLegacyMatch(byte[] output, ref int position, int distance, int length) {
    for (var index = 0; index < length && position < output.Length; ++index) {
      output[position] = output[position - distance];
      ++position;
    }
  }

  private static int ReadLegacyGamma(LegacyBitReader reader) {
    var result = 1;
    do {
      if (result > int.MaxValue >> 1)
        throw new InvalidDataException("Legacy aPLib gamma code overflows Int32.");
      result = (result << 1) + reader.ReadBit();
    } while (reader.ReadBit() == 0);
    return result;
  }

  private sealed class LegacyBitReader(byte[] data) {
    private int _position;
    private int _tag;
    private int _bitsLeft;

    public int ReadBit() {
      if (this._bitsLeft == 0) {
        if (this._position >= data.Length)
          throw new InvalidDataException("Unexpected end of legacy aPLib compressed data.");
        this._tag = data[this._position++];
        this._bitsLeft = 8;
      }

      var bit = this._tag & 1;
      this._tag >>= 1;
      --this._bitsLeft;
      return bit;
    }

    public byte ReadByte() {
      if (this._position >= data.Length)
        throw new InvalidDataException("Unexpected end of legacy aPLib compressed data.");
      return data[this._position++];
    }
  }
}
