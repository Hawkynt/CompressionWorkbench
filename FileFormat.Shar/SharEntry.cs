namespace FileFormat.Shar;

/// <summary>
/// An entry in a shell archive.
/// </summary>
public sealed class SharEntry {
  /// <summary>The file name.</summary>
  public required string FileName { get; init; }

  /// <summary>The file data.</summary>
  public required byte[] Data { get; init; }
}
