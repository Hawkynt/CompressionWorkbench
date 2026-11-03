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
/// standard bare aPLib stream accepted by <c>aP_depack</c>. This is what
/// <c>aPsafe_pack</c> and the <c>appack</c> example of aPLib 1.1.1 write; aPLib cannot pack
/// an empty input, so neither can this.
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

  /// <summary>Decompresses a standard AP32 stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[(int)HeaderSize];
    input.ReadExactly(header);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (magic != Magic)
      throw new InvalidDataException($"Invalid aPLib magic: 0x{magic:X8}, expected 0x{Magic:X8}.");

    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
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

    if (originalSize == 0)
      throw new InvalidDataException("AP32 original size is zero; aPLib cannot pack an empty input, so no aPLib writer produces this.");

    var expectedLength = (int)originalSize;
    var decodeLimit = expectedLength == int.MaxValue ? int.MaxValue : expectedLength + 1;
    var restored = AplibBuildingBlock.DecompressRaw(packed, decodeLimit, out var endMarkerHit, out _);
    if (restored.Length != expectedLength)
      throw new InvalidDataException($"AP32 original size mismatch: decoded {restored.Length} bytes, expected {expectedLength}.");
    if (decodeLimit != int.MaxValue && !endMarkerHit)
      throw new InvalidDataException("AP32 aPLib payload did not terminate with an end marker.");

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
    if (original.Length == 0)
      throw new ArgumentException("AP32 cannot hold an empty input: aPsafe_pack refuses it and aPsafe_depack rejects an original size of zero.", nameof(input));
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
}
