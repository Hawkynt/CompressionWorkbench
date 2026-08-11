using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Exposes the Quantum algorithm as a benchmarkable building block.
/// Prepends a 4-byte big-endian uncompressed size header for round-trip support.
/// </summary>
/// <remarks>
/// <para>
/// Quantum is the LZ77 plus adaptive arithmetic coding method Microsoft licensed from
/// David Stafford for Cabinet (CAB) archives, alongside MSZIP and LZX. Microsoft never
/// published its bitstream; the only public descriptions are prose reconstructions,
/// chiefly Matthew Russotto's "Quantum compression format"
/// (http://www.russotto.net/quantumcomp.html) and the libmspack documentation
/// (https://www.cabextract.org.uk/libmspack/doc/).
/// </para>
/// <para>
/// What this block implements is the general shape of that method — LZ77 matches whose
/// literals, lengths and distances are coded by an adaptive arithmetic coder driven by
/// several small history-dependent context models — with its own models and slot tables.
/// It is <b>not</b> bit-compatible with Quantum data found in real CAB archives, and
/// guarantees only that its own encoder and decoder agree.
/// </para>
/// </remarks>
public sealed class QuantumBuildingBlock : IBuildingBlock {
  /// <summary>Window level used by this block: 7 selects the maximum 64 KB window.</summary>
  private const int WindowLevel = QuantumConstants.MaxWindowLevel;

  /// <summary>Size of the uncompressed-length header in bytes.</summary>
  private const int HeaderSize = 4;

  /// <inheritdoc/>
  public string Id => "BB_Quantum";
  /// <inheritdoc/>
  public string DisplayName => "Quantum";
  /// <inheritdoc/>
  public string Description => "LZ77 with an adaptive arithmetic coder, after the Quantum method of Microsoft CAB archives";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var compressed = QuantumCompressor.Compress(data, WindowLevel);
    var result = new byte[HeaderSize + compressed.Length];
    BinaryPrimitives.WriteInt32BigEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(HeaderSize));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize)
      return [];

    var originalSize = BinaryPrimitives.ReadInt32BigEndian(data);
    return QuantumDecompressor.Decompress(data[HeaderSize..].ToArray(), originalSize, WindowLevel);
  }
}
