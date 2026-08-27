#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.BcacheFs;

/// <summary>
/// Bootstrap state for a driver: device membership, selected superblocks,
/// clean checkpoint, merged journal and the effective post-replay b-tree roots.
/// It owns no streams; callers retain lifetime control of the supplied devices.
/// </summary>
internal sealed class BcacheFsCoreVolume {
  internal required IReadOnlyDictionary<byte, Stream> Devices { get; init; }
  internal required IReadOnlyDictionary<byte, BcacheFsDeviceSuperblocks> DeviceSuperblocks { get; init; }
  internal required BcacheFsSuperblockRecord Superblock { get; init; }
  internal required IReadOnlyList<BcacheFsMemberRecord> Members { get; init; }
  internal required BcacheFsCheckpoint? Checkpoint { get; init; }
  internal required BcacheFsJournalLog Journal { get; init; }
  internal required BcacheFsJournalOverlay Overlay { get; init; }
  internal required IReadOnlyDictionary<byte, BcacheFsTreeRoot> EffectiveRoots { get; init; }
  internal required IReadOnlyList<string> Diagnostics { get; init; }
  internal required bool Recoverable { get; init; }

  internal bool Clean => this.Superblock.Clean;

  internal BcacheFsTreeRoot? Root(BcacheFsBtreeId id)
    => this.EffectiveRoots.GetValueOrDefault((byte)id);

  internal static BcacheFsCoreVolume Open(Stream device)
    => Open([device]);

  internal static BcacheFsCoreVolume Open(IEnumerable<Stream> devices) {
    ArgumentNullException.ThrowIfNull(devices);
    var supplied = devices.ToList();
    if (supplied.Count == 0)
      throw new ArgumentException("At least one bcachefs member device is required.", nameof(devices));

    var diagnostics = new List<string>();
    var discovered = new List<(Stream Stream, BcacheFsDeviceSuperblocks Set, BcacheFsSuperblockRecord Current)>();
    foreach (var stream in supplied) {
      var set = BcacheFsDeviceSuperblocks.Read(stream);
      diagnostics.AddRange(set.Diagnostics);
      if (set.Current == null) {
        diagnostics.Add("device has no structurally valid bcachefs superblock copy.");
        continue;
      }
      discovered.Add((stream, set, set.Current));
    }
    if (discovered.Count == 0)
      throw new InvalidDataException("No supplied device contains a structurally valid bcachefs superblock.");

    var identity = discovered[0].Current.InternalUuidBytes;
    foreach (var (_, _, current) in discovered.Skip(1))
      if (!current.InternalUuidBytes.AsSpan().SequenceEqual(identity))
        throw new InvalidDataException("Supplied devices belong to different bcachefs filesystems.");

    // Superblock sequence is global filesystem state; take the newest copy from
    // any member, while each member still uses its own current copy to locate its
    // local journal buckets.
    var global = discovered
      .Select(d => d.Current)
      .OrderByDescending(s => s.Sequence)
      .ThenBy(s => s.DeviceIndex)
      .First();

    var streamsByIndex = new Dictionary<byte, Stream>();
    var superblocksByIndex = new Dictionary<byte, BcacheFsDeviceSuperblocks>();
    foreach (var (stream, set, current) in discovered) {
      if (!streamsByIndex.TryAdd(current.DeviceIndex, stream))
        throw new InvalidDataException($"Two supplied devices claim bcachefs member index {current.DeviceIndex}.");
      superblocksByIndex[current.DeviceIndex] = set;
    }

    var members = BcacheFsMembers.Read(global);
    if (members.Count < global.DeviceCount)
      diagnostics.Add($"members table has {members.Count} records but superblock declares {global.DeviceCount} devices.");

    var checkpoint = BcacheFsCheckpoint.Read(global);
    if (global.Clean && checkpoint == null)
      diagnostics.Add("filesystem is marked clean but BCH_SB_FIELD_clean is missing.");
    else if (checkpoint != null)
      diagnostics.AddRange(checkpoint.Diagnostics);

    var deviceLogs = new List<BcacheFsJournalDeviceLog>();
    // Clean mounts need not read the journal for correctness, but doing so here
    // makes the core state complete and catches disagreeing replicas early. It is
    // an offline library; a future driver mount policy may choose the fast path.
    foreach (var (stream, _, current) in discovered)
      deviceLogs.Add(BcacheFsJournalReader.ReadDevice(stream, current));

    var journal = BcacheFsJournalLog.Merge(deviceLogs);
    var superblockBlacklists = ReadSuperblockBlacklists(global, diagnostics);
    var overlay = BcacheFsJournalOverlay.Build(journal, superblockBlacklists);
    diagnostics.AddRange(overlay.Diagnostics);

    var roots = new Dictionary<byte, BcacheFsTreeRoot>();
    if (checkpoint != null)
      foreach (var root in checkpoint.Roots)
        roots[root.BtreeId] = root;

    foreach (var root in overlay.RootUpdates.OrderBy(r => r.Sequence).ThenBy(r => r.JournalOrder))
      roots[root.BtreeId] = new BcacheFsTreeRoot(
        root.BtreeId,
        root.Level,
        root.RootKey,
        root.Sequence,
        BcacheFsTreeRootSource.Journal);

    var recoverable = global.Clean
      ? checkpoint?.Valid == true
      : overlay.Complete && overlay.RootUpdates.Count != 0;

    if (!global.Clean && overlay.RootUpdates.Count == 0) {
      diagnostics.Add("dirty filesystem has no replayable btree_root journal entries.");
      recoverable = false;
    }

    // A newer superblock copy than the one on another supplied member is normal
    // during propagation, but record it: a write-capable driver will need to
    // rewrite stale copies after recovery.
    foreach (var (_, _, current) in discovered)
      if (current.Sequence != global.Sequence)
        diagnostics.Add($"member {current.DeviceIndex} superblock seq {current.Sequence} trails selected seq {global.Sequence}.");

    return new BcacheFsCoreVolume {
      Devices = streamsByIndex,
      DeviceSuperblocks = superblocksByIndex,
      Superblock = global,
      Members = members,
      Checkpoint = checkpoint,
      Journal = journal,
      Overlay = overlay,
      EffectiveRoots = roots,
      Diagnostics = diagnostics,
      Recoverable = recoverable,
    };
  }

  private static IReadOnlyList<BcacheFsJournalSequenceRange> ReadSuperblockBlacklists(
      BcacheFsSuperblockRecord superblock,
      List<string> diagnostics) {
    var result = new List<BcacheFsJournalSequenceRange>();
    foreach (var field in superblock.FieldsOf(BcacheFsSuperblockFieldType.JournalSequenceBlacklist)) {
      var bytes = field.RawBytes;
      for (var offset = 8; offset + 16 <= bytes.Length; offset += 16) {
        var start = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        var end = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8));
        if (start > end) {
          diagnostics.Add($"superblock journal blacklist {start}..{end} has start > end.");
          continue;
        }
        result.Add(new BcacheFsJournalSequenceRange(start, end));
      }
      if ((bytes.Length - 8) % 16 != 0)
        diagnostics.Add("superblock journal blacklist has a truncated trailing range.");
    }
    return result;
  }
}
