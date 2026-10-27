namespace FileFormat.PackIt;

/// <summary>Represents a file entry in a PackIt (.pit) archive.</summary>
public sealed class PackItEntry {
  /// <summary>Gets the filename (up to 63 characters, Mac Roman / Latin-1 encoded).</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>Gets the Mac four-character file type code (e.g. "TEXT").</summary>
  public string FileType { get; init; } = string.Empty;

  /// <summary>Gets the Mac four-character creator code (e.g. "CWIE").</summary>
  public string Creator { get; init; } = string.Empty;

  /// <summary>Gets the uncompressed data fork size in bytes.</summary>
  public long DataForkSize { get; init; }

  /// <summary>Gets the uncompressed resource fork size in bytes.</summary>
  public long ResourceForkSize { get; init; }

  /// <summary>Gets whether this entry uses Huffman compression ("PMa4").</summary>
  public bool IsCompressed { get; init; }

  /// <summary>Gets the absolute byte offset where the data fork begins in the source stream.</summary>
  public long DataOffset { get; init; }
}
