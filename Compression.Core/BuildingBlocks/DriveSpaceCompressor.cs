using Compression.Registry;

namespace Compression.Core.BuildingBlocks;

/// <summary>
/// Clean-room port of the Microsoft DriveSpace (MS-DOS 6.22) "LZ" compression
/// algorithm used by the DVRS CVF format.
/// <para>
/// DriveSpace uses the same token grammar as <see cref="DoubleSpaceCompressor"/>
/// but doubles the sliding-window size to 8 KiB, enabling the class-3 distance
/// code (4417..8192 effective range). See <see cref="DoubleSpaceCompressor"/>
/// for the detailed bit-stream layout.
/// </para>
/// <para>
/// The MS-DOS 7 (Win 95 OSR2) DriveSpace 3.0 variant uses a different
/// block-level compression engine and is NOT produced by this building block.
/// </para>
/// </summary>
public sealed class DriveSpaceCompressor : IBuildingBlock {

  /// <summary>Sliding-window size for DriveSpace 6.22 (DVRS) — 8 KiB.</summary>
  internal const int DefaultMaxDistance = 8192;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_DriveSpace";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DriveSpace LZ";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Microsoft DriveSpace (DVRS) LZ77 — DoubleSpace grammar with 8 KiB window";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DoubleSpaceCompressor.CompressCore(data, DefaultMaxDistance);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DoubleSpaceCompressor.DecompressCore(data);
}
