using Compression.Analysis.ChainReconstruction;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;
using Compression.Analysis.TrialDecompression;
using FormatProber = Compression.Analysis.Scanning.FormatProber;

namespace Compression.Analysis;

/// <summary>
/// Facade that orchestrates all analysis subsystems: statistics, scanning,
/// fingerprinting, trial decompression, and chain reconstruction.
/// </summary>
public sealed class BinaryAnalyzer {

  private readonly AnalysisOptions _options;

  /// <summary>Creates an analyzer with the given options.</summary>
  public BinaryAnalyzer(AnalysisOptions? options = null) {
    _options = options ?? new AnalysisOptions { All = true };
  }

  /// <summary>
  /// Runs all enabled analyses on the data.
  /// </summary>
  public AnalysisResult Analyze(ReadOnlySpan<byte> data) {
    // Apply offset/length slicing
    var slice = data;
    if (_options.Offset > 0 && _options.Offset < data.Length)
      slice = data[(int)_options.Offset..];
    if (_options.Length > 0 && _options.Length < slice.Length)
      slice = slice[..(int)_options.Length];

    var result = new AnalysisResult {
      Statistics = BinaryStatistics.Analyze(slice)
    };

    if (_options.DeepScan || _options.All)
      result.Signatures = SignatureScanner.Scan(slice, _options.MaxScanResults);

    if (_options.Fingerprint || _options.All)
      result.Fingerprints = new AlgorithmFingerprinter().Analyze(slice);

    if (_options.EntropyMap || _options.All)
      result.EntropyMap = Statistics.EntropyMap.Profile(slice, _options.WindowSize, _options.BoundaryDetection);

    if (_options.Trial || _options.All)
      result.TrialResults = new TrialDecompressor(perTrialTimeoutMs: _options.PerTrialTimeoutMs).TryAll(slice);

    if (_options.Chain || _options.All)
      result.Chain = new ChainReconstructor(_options.MaxDepth, _options.PerTrialTimeoutMs).Reconstruct(slice);

    if ((_options.Probe || _options.All) && result.Signatures is { Count: > 0 }) {
      var prober = new FormatProber(_options.ProbeMaxLevel, _options.ProbeIntegrityTimeoutMs);
      result.ProbeResults = prober.Probe(slice, result.Signatures);
    }

    return result;
  }
}
