using Compression.Core.BuildingBlocks;

namespace Compression.Core.Dictionary.DsLz77;

/// <summary>
/// DoubleSpace/DriveSpace LZ77 decompressor. Sister of
/// <see cref="DsLz77Compressor"/>; thin pass-through to the canonical
/// DoubleSpace decoder so the token grammar stays single-sourced.
/// </summary>
public static class DsLz77Decompressor {

  /// <summary>
  /// Decodes a complete DoubleSpace/DriveSpace bit stream (4-byte LE
  /// uncompressed-size header + LSB-first literal/match tokens) into the
  /// original byte sequence.
  /// </summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data)
    => DoubleSpaceCompressor.DecompressStream(data);
}
