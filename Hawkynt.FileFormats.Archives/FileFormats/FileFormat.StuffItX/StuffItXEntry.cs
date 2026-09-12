namespace FileFormat.StuffItX;

/// <summary>Represents a single entry in a StuffIt X (.sitx) archive.</summary>
public sealed class StuffItXEntry {
  /// <summary>Gets the bare file or directory name.</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the full path of the entry within the archive (slash-separated).</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Gets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the uncompressed size of the entry data in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Gets the compressed size of the entry data in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets a display name for the compression method used.</summary>
  public string Method { get; init; } = "";

  // ── Internal positioning data used by StuffItXReader.Extract ─────────────────

  /// <summary>Byte offset within the archive stream where compressed data begins.</summary>
  internal long DataOffset { get; init; }

  /// <summary>Compression method code (0=stored, 5=deflate, etc.).</summary>
  internal int MethodCode { get; init; }
}
