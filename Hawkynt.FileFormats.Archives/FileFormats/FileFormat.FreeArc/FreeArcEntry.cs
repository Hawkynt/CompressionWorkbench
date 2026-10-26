namespace FileFormat.FreeArc;

/// <summary>Represents a single file or directory recorded in a FreeArc directory block.</summary>
public sealed class FreeArcEntry {
  /// <summary>The full stored path, with <c>/</c> as separator.</summary>
  public string Name { get; init; } = "";

  /// <summary>The uncompressed size of the file in bytes.</summary>
  public long Size { get; init; }

  /// <summary>
  /// The compressed size of the solid block that holds this file, reported on the block's first
  /// file only (FreeArc does not record per-file compressed sizes); zero on the others.
  /// </summary>
  public long CompressedSize { get; init; }

  /// <summary>The method string of the solid block holding this file, e.g. <c>lzma:32kb:normal:32</c>.</summary>
  public string Method { get; init; } = FreeArcMethods.Storing;

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>The modification time (stored as 32-bit Unix seconds), in UTC.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>The CRC-32 of the uncompressed file contents.</summary>
  public uint Crc { get; init; }

  /// <summary>The solid data block holding this file.</summary>
  internal FreeArcDataBlock Block { get; init; } = null!;

  /// <summary>The byte offset of this file inside the decoded solid block.</summary>
  internal long OffsetInBlock { get; init; }
}

/// <summary>A solid data block: one compressed stream holding the concatenated contents of consecutive files.</summary>
internal sealed record FreeArcDataBlock(string Compressor, long Position, long CompressedSize, long OriginalSize);
