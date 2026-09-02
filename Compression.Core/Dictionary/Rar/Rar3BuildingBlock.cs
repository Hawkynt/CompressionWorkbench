using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Exposes the classic RAR compression algorithm (the RAR3/RAR4-era method) as a
/// benchmarkable building block. Prepends a 4-byte LE uncompressed size header
/// for round-trip support.
/// </summary>
/// <remarks>
/// <para>
/// This is the compression method introduced with RAR 3.x and carried through
/// RAR 4.x: LZ77 matches with four repeat-offset slots, coded through four
/// adaptive Huffman tables (main 299, distance 60, low-distance 17, repeat-length 28)
/// whose code lengths are delta-coded between blocks. It is a different algorithm
/// from the RAR5 method exposed by <see cref="RarBuildingBlock"/>.
/// </para>
/// <para>
/// The RAR3 bitstream also admits a PPMd (PPMII variant H) mode and the VM-based
/// data filters; <see cref="Rar3Decoder"/> handles both on the decode side, but
/// only <see cref="Rar3Encoder"/>'s LZ+Huffman mode has an encoder, so this block
/// is scoped to exactly that pair. The RAR1 and RAR2 methods
/// (<see cref="Rar1Decoder"/>, <see cref="Rar2Decoder"/>) are decode-only and are
/// therefore not exposed as building blocks.
/// </para>
/// </remarks>
public sealed class Rar3BuildingBlock : IBuildingBlock {
  /// <summary>Window size as log2. 22 = 4 MB, the RAR3 maximum.</summary>
  private const int WindowBits = 22;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Rar3";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "RAR3 (classic)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Classic RAR LZ+Huffman compression from RAR 3.x/4.x archives (LZ mode only, no PPMd)";
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
    var encoder = new Rar3Encoder(WindowBits);
    var compressed = encoder.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var decoder = new Rar3Decoder();
    return decoder.Decompress(data[4..], originalSize, WindowBits);
  }
}
