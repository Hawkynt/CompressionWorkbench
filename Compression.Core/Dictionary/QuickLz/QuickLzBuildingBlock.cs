using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>Exposes QuickLZ 1.5.0 level-1 compression as a benchmarkable building block.</summary>
/// <remarks>
/// A native QuickLZ payload has no independent expanded-length field. The building-block envelope
/// prefixes a four-byte little-endian length; <see cref="QuickLzCompressor"/> and
/// <see cref="QuickLzDecompressor"/> operate on the actual level-1 payload bytes.
/// </remarks>
public sealed class QuickLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_QuickLz";
  /// <inheritdoc/>
  public string DisplayName => "QuickLZ 1.5 level 1";
  /// <inheritdoc/>
  public string Description => "QuickLZ 1.5 level-1 hash-indexed LZ77 payload coding";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = QuickLzCompressor.Compress(data);
    var result = new byte[checked(4 + compressed.Length)];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("QuickLZ building-block envelope is truncated.");
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize < 0)
      throw new InvalidDataException("QuickLZ building-block envelope has a negative expanded length.");
    return QuickLzDecompressor.Decompress(data[4..], originalSize);
  }
}
