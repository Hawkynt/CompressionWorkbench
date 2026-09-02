using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Golomb/Rice coding with a pinned parameter — the fixed-M bit-stream profile.
/// </summary>
/// <remarks>
/// <para>
/// The value coding is exactly the one <see cref="GolombBuildingBlock"/> uses: a unary
/// quotient (<c>q</c> one-bits followed by a zero-bit) then a truncated-binary remainder,
/// both written most-significant-bit first. What this block pins is the surrounding policy —
/// <see cref="GolombProfile.FixedParameter"/> with <c>M = 2</c>, and an LEB128 element count
/// instead of the four-byte little-endian one.
/// </para>
/// <para>
/// A constant M is the classic Golomb formulation: it is what a codec uses when the source's
/// geometric parameter is known up front (or agreed out of band) rather than measured per
/// buffer, and it removes the encoder's mean-scanning pass. Because M is a power of two here
/// the truncated-binary remainder degenerates to plain Rice coding with <c>k = 1</c>, which is
/// optimal for sources whose values are overwhelmingly 0 and 1 — residual and run-length
/// streams, for instance. On sources with a large mean it is far worse than the adaptive
/// default, since the unary prefix grows linearly with the value.
/// </para>
/// <para>
/// Reference: S. W. Golomb, "Run-length encodings", IEEE Transactions on Information Theory
/// 12(3), 1966; R. F. Rice, "Some practical universal noiseless coding techniques",
/// JPL Publication 79-22, 1979.
/// </para>
/// </remarks>
public sealed class GolombFixedMBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_GolombFixedM";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Golomb/Rice (fixed M=2)";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Golomb/Rice with a constant parameter M=2 and a varint element count — Rice k=1 for zero-heavy residuals";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <summary>The pinned Golomb parameter.</summary>
  private const int Parameter = 2;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data)
    => GolombBuildingBlock.Compress(data, GolombProfile.FixedParameter, Parameter);

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data)
    => GolombBuildingBlock.Decompress(data, GolombProfile.FixedParameter);
}
