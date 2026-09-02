using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;
using Compression.Registry;

namespace Compression.Core.Entropy.Fpaq;

/// <summary>
/// Exposes FPAQ0-style adaptive order-0 arithmetic compression as a benchmarkable building block.
/// Each byte is coded MSB-first with one binary probability model per prefix of the current byte.
/// </summary>
/// <remarks>
/// The model starts every branch at one zero and one one, updates after each coded bit, and
/// halves large counts to keep the arithmetic coder's probability calculation bounded. This is
/// the compact order-0 modelling scheme described for Matt Mahoney's FPAQ family; the outer
/// four-byte original-length field is CompressionWorkbench framing rather than an FPAQ archive.
/// Reference: https://mattmahoney.net/dc/ — FPAQ section.
/// </remarks>
public sealed class Fpaq0BuildingBlock : IBuildingBlock {
  private const int ContextCount = 256;
  private const int RescaleAt = 32768;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Fpaq0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "FPAQ0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Adaptive order-0 binary arithmetic compression";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, data.Length);
    output.Write(lengthBytes);

    if (data.IsEmpty)
      return output.ToArray();

    var zeroCounts = CreateCounts();
    var oneCounts = CreateCounts();
    var encoder = new ArithmeticEncoder(output);

    foreach (var value in data) {
      var context = 1;
      for (var shift = 7; shift >= 0; --shift) {
        var bit = value >> shift & 1;
        encoder.EncodeBit(bit, ProbabilityOfZero(zeroCounts[context], oneCounts[context]));
        UpdateModel(zeroCounts, oneCounts, context, bit);
        context = context << 1 | bit;
      }
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < sizeof(int))
      throw new InvalidDataException("FPAQ0 stream is missing its original-length header.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength < 0)
      throw new InvalidDataException("FPAQ0 stream declares a negative original length.");
    if (originalLength == 0) {
      if (data.Length != sizeof(int))
        throw new InvalidDataException("FPAQ0 empty stream contains trailing payload data.");
      return [];
    }
    if (data.Length == sizeof(int))
      throw new InvalidDataException("FPAQ0 stream has no arithmetic-coded payload.");

    var result = new byte[originalLength];
    var zeroCounts = CreateCounts();
    var oneCounts = CreateCounts();
    using var input = new MemoryStream(data[sizeof(int)..].ToArray(), writable: false);
    var decoder = new ArithmeticDecoder(input);

    for (var index = 0; index < result.Length; ++index) {
      var context = 1;
      var value = 0;
      for (var bitIndex = 0; bitIndex < 8; ++bitIndex) {
        var bit = decoder.DecodeBit(ProbabilityOfZero(zeroCounts[context], oneCounts[context]));
        UpdateModel(zeroCounts, oneCounts, context, bit);
        value = value << 1 | bit;
        context = context << 1 | bit;
      }
      result[index] = (byte)value;
    }

    return result;
  }

  private static int[] CreateCounts() {
    var result = new int[ContextCount];
    Array.Fill(result, 1);
    return result;
  }

  private static int ProbabilityOfZero(int zeroCount, int oneCount) {
    var probability = (int)(((long)zeroCount << 16) / (zeroCount + oneCount));
    return Math.Clamp(probability, 1, ushort.MaxValue);
  }

  private static void UpdateModel(int[] zeroCounts, int[] oneCounts, int context, int bit) {
    if (bit == 0)
      ++zeroCounts[context];
    else
      ++oneCounts[context];

    if (zeroCounts[context] + oneCounts[context] < RescaleAt)
      return;

    zeroCounts[context] = zeroCounts[context] + 1 >> 1;
    oneCounts[context] = oneCounts[context] + 1 >> 1;
  }
}
