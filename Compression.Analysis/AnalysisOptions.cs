namespace Compression.Analysis;

/// <summary>
/// Configuration for binary analysis operations.
/// </summary>
public sealed class AnalysisOptions {
  /// <summary>Maximum chain reconstruction depth (default 10).</summary>
  public int MaxDepth { get; init; } = 10;

  /// <summary>Per-trial timeout in milliseconds (default 200).</summary>
  public int PerTrialTimeoutMs { get; init; } = 200;

  /// <summary>Entropy map window size in bytes (default 256).</summary>
  public int WindowSize { get; init; } = 256;

  /// <summary>Start analysis at this offset (default 0).</summary>
  public long Offset { get; init; }

  /// <summary>Analyze only this many bytes (default 0 = all).</summary>
  public long Length { get; init; }

  /// <summary>Maximum scan results to return (default 100).</summary>
  public int MaxScanResults { get; init; } = 100;

  /// <summary>Enable deep signature scanning (default false).</summary>
  public bool DeepScan { get; init; }

  /// <summary>Enable algorithm fingerprinting (default false).</summary>
  public bool Fingerprint { get; init; }

  /// <summary>Enable trial decompression (default false).</summary>
  public bool Trial { get; init; }

  /// <summary>Enable entropy map (default false).</summary>
  public bool EntropyMap { get; init; }

  /// <summary>Use CUSUM boundary detection for entropy mapping instead of adaptive windowing (default false).</summary>
  public bool BoundaryDetection { get; init; }

  /// <summary>Enable chain reconstruction (default false).</summary>
  public bool Chain { get; init; }

  /// <summary>Enable all analysis modes.</summary>
  public bool All { get; init; }
}
