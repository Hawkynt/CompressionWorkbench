namespace FileFormat.FreeArc;

/// <summary>Represents a single file entry within a FreeArc archive.</summary>
public sealed class FreeArcEntry {
  /// <summary>The file name as stored in the archive directory block.</summary>
  public string Name { get; init; } = "";

  /// <summary>The uncompressed size of the file in bytes.</summary>
  public long Size { get; init; }

  /// <summary>The compressed size of the file data in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>The compression method string (e.g. "storing", "lzma", "freearc").</summary>
  public string Method { get; init; } = "storing";

  /// <summary>The byte offset within the data payload where this file's data begins.</summary>
  internal long DataOffset { get; init; }
}
