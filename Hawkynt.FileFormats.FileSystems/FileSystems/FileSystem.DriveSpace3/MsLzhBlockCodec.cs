#pragma warning disable CS1591
using Compression.Core.Dictionary.MsLzh;

namespace FileSystem.DriveSpace3;

/// <summary>
/// CVF block-framing wrapper for the MS LZH codec used by Microsoft DriveSpace
/// 3 (Win95 Plus! Pack, 1995). Each compressed run consists of a 2-byte
/// little-endian header followed by either an MS LZH-encoded payload (header
/// bit 15 set) or a raw stored payload (bit 15 clear). The low 12 bits encode
/// <c>payload_size - 1</c>.
/// <para>
/// This matches the DOS 6.x DBLSPACE/DRVSPACE block-framing convention; only
/// the inner codec changed when DriveSpace 3 shipped.
/// </para>
/// </summary>
public static class MsLzhBlockCodec {

  /// <summary>
  /// Compresses a single cluster with the MS LZH codec and wraps it in the
  /// CVF 2-byte header. Falls back to a stored run if the compressed payload
  /// would not shrink the data or would exceed the 12-bit size cap.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.Length == 0)
      return [0x00, 0x00];

    var compressed = new MsLzhCompressor().Compress(input);
    if (compressed.Length <= 4096 && compressed.Length < input.Length)
      return WrapRun(compressed, isCompressed: true);

    return WrapRun(input, isCompressed: false);
  }

  /// <summary>
  /// Decompresses a single CVF run (2-byte header + payload). The header's
  /// bit 15 dispatches between MS LZH (set) and raw stored (clear).
  /// </summary>
  public static byte[] Decompress(ReadOnlySpan<byte> block) {
    if (block.Length < 2)
      throw new InvalidDataException("MS LZH block: block too small.");

    var header = (ushort)(block[0] | (block[1] << 8));
    var isCompressed = (header & 0x8000) != 0;
    var dataSize = (header & 0x0FFF) + 1;

    if (2 + dataSize > block.Length)
      throw new InvalidDataException("MS LZH block: payload truncated.");

    var data = block.Slice(2, dataSize);
    return isCompressed ? new MsLzhDecompressor().Decompress(data) : data.ToArray();
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
