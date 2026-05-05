namespace FileFormat.MacBinary;

/// <summary>
/// Represents the 128-byte header of a MacBinary encoded file.
/// </summary>
public class MacBinaryHeader {
  /// <summary>Mac filename (1-63 characters).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>4-byte Mac file type (e.g., "TEXT").</summary>
  public byte[] FileType { get; init; } = new byte[4];

  /// <summary>4-byte Mac creator code (e.g., "ttxt").</summary>
  public byte[] FileCreator { get; init; } = new byte[4];

  /// <summary>Finder flags high byte.</summary>
  public byte FinderFlags { get; init; }

  /// <summary>Length of the data fork in bytes.</summary>
  public uint DataForkLength { get; init; }

  /// <summary>Length of the resource fork in bytes.</summary>
  public uint ResourceForkLength { get; init; }

  /// <summary>File creation date.</summary>
  public DateTime CreatedDate { get; init; }

  /// <summary>File modification date.</summary>
  public DateTime ModifiedDate { get; init; }

  /// <summary>MacBinary version (0 = I, 129 = II, 130 = III).</summary>
  public byte Version { get; init; }

  /// <summary>CRC-16 of header bytes 0-123 (MacBinary II and III).</summary>
  public ushort HeaderCrc { get; init; }
}
