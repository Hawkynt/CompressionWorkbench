using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Exposes the Quantum algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
/// <remarks>
/// Quantum is the LZ77 + adaptive range coding method of the Microsoft Cabinet
/// (CAB) file format, specified in [MS-CAB] section 2.4 (compression type 0x0003).
/// The block uses the largest window level (7 = 64 KB) and the compressor's
/// rescale threshold, which the decompressor must be told about explicitly.
/// </remarks>
public sealed class QuantumBuildingBlock : IBuildingBlock {
  /// <summary>Window level used by this block: 7 selects the maximum 64 KB window.</summary>
  private const int WindowLevel = QuantumConstants.MaxWindowLevel;

  /// <inheritdoc/>
  public string Id => "BB_Quantum";
  /// <inheritdoc/>
  public string DisplayName => "Quantum";
  /// <inheritdoc/>
  public string Description => "LZ77 with an adaptive range coder, the Quantum method of Microsoft CAB archives";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = QuantumCompressor.Compress(data, WindowLevel);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return QuantumDecompressor.Decompress(data[4..], originalSize, WindowLevel,
      QuantumConstants.CompressorRescaleThreshold);
  }
}
