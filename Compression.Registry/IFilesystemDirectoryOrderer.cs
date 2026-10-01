namespace Compression.Registry;

/// <summary>
/// Opt-in maintenance capability: sort the entries of every directory of a filesystem
/// image in place, without moving any file data and without touching any allocation.
/// </summary>
/// <remarks>
/// <para>This is the <c>sort-entries</c> verb of the maintenance split
/// (<see cref="MaintenanceCapability.SortDirectoryEntries"/>). Firmware, media players and
/// boot loaders that list a FAT or exFAT volume in on-disk order show what this changes;
/// a driver that looks names up sees no difference at all.</para>
///
/// <para>Only directory records move. File contents, cluster chains, timestamps,
/// attributes, the volume label and serial, and the image size stay exactly as they
/// were. Anything the orderer cannot carry over intact — a damaged long-name chain, an
/// entry set whose checksum does not match — is refused with
/// <see cref="NotSupportedException"/> and the image is left byte for byte as it was.</para>
/// </remarks>
public interface IFilesystemDirectoryOrderer {
  /// <summary>
  /// Sorts every directory of <paramref name="image"/> in place by name
  /// (case-insensitive, then ordinal), keeping the entries the format pins first.
  /// </summary>
  /// <param name="image">The image, readable, writable and seekable.</param>
  void SortDirectoryEntries(Stream image);
}
