#pragma warning disable CS1591
using Compression.Core.BuildingBlocks;
using Compression.Core.Dictionary.DsLz77;
using Compression.Core.Dictionary.MsLzh;

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
    => CompressVariant(input, useDriveSpace: false, effort: 0);

  /// <summary>
  /// Compresses with an explicit parse-effort tier (<c>0</c> = greedy,
  /// <c>1</c> = lazy matching, <c>2+</c> = iterated multi-pass) routed
  /// through <see cref="DsLz77Compressor"/>. Falls back to a stored run if
  /// compression does not shrink the data.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input, int effort)
    => CompressVariant(input, useDriveSpace: false, effort: effort);

  /// <summary>
  /// Compresses using the DriveSpace LZ algorithm (8 KiB window) instead of
  /// DoubleSpace JM. The CVF header framing is identical so the reader
  /// handles both transparently.
  /// </summary>
  public static byte[] CompressDriveSpace(ReadOnlySpan<byte> input)
    => CompressVariant(input, useDriveSpace: true, effort: 0);

  /// <summary>
  /// DriveSpace variant with an explicit parse-effort tier — see
  /// <see cref="Compress(ReadOnlySpan{byte}, int)"/>.
  /// </summary>
  public static byte[] CompressDriveSpace(ReadOnlySpan<byte> input, int effort)
    => CompressVariant(input, useDriveSpace: true, effort: effort);

  /// <summary>
  /// Compresses a single cluster with the MS LZH codec (Win95 Plus! Pack
  /// DriveSpace 3) at the default effort 0 (greedy + fixed Huffman tables)
  /// and wraps it in the shared CVF 2-byte header. Falls back to a stored
  /// run when the compressed payload either exceeds the 12-bit CVF size cap
  /// (&gt; 4096 B) or is no smaller than the raw input — the same
  /// shrink-or-store invariant the DS LZ77 path honours.
  /// </summary>
  public static byte[] CompressMsLzh(ReadOnlySpan<byte> input)
    => CompressMsLzh(input, effort: 0);

  /// <summary>
  /// Compresses a single cluster with the MS LZH codec at an explicit
  /// parse-effort tier (<c>0</c> = greedy, <c>1</c> = lazy matching,
  /// <c>2+</c> = iterated multi-pass). The shrink-or-store fallback applies
  /// at every effort level — incompressible clusters always end up as
  /// stored CVF runs regardless of effort.
  /// </summary>
  public static byte[] CompressMsLzh(ReadOnlySpan<byte> input, int effort) {
    if (input.Length == 0)
      return [0x00, 0x00];

    var compressed = new MsLzhCompressor().Compress(input, effort);
    if (compressed.Length <= 4096 && compressed.Length < input.Length)
      return WrapRun(compressed, isCompressed: true);

    return WrapRun(input, isCompressed: false);
  }

  /// <summary>
  /// Decompresses a single CVF run produced by
  /// <see cref="CompressMsLzh(ReadOnlySpan{byte})"/> /
  /// <see cref="CompressMsLzh(ReadOnlySpan{byte}, int)"/>. Header bit 15
  /// dispatches between MS LZH (set) and raw stored (clear).
  /// </summary>
  public static byte[] DecompressMsLzh(ReadOnlySpan<byte> block) {
    if (block.Length < 2)
      throw new InvalidDataException("MS LZH block: too small.");

    var header = (ushort)(block[0] | (block[1] << 8));
    var isCompressed = (header & 0x8000) != 0;
    var dataSize = (header & 0x0FFF) + 1;

    if (2 + dataSize > block.Length)
      throw new InvalidDataException("MS LZH block: payload truncated.");

    var data = block.Slice(2, dataSize);
    return isCompressed ? new MsLzhDecompressor().Decompress(data) : data.ToArray();
  }

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

  private static byte[] CompressVariant(ReadOnlySpan<byte> input, bool useDriveSpace, int effort) {
    if (input.Length == 0)
      return [0x00, 0x00]; // empty stored run, size=1

    var window = useDriveSpace
      ? DsLz77Compressor.DriveSpaceMaxDistance
      : DsLz77Compressor.DefaultMaxDistance;

    // Effort 0 stays on the historical building-block path so the existing
    // sector tests still see the exact same bytes. Effort 1+ routes through
    // DsLz77Compressor's lazy / iterated parses.
    var compressed = effort <= 0
      ? (useDriveSpace
          ? new DriveSpaceCompressor().Compress(input.ToArray())
          : new DoubleSpaceCompressor().Compress(input.ToArray()))
      : DsLz77Compressor.Compress(input, window, effort);

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
