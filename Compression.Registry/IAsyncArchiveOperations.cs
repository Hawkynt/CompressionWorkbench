namespace Compression.Registry;

/// <summary>
/// Optional interface for archive formats that support lazy, asynchronous entry enumeration.
/// Implementations yield entries one at a time, enabling processing of huge archives without
/// materializing the full entry list in memory.
/// </summary>
public interface IAsyncArchiveOperations {

  /// <summary>
  /// Lazily enumerates archive entries as an async stream.
  /// Each entry is yielded as it is discovered, without requiring the full archive to be scanned first.
  /// </summary>
  /// <param name="stream">The archive stream to read from. Must be positioned at the start of the archive.</param>
  /// <param name="password">Optional password for encrypted archives.</param>
  /// <param name="ct">Cancellation token to stop enumeration early.</param>
  /// <returns>An async enumerable of archive entry metadata.</returns>
  IAsyncEnumerable<ArchiveEntryInfo> ListEntriesAsync(Stream stream, string? password, CancellationToken ct = default);
}
