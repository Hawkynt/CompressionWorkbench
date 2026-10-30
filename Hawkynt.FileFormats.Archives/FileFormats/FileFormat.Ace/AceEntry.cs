namespace FileFormat.Ace;

/// <summary>One member of an ACE archive, as its file header describes it.</summary>
public sealed class AceEntry {
  /// <summary>Member path with '/' separators (ACE stores '\').</summary>
  public string FileName { get; init; } = "";

  public long CompressedSize { get; init; }

  public long OriginalSize { get; init; }

  /// <summary>ACE CRC-32 (not inverted) of the uncompressed data.</summary>
  public uint Crc32 { get; init; }

  /// <summary>0 stored, 1 ACE 1.0 LZ77, 2 ACE 2.0 blocked.</summary>
  public int CompressionType { get; init; }

  public int Quality { get; init; }

  public int DictionaryBits { get; init; }

  public DateTime LastModified { get; init; }

  public uint Attributes { get; init; }

  public ushort Flags { get; init; }

  public string? Comment { get; init; }

  public bool IsDirectory => (this.Attributes & AceConstants.AttributeDirectory) != 0;

  public bool IsEncrypted => (this.Flags & AceConstants.FileFlagPassword) != 0;

  public bool IsSolid => (this.Flags & AceConstants.FileFlagSolid) != 0;

  /// <summary>Whether the member is split across volumes of a multi-volume set.</summary>
  public bool IsContinued => (this.Flags & (AceConstants.FileFlagContinuedFromPrevious | AceConstants.FileFlagContinuedInNext)) != 0;

  internal int Index { get; init; }

  internal long DataOffset { get; init; }
}
