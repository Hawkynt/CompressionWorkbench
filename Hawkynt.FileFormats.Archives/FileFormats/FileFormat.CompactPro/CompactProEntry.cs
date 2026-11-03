namespace FileFormat.CompactPro;

/// <summary>One file or folder of a Compact Pro archive's directory.</summary>
public sealed class CompactProEntry {
  /// <summary>Path with '/' separators (folders included).</summary>
  public string FileName { get; init; } = "";

  public bool IsDirectory { get; init; }

  public uint FileOffset { get; init; }

  public uint DataForkSize { get; init; }
  public uint ResourceForkSize { get; init; }
  public uint DataForkCompressedSize { get; init; }
  public uint ResourceForkCompressedSize { get; init; }

  /// <summary>Stored CRC: the CRC-32 register over resource then data fork, not inverted.</summary>
  public uint Crc { get; init; }

  /// <summary>Bit 0 encrypted, bit 1 resource fork LZH, bit 2 data fork LZH.</summary>
  public ushort Flags { get; init; }

  public uint FileType { get; init; }
  public uint FileCreator { get; init; }
  public ushort FinderFlags { get; init; }
  public DateTime CreatedDate { get; init; }
  public DateTime ModifiedDate { get; init; }
  public byte Volume { get; init; }

  public bool IsEncrypted => (this.Flags & 1) != 0;
  public bool ResourceForkLzh => (this.Flags & 2) != 0;
  public bool DataForkLzh => (this.Flags & 4) != 0;

  /// <summary>Offset of the data fork's packed bytes (it follows the resource fork).</summary>
  public long DataOffset => this.FileOffset + (long)this.ResourceForkCompressedSize;

  /// <summary>Offset of the resource fork's packed bytes.</summary>
  public long ResourceOffset => this.FileOffset;
}
