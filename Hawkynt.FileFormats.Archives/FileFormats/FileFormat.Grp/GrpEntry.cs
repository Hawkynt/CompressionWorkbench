namespace FileFormat.Grp;

/// <summary>Entry in a BUILD Engine GRP archive.</summary>
public sealed class GrpEntry {
  /// <summary>File name, up to 12 characters (null-padded in the on-disk format).</summary>
  public string Name { get; init; } = "";
  /// <summary>Uncompressed size of the file data in bytes.</summary>
  public int Size { get; init; }
  /// <summary>Absolute byte offset of the file data within the GRP stream.</summary>
  public long DataOffset { get; init; }
}
