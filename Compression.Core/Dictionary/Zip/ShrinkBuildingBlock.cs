using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Exposes PKWARE ZIP <b>Shrink</b> (method 1) as a benchmarkable building block.
/// Shrink is LZW with 9-13 bit variable-width codes plus a partial-clear
/// mechanism (control code 256). The raw <see cref="ShrinkDecoder"/> needs the
/// decompressed size, so the building block prepends a 4-byte little-endian
/// length header (matching the convention used by the other size-bearing blocks).
/// </summary>
public sealed class ShrinkBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Shrink";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Shrink";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "PKWARE ZIP Shrink (method 1) - LZW with 9-13 bit codes and partial dictionary clear";
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
    var body = data.Length == 0 ? [] : ShrinkEncoder.Encode(data);
    var output = new byte[4 + body.Length];
    BinaryPrimitives.WriteInt32LittleEndian(output, data.Length);
    body.CopyTo(output.AsSpan(4));
    return output;
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("Shrink: input smaller than 4-byte header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0) throw new InvalidDataException("Shrink: negative decompressed size.");
    return size == 0 ? [] : ShrinkDecoder.Decode(data[4..], size);
  }
}
