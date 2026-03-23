using Compression.Analysis.ChainReconstruction;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;
using Compression.Analysis.TrialDecompression;

namespace Compression.Analysis;

/// <summary>
/// Top-level result combining all analysis subsystems.
/// </summary>
public sealed class AnalysisResult {
  /// <summary>Overall statistics for the analyzed data.</summary>
  public BinaryStatistics.StatisticsResult? Statistics { get; set; }

  /// <summary>Signature scan results (format magic bytes found).</summary>
  public List<ScanResult>? Signatures { get; set; }

  /// <summary>Algorithm fingerprinting results.</summary>
  public List<FingerprintResult>? Fingerprints { get; set; }

  /// <summary>Entropy map (per-region profiles).</summary>
  public List<RegionProfile>? EntropyMap { get; set; }

  /// <summary>Trial decompression results.</summary>
  public List<DecompressionAttempt>? TrialResults { get; set; }

  /// <summary>Reconstructed compression chain.</summary>
  public CompressionChain? Chain { get; set; }
}
