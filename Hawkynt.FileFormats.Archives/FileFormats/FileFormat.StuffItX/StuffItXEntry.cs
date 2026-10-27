namespace FileFormat.StuffItX;

/// <summary>One file, directory or resource fork of a StuffIt X (.sitx) archive.</summary>
public sealed class StuffItXEntry {
  /// <summary>The bare file or directory name.</summary>
  public string Name { get; init; } = "";

  /// <summary>Full path within the archive, '/'-separated; resource forks end in <c>.rsrc</c>.</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Whether this entry is the resource fork of a file.</summary>
  public bool IsResourceFork { get; init; }

  /// <summary>Uncompressed size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>
  /// Compressed size in bytes. Forks share solid streams, so this is the fork's share of its
  /// stream, proportional to its length.
  /// </summary>
  public long CompressedSize { get; init; }

  /// <summary>Name of the stream's compression method.</summary>
  public string Method { get; init; } = "";

  /// <summary>Last modification time, when the catalog records one.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Creation time, when the catalog records one.</summary>
  public DateTime? Created { get; init; }

  /// <summary>The entry's comment, when the catalog records one.</summary>
  public string? Comment { get; init; }

  /// <summary>Offset of the stream's first data block in the archive.</summary>
  public long DataOffset { get; init; }

  /// <summary>The solid stream holding this fork, or -1 for an empty entry.</summary>
  internal long StreamId { get; init; } = -1;

  /// <summary>Offset of this fork within its decoded stream.</summary>
  internal long StreamOffset { get; init; }
}
