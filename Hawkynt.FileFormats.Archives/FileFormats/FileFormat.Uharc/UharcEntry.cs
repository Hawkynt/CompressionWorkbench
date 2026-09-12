namespace FileFormat.Uharc;

/// <summary>
/// Represents a single entry in a UHARC archive.
/// </summary>
public sealed class UharcEntry {
  /// <summary>Gets the filename stored in the archive (UTF-8, '/' separators).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Gets the uncompressed size in bytes.</summary>
  public uint OriginalSize { get; init; }

  /// <summary>Gets the compressed size in bytes.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets the compression method (0 = LZP, 255 = Store).</summary>
  public byte Method { get; init; }

  /// <summary>Gets the CRC-32 (IEEE polynomial) of the uncompressed data.</summary>
  public uint Crc32 { get; init; }

  /// <summary>Gets the last-modification date/time.</summary>
  public DateTime LastModified { get; init; }

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Offset in the stream where the compressed data begins. Used internally by the reader.</summary>
  internal long DataOffset { get; init; }

  // ── Unix timestamp helpers ──────────────────────────────────────────────

  internal static uint EncodeUnixTimestamp(DateTime dt) =>
    (uint)new DateTimeOffset(dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()).ToUnixTimeSeconds();

  internal static DateTime DecodeUnixTimestamp(uint timestamp) =>
    DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
}
