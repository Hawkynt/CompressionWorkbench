namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// One member device of an assembled array, addressed by its <see cref="Role"/>.
/// A member whose backing device is absent (a degraded array) has a <c>null</c>
/// <see cref="Data"/> stream; its content is reconstructed from parity where the
/// level allows.
/// </summary>
public sealed class RaidMember {
  /// <summary>Zero-based role/slot of this member in the array.</summary>
  public required int Role { get; init; }

  /// <summary>
  /// Seekable, readable stream over the raw member device, or <c>null</c> when the
  /// member is missing (degraded array).
  /// </summary>
  public Stream? Data { get; init; }

  /// <summary>Byte offset within <see cref="Data"/> at which array data begins.</summary>
  public required long DataOffsetBytes { get; init; }

  /// <summary>Usable data length of this member in bytes.</summary>
  public required long DataSizeBytes { get; init; }

  /// <summary>Whether the member's backing device is present.</summary>
  public bool IsPresent => this.Data != null;
}
