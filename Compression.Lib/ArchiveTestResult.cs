namespace Compression.Lib;

/// <summary>
/// The outcome of an integrity check, distinguishing a damaged archive from one this library has
/// no verifier for.
/// </summary>
public enum ArchiveTestResult {
  /// <summary>Every entry read back without error.</summary>
  Ok,

  /// <summary>The archive is recognised but does not read back — truncated, corrupt, or wrongly keyed.</summary>
  Corrupt,

  /// <summary>
  /// Nothing is known to be wrong with the file; there is simply no verifier for it. The format is
  /// unrecognised, has no registered descriptor, or uses a variant the reader does not support.
  /// </summary>
  NotTestable,
}
