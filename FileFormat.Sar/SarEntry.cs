namespace FileFormat.Sar;

/// <summary>
/// Represents a single file entry in a SAR archive.
/// </summary>
public sealed class SarEntry {
  /// <summary>Gets the filename stored in the archive (null-terminated Shift-JIS/ASCII).</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>Gets the offset of the file data relative to the start of the data area.</summary>
  public uint Offset { get; init; }

  /// <summary>Gets the size of the file data in bytes.</summary>
  public uint Size { get; init; }
}
