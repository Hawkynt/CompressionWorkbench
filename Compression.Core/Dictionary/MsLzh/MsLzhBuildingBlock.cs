using Compression.Registry;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// Building block wrapper for the MS LZH codec used by Microsoft DriveSpace 3
/// (Windows 95 Plus! Pack, 1995). LZ77 with 4 KiB window plus canonical
/// Huffman coding over a DEFLATE-shaped alphabet.
/// <para>
/// Effort 0 only: static (fixed) Huffman + greedy match selection. Dynamic
/// per-block Huffman trees and lazy/optimal matching are deferred — see
/// <see cref="MsLzhCompressor"/>.
/// </para>
/// </summary>
public sealed class MsLzhBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_MsLzh";

  /// <inheritdoc/>
  public string DisplayName => "MS LZH";

  /// <inheritdoc/>
  public string Description =>
    "Microsoft DriveSpace 3 codec — LZ77 (4 KiB window) + canonical Huffman, fixed-table effort-0 variant";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => new MsLzhCompressor().Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => new MsLzhDecompressor().Decompress(data);
}
