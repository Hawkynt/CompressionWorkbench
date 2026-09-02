using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzss;

/// <summary>
/// Exposes the LZSS algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class LzssBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzss";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZSS";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 variant with flag-bit encoding, omitting uncompressed references";
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
    using var ms = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    var encoder = new LzssEncoder(ms);
    var matchFinder = new HashChainMatchFinder(encoder.MaxDistance, encoder.MaxLength);
    encoder.Encode(data, matchFinder);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    using var ms = new MemoryStream(data[4..].ToArray());
    var decoder = new LzssDecoder(ms);
    return decoder.Decode(originalSize);
  }
}
