namespace FileFormat.Dms;

/// <summary>
/// Represents a track entry within a DMS archive.
/// </summary>
public sealed class DmsTrack {
  /// <summary>Track number.</summary>
  public ushort TrackNumber { get; init; }

  /// <summary>Size of the compressed track data in bytes.</summary>
  public ushort CompressedSize { get; init; }

  /// <summary>Size of the uncompressed track data in bytes (normally 11264 for a cylinder).</summary>
  public ushort UncompressedSize { get; init; }

  /// <summary>Compression mode used for this track.</summary>
  public byte CompressionMode { get; init; }

  /// <summary>Flags byte.</summary>
  public byte Flags { get; init; }

  /// <summary>CRC-16 of the compressed data.</summary>
  public ushort CompressedCrc { get; init; }

  /// <summary>CRC-16 of the uncompressed data.</summary>
  public ushort UncompressedCrc { get; init; }

  /// <summary>Offset in the stream where the compressed track data starts.</summary>
  public long DataOffset { get; init; }
}
