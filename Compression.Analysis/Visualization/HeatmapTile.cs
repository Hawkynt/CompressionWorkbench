#pragma warning disable CS1591

namespace Compression.Analysis.Visualization;

/// <summary>
/// One tile in a 16x16 heatmap grid. Represents a region of a file with
/// pre-computed statistics for fast rendering.
/// </summary>
public sealed class HeatmapTile {
  /// <summary>Byte offset from start of file.</summary>
  public long Offset { get; init; }

  /// <summary>Number of bytes this tile covers.</summary>
  public long Length { get; init; }

  /// <summary>Shannon entropy 0-8 (0=constant, 8=random).</summary>
  public double Entropy { get; init; }

  /// <summary>Most frequent byte value in this region.</summary>
  public byte DominantByte { get; init; }

  /// <summary>Fraction of bytes that are zero (0.0-1.0).</summary>
  public double ZeroFraction { get; init; }

  /// <summary>Fraction of bytes that are printable ASCII (0.0-1.0).</summary>
  public double AsciiFraction { get; init; }

  /// <summary>Format signature detected at or near the start of this region, if any.</summary>
  public string? DetectedFormat { get; init; }

  /// <summary>Row (0-15) in the 16x16 grid.</summary>
  public int Row { get; init; }

  /// <summary>Column (0-15) in the 16x16 grid.</summary>
  public int Col { get; init; }

  /// <summary>Classification for color mapping.</summary>
  public TileClass Classification => this switch {
    { Length: 0 } => TileClass.Empty,
    { ZeroFraction: > 0.99 } => TileClass.Zeros,
    { DetectedFormat: not null } => TileClass.KnownFormat,
    { Entropy: < 1.0 } => TileClass.VeryLowEntropy,
    { Entropy: < 3.5 } => TileClass.LowEntropy,
    { Entropy: < 5.5 } => TileClass.MediumEntropy,
    { Entropy: < 7.0 } => TileClass.HighEntropy,
    { Entropy: < 7.8 } => TileClass.Compressed,
    _ => TileClass.Encrypted
  };
}

/// <summary>Tile classification for color mapping.</summary>
public enum TileClass {
  Empty,          // Gray
  Zeros,          // Dark blue
  KnownFormat,    // Purple
  VeryLowEntropy, // Blue
  LowEntropy,     // Cyan
  MediumEntropy,  // Green
  HighEntropy,    // Yellow
  Compressed,     // Orange
  Encrypted       // Red
}
