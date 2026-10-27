namespace FileFormat.PackIt;

/// <summary>Represents a file entry in a PackIt (.pit) archive.</summary>
public sealed class PackItEntry {
  /// <summary>Gets the filename (up to 63 characters, Mac Roman / Latin-1 encoded).</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>Gets the Mac four-character file type code (e.g. "TEXT").</summary>
  public string FileType { get; init; } = string.Empty;

  /// <summary>Gets the Mac four-character creator code (e.g. "CWIE").</summary>
  public string Creator { get; init; } = string.Empty;

  /// <summary>Gets the Finder flags.</summary>
  public ushort FinderFlags { get; init; }

  /// <summary>Gets the uncompressed data fork size in bytes.</summary>
  public long DataForkSize { get; init; }

  /// <summary>Gets the uncompressed resource fork size in bytes.</summary>
  public long ResourceForkSize { get; init; }

  /// <summary>Gets the creation time, or <see langword="null"/> when the header holds zero.</summary>
  public DateTime? Created { get; init; }

  /// <summary>Gets the modification time, or <see langword="null"/> when the header holds zero.</summary>
  public DateTime? Modified { get; init; }

  /// <summary>Gets whether this entry is Huffman-compressed ("PMa4", "PMa5", "PMa6").</summary>
  public bool IsCompressed { get; init; }

  /// <summary>Gets whether this entry is password-encrypted ("PMa5" XOR, "PMa6" DES).</summary>
  public bool IsEncrypted { get; init; }

  /// <summary>Gets whether the header matched its CRC-16 (always <see langword="true"/> for encrypted entries, which cannot be checked).</summary>
  public bool HeaderCrcValid { get; init; } = true;

  /// <summary>
  /// Gets the absolute offset where the data fork begins for a stored entry; for a compressed entry,
  /// the offset of its Huffman bit stream (the byte after the magic).
  /// </summary>
  public long DataOffset { get; init; }

  /// <summary>Gets the absolute offset of the entry's magic.</summary>
  public long EntryOffset { get; init; }

  /// <summary>Gets the number of bytes the entry occupies, magic included.</summary>
  public long EntryLength { get; init; }
}
