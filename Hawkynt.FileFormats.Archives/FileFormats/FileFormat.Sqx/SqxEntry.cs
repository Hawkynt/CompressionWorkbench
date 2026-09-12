namespace FileFormat.Sqx;

/// <summary>
/// Represents a file entry in an SQX archive.
/// </summary>
public sealed class SqxEntry {
  /// <summary>Gets or sets the file name.</summary>
  public string FileName { get; set; } = "";

  /// <summary>Gets or sets the compressed size in bytes.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; set; }

  /// <summary>Gets or sets the CRC-32 of the original data.</summary>
  public uint Crc32 { get; set; }

  /// <summary>Gets or sets the compression method.</summary>
  public byte Method { get; set; }

  /// <summary>Gets or sets the last modification time.</summary>
  public DateTime LastModified { get; set; }

  /// <summary>Gets or sets the file attributes.</summary>
  public uint Attributes { get; set; }

  /// <summary>Gets or sets the block flags.</summary>
  public ushort Flags { get; set; }

  /// <summary>Gets or sets the archive version required.</summary>
  public byte ArchiveVersion { get; set; } = SqxConstants.ArcVersion11;

  /// <summary>Gets or sets the compressor flags byte.</summary>
  public byte CompFlags { get; set; }

  /// <summary>Gets or sets the extra compressor flags (BCJ/delta).</summary>
  public ushort ExtraCompFlags { get; set; }

  /// <summary>Gets whether this entry is encrypted.</summary>
  public bool IsEncrypted => (this.Flags & SqxConstants.FileFlagEncrypted) != 0;

  /// <summary>Gets whether this entry uses solid compression.</summary>
  public bool IsSolid => (this.Flags & SqxConstants.FileFlagSolid) != 0;

  /// <summary>Gets the dictionary size for this entry.</summary>
  public int DictionarySize => SqxConstants.GetDictSize(this.Flags);

  /// <summary>Gets or sets the offset of compressed data in the stream.</summary>
  internal long DataOffset { get; set; }
}
