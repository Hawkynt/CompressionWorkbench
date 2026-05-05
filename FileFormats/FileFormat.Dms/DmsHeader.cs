namespace FileFormat.Dms;

/// <summary>
/// Represents the 56-byte file header of a DMS archive.
/// </summary>
public sealed class DmsHeader {
  /// <summary>Magic number (should be 0x444D5321 = "DMS!").</summary>
  public uint Magic { get; init; }

  /// <summary>Creator version number.</summary>
  public ushort CreatorVersion { get; init; }

  /// <summary>Minimum version needed to extract.</summary>
  public ushort NeededVersion { get; init; }

  /// <summary>Disk type (0 = standard Amiga DD).</summary>
  public ushort DiskType { get; init; }

  /// <summary>Overall compression mode used.</summary>
  public ushort CompressionMode { get; init; }

  /// <summary>Info flags (bit 0 = locked, bit 1 = noDMS, bit 4 = has info text, etc.).</summary>
  public uint InfoFlags { get; init; }

  /// <summary>First track number in the archive.</summary>
  public ushort From { get; init; }

  /// <summary>Last track number in the archive.</summary>
  public ushort To { get; init; }

  /// <summary>Low track number (valid range start).</summary>
  public ushort LowTrack { get; init; }

  /// <summary>High track number (valid range end).</summary>
  public ushort HighTrack { get; init; }

  /// <summary>Total packed (compressed) data size.</summary>
  public uint PackedSize { get; init; }

  /// <summary>Total unpacked (uncompressed) data size.</summary>
  public uint UnpackedSize { get; init; }

  /// <summary>CPU type that created the archive.</summary>
  public ushort CpuType { get; init; }

  /// <summary>CPU speed indicator.</summary>
  public ushort CpuSpeed { get; init; }

  /// <summary>Creation day (Amiga date format).</summary>
  public ushort CreatedDay { get; init; }

  /// <summary>Creation minute within the day.</summary>
  public ushort CreatedMinute { get; init; }

  /// <summary>Creation tick within the minute (50ths of a second).</summary>
  public ushort CreatedTick { get; init; }
}
