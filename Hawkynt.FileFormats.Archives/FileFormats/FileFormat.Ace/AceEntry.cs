namespace FileFormat.Ace;

/// <summary>
/// Represents a file entry in an ACE archive.
/// </summary>
public sealed class AceEntry {
  /// <summary>Gets or sets the file name.</summary>
  public string FileName { get; set; } = "";

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; set; }

  /// <summary>Gets or sets the CRC-32 of the original data.</summary>
  public uint Crc32 { get; set; }

  /// <summary>Gets or sets the compression type (0=store, 1=ACE 1.0, 2=ACE 2.0).</summary>
  public int CompressionType { get; set; }

  /// <summary>Gets or sets the compression quality/level.</summary>
  public int Quality { get; set; }

  /// <summary>Gets or sets the dictionary bits (10-22).</summary>
  public int DictionaryBits { get; set; }

  /// <summary>Gets or sets the last modification time.</summary>
  public DateTime LastModified { get; set; }

  /// <summary>Gets or sets the file attributes.</summary>
  public uint Attributes { get; set; }

  /// <summary>Gets or sets the file header flags.</summary>
  public ushort Flags { get; set; }

  /// <summary>Gets whether the file is encrypted.</summary>
  public bool IsEncrypted => (this.Flags & AceConstants.FileFlagEncrypted) != 0;

  /// <summary>Gets whether this entry is part of a solid block.</summary>
  public bool IsSolid => (this.Flags & AceConstants.FileFlagSolid) != 0;

  /// <summary>Gets or sets the offset of compressed data in the stream.</summary>
  internal long DataOffset { get; set; }
}
