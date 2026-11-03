using Compression.Registry;

namespace Compression.Core.Dictionary.Ibm842;

/// <summary>
/// Exposes IBM 842 as a benchmarkable building block: the raw stream the Linux kernel's
/// <c>sw842_compress</c>/<c>sw842_decompress</c> write and read (templates of 2-, 4- and 8-byte
/// literals and ring-buffer indexes over 8-byte groups, ended by a CRC-32), with no length prefix.
/// See <c>docs/IBM842-ON-DISK.md</c>.
/// </summary>
public sealed class Ibm842BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_842";
  /// <inheritdoc/>
  public string DisplayName => "842";
  /// <inheritdoc/>
  public string Description => "IBM 842 (Linux lib/842 software stream) with 2/4/8-byte template matching";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Ibm842Codec.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Ibm842Codec.Decompress(data);
}
