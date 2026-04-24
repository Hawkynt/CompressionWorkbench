#pragma warning disable CS1591
using Compression.Core.BuildingBlocks;

namespace FileSystem.DoubleSpace;

/// <summary>
/// DoubleSpace / DriveSpace sector-level LZ77 compression.
/// Each CVF "compressed run" consists of a 2-byte little-endian header plus a
/// payload. In the header: bit 15 indicates compressed (<c>1</c>) or stored
/// (<c>0</c>); bits 0..11 carry <c>payload_size − 1</c> (so a payload of 4096 B
/// encodes as <c>0x0FFF</c>).
/// <para>
/// The compression algorithm itself is delegated to
/// <see cref="DoubleSpaceCompressor"/> (DBLS, 4 KiB window) and
/// <see cref="DriveSpaceCompressor"/> (DVRS, 8 KiB window) in
/// <c>Compression.Core.BuildingBlocks</c>. Both produce a stream prefixed with
/// a 4-byte little-endian uncompressed-size header followed by the variable
/// bit-length token sequence.
/// </para>
/// <para>
/// When the compressed payload would not fit in the 12-bit header size field
/// (&gt; 4096 B) or is not smaller than the raw input, a stored run is emitted
/// instead. On decode, the header's bit 15 picks the branch.
/// </para>
/// </summary>
public static class DsCompression {

  /// <summary>
  /// Compresses a single sector (at most 4096 B) using the DoubleSpace JM
  /// algorithm and returns the complete CVF run (2-byte header + payload).
  /// Falls back to a stored run if compression does not shrink the data.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input)
    => CompressVariant(input, useDriveSpace: false);

  /// <summary>
  /// Compresses using the DriveSpace LZ algorithm (8 KiB window) instead of
  /// DoubleSpace JM. The CVF header framing is identical so the reader
  /// handles both transparently.
  /// </summary>
  public static byte[] CompressDriveSpace(ReadOnlySpan<byte> input)
    => CompressVariant(input, useDriveSpace: true);

  /// <summary>
  /// Decompresses a single CVF run (2-byte header + payload). The compressed
  /// payload is decoded with the DoubleSpace/DriveSpace building block —
  /// both variants share the same token grammar, so a single decoder handles
  /// them.
  /// </summary>
  public static byte[] Decompress(ReadOnlySpan<byte> block) {
    if (block.Length < 2)
      throw new InvalidDataException("DS: block too small.");

    var header = (ushort)(block[0] | (block[1] << 8));
    var isCompressed = (header & 0x8000) != 0;
    var dataSize = (header & 0x0FFF) + 1;

    if (2 + dataSize > block.Length)
      throw new InvalidDataException("DS: block data truncated.");

    var data = block.Slice(2, dataSize);

    if (!isCompressed)
      return data.ToArray();

    // Compressed payload is a complete BB stream (4-byte LE size header + bit stream).
    return DoubleSpaceCompressor.DecompressStream(data);
  }

  // =========================================================================

  private static byte[] CompressVariant(ReadOnlySpan<byte> input, bool useDriveSpace) {
    if (input.Length == 0)
      return [0x00, 0x00]; // empty stored run, size=1

    var compressed = useDriveSpace
      ? new DriveSpaceCompressor().Compress(input.ToArray())
      : new DoubleSpaceCompressor().Compress(input.ToArray());

    // Compressed payload must fit the 12-bit size field (max 4096 B) *and*
    // be smaller than the raw input to be worth emitting.
    if (compressed.Length <= 4096 && compressed.Length < input.Length)
      return WrapRun(compressed, isCompressed: true);

    return WrapRun(input, isCompressed: false);
  }

  private static byte[] WrapRun(ReadOnlySpan<byte> payload, bool isCompressed) {
    var result = new byte[2 + payload.Length];
    var header = (ushort)(payload.Length - 1);
    if (isCompressed) header |= 0x8000;
    result[0] = (byte)(header & 0xFF);
    result[1] = (byte)((header >> 8) & 0xFF);
    payload.CopyTo(result.AsSpan(2));
    return result;
  }
}
