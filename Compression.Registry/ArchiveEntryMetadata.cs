namespace Compression.Registry;

/// <summary>
/// Portable metadata supplied with an archive or filesystem entry.
/// </summary>
/// <remarks>
/// Fields are optional because many streaming sources cannot provide them. Unix
/// IDs and mode are numeric so metadata can cross platforms without changing their
/// meaning; formats may preserve only the fields their on-disk model supports.
/// </remarks>
public sealed record ArchiveEntryMetadata(
  DateTimeOffset? CreationTimeUtc = null,
  DateTimeOffset? LastAccessTimeUtc = null,
  DateTimeOffset? LastWriteTimeUtc = null,
  uint? UnixUserId = null,
  uint? UnixGroupId = null,
  ushort? UnixMode = null,
  DateTimeOffset? StatusChangeTimeUtc = null
);
