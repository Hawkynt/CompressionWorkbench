using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Exposes PKWARE ZIP <b>Reduce</b> (methods 2-5) as a benchmarkable building
/// block. Reduce is a probabilistic byte predictor (the "compression factor"
/// selects the follower-set width) followed by a run-length expansion stage.
/// The raw <see cref="ReduceDecoder"/> needs both the decompressed size and the
/// factor, so the building block prepends a 4-byte little-endian length header
/// followed by a single factor byte.
/// </summary>
public sealed class ReduceBuildingBlock : IBuildingBlock {
  private const int Factor = 4; // methods 2-5 == factor 1-4; 4 is the strongest.

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Reduce";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Reduce";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "PKWARE ZIP Reduce (methods 2-5) - probabilistic follower-set predictor plus RLE expansion";
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
    var body = data.Length == 0 ? [] : ReduceEncoder.Encode(data, Factor);
    var output = new byte[5 + body.Length];
    BinaryPrimitives.WriteInt32LittleEndian(output, data.Length);
    output[4] = (byte)Factor;
    body.CopyTo(output.AsSpan(5));
    return output;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 5) throw new InvalidDataException("Reduce: input smaller than 5-byte header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0) throw new InvalidDataException("Reduce: negative decompressed size.");
    var factor = data[4];
    return size == 0 ? [] : ReduceDecoder.Decode(data[5..], size, factor);
  }
}
