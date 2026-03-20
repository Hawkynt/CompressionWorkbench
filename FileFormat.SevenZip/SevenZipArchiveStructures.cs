namespace FileFormat.SevenZip;

/// <summary>
/// Represents a folder (solid block) in a 7z archive, containing one or more coders.
/// </summary>
internal sealed class SevenZipFolder {
  /// <summary>Gets the list of coders in this folder.</summary>
  public List<SevenZipCoder> Coders { get; } = [];

  /// <summary>Gets the list of bind pairs connecting coder streams.</summary>
  public List<(int InIndex, int OutIndex)> BindPairs { get; } = [];

  /// <summary>Gets or sets the unpack sizes for each coder output stream.</summary>
  public long[] UnpackSizes { get; set; } = [];

  /// <summary>Gets or sets the CRC-32 of the final uncompressed data.</summary>
  public uint? UnpackCrc { get; set; }
}

/// <summary>
/// Represents a coder (compression method) within a folder.
/// </summary>
internal sealed class SevenZipCoder {
  /// <summary>Gets or sets the codec identifier bytes.</summary>
  public byte[] CodecId { get; set; } = [];

  /// <summary>Gets or sets the number of input streams.</summary>
  public int NumInStreams { get; set; } = 1;

  /// <summary>Gets or sets the number of output streams.</summary>
  public int NumOutStreams { get; set; } = 1;

  /// <summary>Gets or sets the coder properties (codec-specific configuration).</summary>
  public byte[]? Properties { get; set; }
}

/// <summary>
/// Describes the packed (compressed) data location and sizes within a 7z archive.
/// </summary>
internal sealed class SevenZipPackInfo {
  /// <summary>Gets or sets the position of packed data relative to the end of the signature header.</summary>
  public long PackPos { get; set; }

  /// <summary>Gets or sets the packed sizes for each pack stream.</summary>
  public long[] PackSizes { get; set; } = [];

  /// <summary>Gets or sets optional CRC-32 values for each pack stream.</summary>
  public uint?[] PackCrcs { get; set; } = [];
}

/// <summary>
/// Tracks per-file unpack sizes and digests within solid folders.
/// </summary>
internal sealed class SevenZipSubStreamsInfo {
  /// <summary>Gets or sets the number of unpack streams (files) per folder.</summary>
  public int[] NumUnpackStreams { get; set; } = [];

  /// <summary>Gets or sets the unpack size for each individual file within a folder.</summary>
  public long[] UnpackSizes { get; set; } = [];

  /// <summary>Gets or sets the CRC-32 digests for each file.</summary>
  public uint[] Digests { get; set; } = [];
}

/// <summary>
/// Holds file metadata for a single entry in the 7z archive header.
/// </summary>
internal sealed class SevenZipFileInfo {
  /// <summary>Gets or sets the file name.</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets whether this entry is an empty stream (no data).</summary>
  public bool IsEmptyStream { get; set; }

  /// <summary>Gets or sets whether this entry is an empty file (vs. directory).</summary>
  public bool IsEmptyFile { get; set; }

  /// <summary>Gets or sets the file creation time.</summary>
  public DateTime? CreationTime { get; set; }

  /// <summary>Gets or sets the last access time.</summary>
  public DateTime? LastAccessTime { get; set; }

  /// <summary>Gets or sets the last write time.</summary>
  public DateTime? LastWriteTime { get; set; }

  /// <summary>Gets or sets the Windows file attributes.</summary>
  public uint? Attributes { get; set; }
}
