#pragma warning disable CS1591
using System.Globalization;

namespace FileSystem.OneFs;

/// <summary>
/// Identity of one OneFS data device in Dell diagnostic output.
/// </summary>
/// <remarks>
/// Dell documents the first two fields of an IDI/BADDR tuple as the node array
/// id (<c>devid</c>) and persistent drive id (<c>Lnum</c>). Lnum is not the
/// physical bay number and is not reused after drive replacement.
/// </remarks>
public readonly record struct OneFsDeviceIdentity {
  /// <summary>Gets the OneFS node array id (<c>devid</c>).</summary>
  public int DeviceId { get; }

  /// <summary>Gets the persistent logical drive id (<c>Lnum</c>).</summary>
  public int LogicalDriveNumber { get; }

  /// <summary>Creates a diagnostic OneFS device identity.</summary>
  public OneFsDeviceIdentity(int deviceId, int logicalDriveNumber) {
    ArgumentOutOfRangeException.ThrowIfNegative(deviceId);
    ArgumentOutOfRangeException.ThrowIfNegative(logicalDriveNumber);
    this.DeviceId = deviceId;
    this.LogicalDriveNumber = logicalDriveNumber;
  }

  /// <inheritdoc />
  public override string ToString()
    => string.Create(CultureInfo.InvariantCulture, $"{this.DeviceId},{this.LogicalDriveNumber}");
}

/// <summary>
/// Parsed textual OneFS block address as emitted by Dell diagnostics, for example
/// <c>6,3,669847150592:8192</c>.
/// </summary>
/// <remarks>
/// <para>
/// Dell explicitly documents the textual fields as node array id (<c>devid</c>),
/// drive id (<c>Lnum</c>), then block address:length. This type models that
/// diagnostic representation only. It makes no claim that OneFS serializes an
/// <c>ifs_baddr_t</c> on disk in the same byte order or field layout.
/// </para>
/// <para>
/// Published diagnostics include both 8 KiB filesystem-block extents and 512-byte
/// inode addresses that need not start on an 8 KiB boundary. Parsing therefore
/// preserves byte offset and length exactly. The block helpers report a value only
/// when a diagnostic extent actually satisfies the documented 8 KiB geometry.
/// </para>
/// </remarks>
public readonly record struct OneFsDiagnosticBlockAddress {
  /// <summary>Gets the node/drive identity from the diagnostic tuple.</summary>
  public OneFsDeviceIdentity Device { get; }

  /// <summary>Gets the diagnostic byte address within that logical drive.</summary>
  public long ByteOffset { get; }

  /// <summary>Gets the diagnostic byte length.</summary>
  public long Length { get; }

  /// <summary>Gets whether both address and length are aligned to complete 8 KiB OneFS blocks.</summary>
  public bool IsFilesystemBlockAligned
    => this.ByteOffset % OneFsReader.PhysicalBlockSize == 0
       && this.Length % OneFsReader.PhysicalBlockSize == 0;

  /// <summary>Gets the zero-based 8 KiB block index when the address is block aligned; otherwise null.</summary>
  public long? BlockIndex
    => this.ByteOffset % OneFsReader.PhysicalBlockSize == 0
      ? this.ByteOffset / OneFsReader.PhysicalBlockSize
      : null;

  /// <summary>Gets the number of complete 8 KiB blocks when the length is block aligned; otherwise null.</summary>
  public long? BlockCount
    => this.Length % OneFsReader.PhysicalBlockSize == 0
      ? this.Length / OneFsReader.PhysicalBlockSize
      : null;

  /// <summary>Creates a parsed diagnostic block address.</summary>
  public OneFsDiagnosticBlockAddress(OneFsDeviceIdentity device, long byteOffset, long length) {
    ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
    this.Device = device;
    this.ByteOffset = byteOffset;
    this.Length = length;
  }

  /// <summary>Parses Dell's strict <c>devid,Lnum,address:length</c> diagnostic form.</summary>
  public static bool TryParse(ReadOnlySpan<char> text, out OneFsDiagnosticBlockAddress address) {
    address = default;
    text = text.Trim();

    var firstComma = text.IndexOf(',');
    if (firstComma <= 0)
      return false;

    var remainder = text[(firstComma + 1)..];
    var secondCommaRelative = remainder.IndexOf(',');
    if (secondCommaRelative <= 0)
      return false;
    var secondComma = firstComma + 1 + secondCommaRelative;

    remainder = text[(secondComma + 1)..];
    var colonRelative = remainder.IndexOf(':');
    if (colonRelative <= 0)
      return false;
    var colon = secondComma + 1 + colonRelative;

    if (text[(colon + 1)..].IndexOfAny(',', ':') >= 0)
      return false;

    if (!int.TryParse(text[..firstComma], NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId)
        || !int.TryParse(text[(firstComma + 1)..secondComma], NumberStyles.None, CultureInfo.InvariantCulture, out var logicalDriveNumber)
        || !long.TryParse(text[(secondComma + 1)..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset)
        || !long.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
        || deviceId < 0
        || logicalDriveNumber < 0
        || byteOffset < 0
        || length <= 0)
      return false;

    address = new OneFsDiagnosticBlockAddress(
      new OneFsDeviceIdentity(deviceId, logicalDriveNumber),
      byteOffset,
      length);
    return true;
  }

  /// <inheritdoc />
  public override string ToString()
    => string.Create(
      CultureInfo.InvariantCulture,
      $"{this.Device.DeviceId},{this.Device.LogicalDriveNumber},{this.ByteOffset}:{this.Length}");
}
