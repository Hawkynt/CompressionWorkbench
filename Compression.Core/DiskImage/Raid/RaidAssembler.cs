namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Assembles N member disks into one virtual guest-disk <see cref="RaidAssembledStream"/>
/// by sniffing each member's RAID metadata, grouping members that belong to the same
/// array, ordering them by role and describing the geometry. The resulting stream can be
/// handed to the ordinary partition/filesystem readers unchanged.
/// </summary>
/// <remarks>
/// Recognised metadata: Linux md 1.x (<see cref="Md1SuperblockParser"/>) and md 0.90
/// (<see cref="Md09SuperblockParser"/>). Additional formats plug in by adding a sniffer
/// to <see cref="Sniff"/> that yields a <see cref="RaidMemberMetadata"/>; the grouping,
/// ordering and stream construction below then handle them uniformly.
/// </remarks>
public static class RaidAssembler {

  /// <summary>
  /// Sniffs and assembles the given member streams. Returns the assembled stream, or
  /// <c>null</c> when no coherent RAID array can be formed from the members.
  /// </summary>
  /// <param name="members">Candidate member device streams (order is irrelevant).</param>
  /// <param name="leaveOpen">When <c>false</c>, the returned stream disposes the members.</param>
  public static RaidAssembledStream? TryAssemble(IReadOnlyList<Stream> members, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(members);

    var decoded = new List<(Stream Stream, RaidMemberMetadata Meta)>();
    foreach (var s in members) {
      var meta = Sniff(s);
      if (meta != null) decoded.Add((s, meta));
    }
    if (decoded.Count == 0) return null;

    // Group by array identity and pick the largest coherent group.
    var group = decoded
      .GroupBy(d => (d.Meta.Format, d.Meta.ArrayUuid))
      .OrderByDescending(g => g.Count())
      .First()
      .ToList();

    var reference = group[0].Meta;
    var raidDisks = reference.RaidDisks;
    if (raidDisks <= 0) return null;

    var perDevice = group.Max(g => g.Meta.DataSizeBytes);

    // Place each present member at its role; reject conflicting roles.
    var slots = new RaidMember?[raidDisks];
    foreach (var (stream, meta) in group) {
      if (meta.Role < 0 || meta.Role >= raidDisks) continue;
      if (slots[meta.Role] != null) continue; // duplicate role — keep the first.
      slots[meta.Role] = new RaidMember {
        Role = meta.Role,
        Data = stream,
        DataOffsetBytes = meta.DataOffsetBytes,
        DataSizeBytes = meta.DataSizeBytes,
      };
    }

    // Fill missing roles with placeholders (degraded array; reconstructed where possible).
    var membersOrdered = new List<RaidMember>(raidDisks);
    for (var role = 0; role < raidDisks; role++) {
      membersOrdered.Add(slots[role] ?? new RaidMember {
        Role = role,
        Data = null,
        DataOffsetBytes = 0,
        DataSizeBytes = perDevice,
      });
    }

    var array = new RaidArray {
      Level = reference.Level,
      ChunkSizeBytes = reference.ChunkSizeBytes,
      RaidDisks = raidDisks,
      NearCopies = reference.NearCopies,
      Layout = reference.Layout,
      Members = membersOrdered,
      PerDeviceDataBytes = perDevice,
      ArrayUuid = reference.ArrayUuid,
      ArrayName = reference.ArrayName,
    };

    return new RaidAssembledStream(array, leaveOpen);
  }

  /// <summary>
  /// Convenience overload that opens the given member file paths read-only and assembles
  /// them. The returned stream owns and disposes the opened files. Returns <c>null</c>
  /// (after closing any opened files) when no array can be formed.
  /// </summary>
  /// <param name="memberPaths">Paths to raw member device images.</param>
  public static RaidAssembledStream? TryAssemble(IReadOnlyList<string> memberPaths) {
    ArgumentNullException.ThrowIfNull(memberPaths);

    var opened = new List<Stream>(memberPaths.Count);
    try {
      foreach (var p in memberPaths)
        opened.Add(File.Open(p, FileMode.Open, FileAccess.Read, FileShare.Read));

      var result = TryAssemble(opened, leaveOpen: false);
      if (result == null)
        foreach (var s in opened) s.Dispose();
      return result;
    } catch {
      foreach (var s in opened) s.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Sniffs a single member stream for recognised RAID metadata. New metadata formats
  /// plug in here by returning a populated <see cref="RaidMemberMetadata"/>.
  /// </summary>
  /// <param name="member">Candidate member device stream.</param>
  public static RaidMemberMetadata? Sniff(Stream member) {
    ArgumentNullException.ThrowIfNull(member);
    if (!member.CanSeek) return null;
    return Md1SuperblockParser.TryParse(member)
        ?? Md09SuperblockParser.TryParse(member);
  }
}
