using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Exposes Quantum as a benchmarkable building block.
/// </summary>
/// <remarks>
/// <para>Quantum is the LZ77-plus-arithmetic-coding method Microsoft licensed from
/// David Stafford for cabinets, alongside MSZIP and LZX. Microsoft never published its
/// bitstream. What is here was derived by measurement against libmspack, the reference
/// reader, and writes streams that libmspack accepts; the derivation is in
/// <c>docs/QUANTUM-ON-DISK.md</c>.</para>
///
/// <para>A cabinet folder can only carry so much before one of its models would sort
/// itself, and that sort is the one part of the format still unmeasured. The
/// compressor therefore closes a folder before that point, and this block frames the
/// resulting folders one after another so it can hand back a single buffer.</para>
/// </remarks>
public sealed class QuantumBuildingBlock : IBuildingBlock {

  /// <summary>The window this block names: 32 KB, well within what a cabinet allows.</summary>
  private const int WindowBits = 15;

  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Quantum";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Quantum";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 with an adaptive arithmetic coder, as Microsoft CAB archives carry it";

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
    if (data.Length == 0)
      return [];

    using var buffer = new MemoryStream();
    foreach (var folder in QuantumCompressor.Compress(data, WindowBits)) {
      var header = new byte[8];
      BinaryPrimitives.WriteInt32BigEndian(header, folder.Consumed);
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), folder.Compressed.Length);
      buffer.Write(header);
      buffer.Write(folder.Compressed);
    }

    return buffer.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var buffer = new MemoryStream();
    var offset = 0;
    while (offset + 8 <= data.Length) {
      var plainLength = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
      var codedLength = BinaryPrimitives.ReadInt32BigEndian(data[(offset + 4)..]);
      offset += 8;
      if (plainLength < 0 || codedLength < 0 || offset + codedLength > data.Length)
        break;

      buffer.Write(QuantumDecompressor.Decompress(data.Slice(offset, codedLength).ToArray(), plainLength, WindowBits));
      offset += codedLength;
    }

    return buffer.ToArray();
  }
}
