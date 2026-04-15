#pragma warning disable CS1591

using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;

namespace Compression.Analysis;

/// <summary>
/// Analyzes streams without materializing the full file into memory.
/// Reads the first 64KB for magic/header detection, then computes
/// entropy and byte statistics by processing the stream in 64KB chunks.
/// </summary>
public sealed class StreamingAnalyzer {

  private const int HeaderBufferSize = 64 * 1024;
  private const int ChunkSize = 64 * 1024;

  /// <summary>
  /// Result of a streaming analysis operation.
  /// </summary>
  public sealed class StreamingAnalysisResult {

    /// <summary>Total number of bytes analyzed from the stream.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Shannon entropy in bits per byte (0-8), computed from the full stream.</summary>
    public double Entropy { get; init; }

    /// <summary>Mean byte value (0-255) across the full stream.</summary>
    public double Mean { get; init; }

    /// <summary>Chi-square statistic for byte distribution uniformity.</summary>
    public double ChiSquare { get; init; }

    /// <summary>Approximate p-value for the chi-square statistic.</summary>
    public double PValue { get; init; }

    /// <summary>Number of distinct byte values observed.</summary>
    public int UniqueBytesCount { get; init; }

    /// <summary>Byte value frequency distribution (256 entries).</summary>
    public long[] ByteFrequency { get; init; } = [];

    /// <summary>Signature scan results from the header (first 64KB).</summary>
    public List<ScanResult>? Signatures { get; init; }

    /// <summary>Per-chunk entropy profiles for the stream.</summary>
    public List<RegionProfile>? EntropyMap { get; init; }
  }

  private readonly int _maxScanResults;

  /// <summary>
  /// Creates a streaming analyzer.
  /// </summary>
  /// <param name="maxScanResults">Maximum number of signature scan results to return.</param>
  public StreamingAnalyzer(int maxScanResults = 100) {
    _maxScanResults = maxScanResults;
  }

  /// <summary>
  /// Analyzes a stream without reading the entire contents into memory.
  /// Reads the first 64KB for header/magic detection, then processes the rest
  /// in 64KB chunks to accumulate byte frequency statistics and entropy profiles.
  /// </summary>
  /// <param name="input">The stream to analyze. Must be readable.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Streaming analysis result with statistics, signatures, and entropy map.</returns>
  /// <exception cref="ArgumentException">If the stream is not readable.</exception>
  public async Task<StreamingAnalysisResult> AnalyzeStreamAsync(Stream input, CancellationToken ct = default) {
    if (!input.CanRead)
      throw new ArgumentException("Stream must be readable.", nameof(input));

    var freq = new long[256];
    long totalBytes = 0;
    long totalSum = 0;
    var entropyMap = new List<RegionProfile>();

    // Phase 1: Read the first 64KB for header/magic detection
    var headerBuffer = new byte[HeaderBufferSize];
    var headerBytesRead = await ReadFullAsync(input, headerBuffer, ct).ConfigureAwait(false);
    var headerSpan = headerBuffer.AsSpan(0, headerBytesRead);

    // Run signature scanning on header
    List<ScanResult>? signatures = null;
    if (headerBytesRead > 0)
      signatures = SignatureScanner.Scan(headerSpan, _maxScanResults);

    // Accumulate header bytes into frequency table
    AccumulateFrequency(headerSpan, freq, ref totalBytes, ref totalSum);

    // Add header as first entropy map region
    if (headerBytesRead > 0) {
      var headerFreq = BinaryStatistics.ComputeByteFrequency(headerSpan);
      var headerEntropy = BinaryStatistics.ComputeEntropy(headerFreq, headerBytesRead);
      var headerChiSq = BinaryStatistics.ComputeChiSquare(headerFreq, headerBytesRead);
      var headerMean = BinaryStatistics.ComputeMean(headerSpan);
      entropyMap.Add(new RegionProfile(0, headerBytesRead, headerEntropy, headerChiSq, headerMean, EntropyMap.Classify(headerEntropy)));
    }

    // Phase 2: Read remaining data in chunks
    var chunkBuffer = new byte[ChunkSize];
    long streamOffset = headerBytesRead;

    while (true) {
      ct.ThrowIfCancellationRequested();

      var bytesRead = await ReadFullAsync(input, chunkBuffer, ct).ConfigureAwait(false);
      if (bytesRead == 0)
        break;

      var chunkSpan = chunkBuffer.AsSpan(0, bytesRead);
      AccumulateFrequency(chunkSpan, freq, ref totalBytes, ref totalSum);

      // Compute per-chunk entropy profile
      var chunkFreq = BinaryStatistics.ComputeByteFrequency(chunkSpan);
      var chunkEntropy = BinaryStatistics.ComputeEntropy(chunkFreq, bytesRead);
      var chunkChiSq = BinaryStatistics.ComputeChiSquare(chunkFreq, bytesRead);
      var chunkMean = BinaryStatistics.ComputeMean(chunkSpan);
      entropyMap.Add(new RegionProfile(streamOffset, bytesRead, chunkEntropy, chunkChiSq, chunkMean, EntropyMap.Classify(chunkEntropy)));

      streamOffset += bytesRead;
    }

    // Phase 3: Compute overall statistics from accumulated frequencies
    var overallEntropy = BinaryStatistics.ComputeEntropy(freq, totalBytes <= int.MaxValue ? (int)totalBytes : int.MaxValue);
    var overallMean = totalBytes > 0 ? (double)totalSum / totalBytes : 0;
    var overallChiSq = ComputeChiSquareLong(freq, totalBytes);
    var pValue = BinaryStatistics.ChiSquarePValue(overallChiSq, 255);

    var uniqueCount = 0;
    foreach (var f in freq)
      if (f > 0)
        uniqueCount++;

    return new StreamingAnalysisResult {
      TotalBytes = totalBytes,
      Entropy = overallEntropy,
      Mean = overallMean,
      ChiSquare = overallChiSq,
      PValue = pValue,
      UniqueBytesCount = uniqueCount,
      ByteFrequency = freq,
      Signatures = signatures,
      EntropyMap = entropyMap,
    };
  }

  private static void AccumulateFrequency(ReadOnlySpan<byte> data, long[] freq, ref long totalBytes, ref long totalSum) {
    foreach (var b in data) {
      freq[b]++;
      totalSum += b;
    }
    totalBytes += data.Length;
  }

  private static double ComputeChiSquareLong(long[] freq, long total) {
    if (total == 0) return 0;
    var expected = (double)total / 256.0;
    var chiSq = 0.0;
    foreach (var f in freq) {
      var diff = f - expected;
      chiSq += diff * diff / expected;
    }
    return chiSq;
  }

  /// <summary>
  /// Reads as many bytes as possible into the buffer, handling partial reads.
  /// Returns the total number of bytes actually read.
  /// </summary>
  private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
      if (bytesRead == 0)
        break;
      totalRead += bytesRead;
    }
    return totalRead;
  }
}
