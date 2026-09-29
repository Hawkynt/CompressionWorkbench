#pragma warning disable CS1591
namespace FileSystem.Gfs2;

/// <summary>POSIX inode attributes stored directly in a GFS2 dinode.</summary>
public sealed record Gfs2InodeMetadata(
  uint Mode,
  uint UserId,
  uint GroupId,
  uint Flags,
  ulong AccessTimeSeconds,
  uint AccessTimeNanoseconds,
  ulong ModificationTimeSeconds,
  uint ModificationTimeNanoseconds,
  ulong ChangeTimeSeconds,
  uint ChangeTimeNanoseconds
);
