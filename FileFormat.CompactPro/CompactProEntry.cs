namespace FileFormat.CompactPro;

/// <summary>
/// Represents a single entry in a Compact Pro (.cpt) archive.
/// </summary>
public sealed class CompactProEntry {
  /// <summary>Gets or sets the filename (up to 63 characters).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets or sets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets or sets the uncompressed data fork size in bytes.</summary>
  public uint DataForkSize { get; init; }

  /// <summary>Gets or sets the uncompressed resource fork size in bytes.</summary>
  public uint ResourceForkSize { get; init; }

  /// <summary>Gets or sets the compressed data fork size in bytes.</summary>
  public uint DataForkCompressedSize { get; init; }

  /// <summary>Gets or sets the compressed resource fork size in bytes.</summary>
  public uint ResourceForkCompressedSize { get; init; }

  /// <summary>Gets or sets the compression method for the data fork.</summary>
  public byte DataForkMethod { get; init; }

  /// <summary>Gets or sets the compression method for the resource fork.</summary>
  public byte ResourceForkMethod { get; init; }

  /// <summary>Gets or sets the CRC-16 of the decompressed data fork.</summary>
  public ushort DataForkCrc { get; init; }

  /// <summary>Gets or sets the CRC-16 of the decompressed resource fork.</summary>
  public ushort ResourceForkCrc { get; init; }

  /// <summary>Gets or sets the Mac four-character file type code (e.g. 0x54455854 for 'TEXT').</summary>
  public uint FileType { get; init; }

  /// <summary>Gets or sets the Mac four-character creator code.</summary>
  public uint FileCreator { get; init; }

  /// <summary>Gets or sets the creation date.</summary>
  public DateTime CreatedDate { get; init; }

  /// <summary>Gets or sets the modification date.</summary>
  public DateTime ModifiedDate { get; init; }

  /// <summary>Internal: byte offset of the data fork compressed data in the stream.</summary>
  internal long DataOffset { get; init; }

  /// <summary>Internal: byte offset of the resource fork compressed data in the stream.</summary>
  internal long ResourceOffset { get; init; }
}
