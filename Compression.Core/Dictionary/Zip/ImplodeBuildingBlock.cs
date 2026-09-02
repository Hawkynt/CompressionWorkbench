using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Exposes PKWARE ZIP <b>Implode</b> (method 6) as a benchmarkable building
/// block. Implode is an LZ77 variant with a 4K or 8K sliding dictionary whose
/// literals, lengths and distances are Shannon-Fano coded. The raw
/// <see cref="ImplodeDecoder"/> needs the decompressed size and the two header
/// flags (literal tree present, 8K dictionary), so the building block prepends a
/// 4-byte little-endian length header followed by a single flags byte
/// (bit 0 = literal tree, bit 1 = 8K dictionary).
/// </summary>
public sealed class ImplodeBuildingBlock : IBuildingBlock {
  private const bool UseLiteralTree = true;
  private const bool Use8KDictionary = true;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Implode";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Implode";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "PKWARE ZIP Implode (method 6) - LZ77 with 4K/8K dictionary and Shannon-Fano coded literals, lengths and distances";
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
    var body = data.Length == 0 ? [] : ImplodeEncoder.Encode(data, UseLiteralTree, Use8KDictionary);
    var output = new byte[5 + body.Length];
    BinaryPrimitives.WriteInt32LittleEndian(output, data.Length);
    output[4] = (byte)((UseLiteralTree ? 1 : 0) | (Use8KDictionary ? 2 : 0));
    body.CopyTo(output.AsSpan(5));
    return output;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 5) throw new InvalidDataException("Implode: input smaller than 5-byte header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0) throw new InvalidDataException("Implode: negative decompressed size.");
    var flags = data[4];
    return size == 0 ? [] : ImplodeDecoder.Decode(data[5..], size, (flags & 1) != 0, (flags & 2) != 0);
  }
}
