namespace Compression.Core.Progress;

/// <summary>
/// Interface for reporting compression/decompression progress.
/// </summary>
public interface ICompressionProgress {
  /// <summary>
  /// Reports progress of the current operation.
  /// </summary>
  /// <param name="bytesProcessed">The number of bytes processed so far.</param>
  /// <param name="totalBytes">The total number of bytes, or -1 if unknown.</param>
  void Report(long bytesProcessed, long totalBytes);
}
