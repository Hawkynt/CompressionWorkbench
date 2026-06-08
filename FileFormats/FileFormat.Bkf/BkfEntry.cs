#pragma warning disable CS1591
namespace FileFormat.Bkf;

/// <summary>
/// A single entry surfaced from a Microsoft NTBackup (.bkf) Microsoft Tape Format
/// (MTF) stream. Currently maps to FILE/DIRB DBLKs that carry a payload (DATA
/// streams of type STAN — Standard). Empty/zero-byte placeholders for
/// directories are reported with <see cref="IsDirectory"/> = <c>true</c>.
/// </summary>
public sealed class BkfEntry {
  /// <summary>Display name (relative path, forward slashes).</summary>
  public string Name { get; init; } = "";

  /// <summary>Uncompressed payload length in bytes.</summary>
  public long Size { get; init; }

  /// <summary>True when the entry represents a DIRB (directory) rather than a FILE.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Absolute stream offset where the entry's STAN data starts.</summary>
  internal long DataOffset { get; init; }

  /// <summary>STAN data stream length on disk (== <see cref="Size"/> for uncompressed).</summary>
  internal long DataLength { get; init; }

  /// <summary>
  /// True when the entry's DATA stream was flagged as compressed in the stream
  /// header. MTF does not specify the compression algorithm in-band — most
  /// real-world ntbackup.exe writes are uncompressed STAN.
  /// </summary>
  internal bool IsCompressed { get; init; }
}
