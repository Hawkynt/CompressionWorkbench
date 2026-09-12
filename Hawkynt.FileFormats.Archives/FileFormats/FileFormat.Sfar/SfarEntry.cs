namespace FileFormat.Sfar;

/// <summary>
/// One file entry inside a BioWare SFAR archive.
/// SFARs store no in-band paths — only an MD5 hash of the lowercased path. The reader
/// either resolves names from the optional <c>Filenames.txt</c> manifest at entry index 0,
/// or falls back to a synthetic <c>&lt;hexhash&gt;.bin</c> name.
/// </summary>
public sealed class SfarEntry {

  /// <summary>The resolved or synthetic name surfaced to callers.</summary>
  public string Name { get; init; } = "";

  /// <summary>Raw 16-byte MD5 hash of the original lowercased forward-slash path.</summary>
  public byte[] PathHash { get; init; } = [];

  /// <summary>Uncompressed payload size in bytes (5-byte LE field on disk; up to 2^40-1).</summary>
  public long Size { get; init; }

  /// <summary>Index of this file's first block in the archive's block-size table.</summary>
  public int BlockTableIndex { get; init; }

  /// <summary>Absolute offset of the entry's first block in the archive (5-byte LE on disk).</summary>
  public long DataOffset { get; init; }
}
