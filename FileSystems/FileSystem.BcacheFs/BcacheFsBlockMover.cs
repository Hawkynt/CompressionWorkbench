#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Moves a file's bytes inside a bcachefs volume and rewrites the extent keys that
/// name them.
/// </summary>
/// <remarks>
/// <para>Where a run of a file's bytes sits is one word in one key in the extents
/// b-tree: the pointer, whose middle forty-four bits are a sector. Moving the run
/// is the copy plus that word — nothing else on the volume records the position,
/// because in bcachefs nothing else can.</para>
///
/// <para>The node the keys live in carries a checksum over everything it holds, so
/// the whole node is re-stamped once the pass is over rather than after each move.
/// Doing it per move would be correct and would rewrite the same sector once for
/// every extent on the volume.</para>
/// </remarks>
public sealed class BcacheFsBlockMover : IFilesystemBlockMover {

  /// <summary>One extent key's pointer: where it points, and where that word is.</summary>
  /// <remarks>
  /// Two sectors are kept, not one. The pass is told where a run started, even for
  /// a run it lifted out of the volume and put back later, so that is what a
  /// pointer answers to; where the run is now is the answer, and matching on it
  /// would let a run that has landed on another's old address claim that other's
  /// pointer.
  /// </remarks>
  private sealed class Slot {
    internal required long NodeOffset { get; init; }
    internal required int FieldOffset { get; init; }
    internal required long OriginalSector { get; init; }
    internal required long Sector { get; set; }
    internal required int Sectors { get; init; }

    /// <summary>Where the key holding this pointer sorts, which its backpointer repeats.</summary>
    internal required Bpos ExtentPosition { get; init; }
  }

  private readonly List<Slot> _slots = [];
  private readonly List<long> _nodes = [];
  private int _nodeSectors = BucketSectors;

  /// <summary>Reads the extents b-tree so its pointers can be found again.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._slots.Clear();
    this._nodes.Clear();

    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid) return;

    this._nodeSectors = volume.BucketSectorCount;
    var node = new byte[this._nodeSectors * SectorSize];
    foreach (var sector in volume.NodeSectors(BtreeExtents)) {
      var offset = sector * SectorSize;
      if (offset + node.Length > image.Length) continue;

      image.Position = offset;
      image.ReadExactly(node);

      var found = false;
      foreach (var (fieldOffset, extentSector, sectors, position) in EnumeratePointers(node)) {
        this._slots.Add(new Slot {
          NodeOffset = offset, FieldOffset = fieldOffset,
          OriginalSector = extentSector, Sector = extentSector, Sectors = sectors,
          ExtentPosition = position,
        });
        found = true;
      }

      if (found) this._nodes.Add(offset);
    }
  }

  /// <summary>Every extent pointer in a node: where its word is, and what it says.</summary>
  private static IEnumerable<(int FieldOffset, long Sector, int Sectors, Bpos Position)> EnumeratePointers(
      byte[] node) {
    var offset = BcacheFsNodeBuilder.KeysOffset;
    var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));
    var end = offset + words * 8;
    if (end > node.Length) yield break;

    while (offset + 8 <= end) {
      var keyWords = node[offset];
      if (keyWords == 0) yield break;

      var bytes = keyWords * 8;
      if (offset + bytes > end) yield break;

      // Only keys written unpacked are moved: those are the ones this project
      // writes, and a volume it did not write is not one it rearranges.
      if ((node[offset + 1] & 0x7F) == KeyFormatCurrent && node[offset + 2] == KeyExtent) {
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(offset + 16));
        var position = ReadBpos(node.AsSpan(offset + 20));
        for (var value = offset + BkeyBytes; value + 8 <= offset + bytes; value += 8) {
          var word = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(value));
          if (!IsPointer(word)) continue;
          yield return (value, PointerSector(word), size, position);
          break;
        }
      }

      offset += bytes;
    }
  }

  /// <inheritdoc />
    /// <summary>
  /// Gets the allocation block size.
  /// </summary>
public int AllocationBlockSize => BucketBytes;

  /// <summary>
  /// The unit a layout may place a run at: a whole bucket.
  /// </summary>
  /// <remarks>
  /// A pointer names a sector, so finer placement is expressible — and refused. An
  /// extent may not straddle a bucket boundary, because a bucket is what bcachefs
  /// allocates and accounts in; a run laid down across one is read as an invalid
  /// key and the file it belongs to comes back as a hole. Quantising the layout to
  /// buckets is what keeps every run inside one.
  /// </remarks>
  public int BlockSize => BucketBytes;

  /// <summary>
  /// The first byte a file's bytes may occupy.
  /// </summary>
  /// <remarks>
  /// It is where the volume's own structures end, not where the first file
  /// currently starts. Taking the second would mean a volume whose files had been
  /// pushed to the tail could never be brought back to the front: the layout would
  /// be told the front was occupied by something it must not touch.
  /// </remarks>
  public long FirstDataByte => MetadataEndBytes;

  /// <inheritdoc />
    /// <summary>
  /// Gets a value indicating whether repoints runs independently.
  /// </summary>
public bool RepointsRunsIndependently => true;

  /// <inheritdoc />
    /// <summary>
  /// Gets a value indicating whether supports held runs.
  /// </summary>
public bool SupportsHeldRuns => true;

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long sourceOffset, long destinationOffset, long length,
      bool zeroSource = false) {
    ArgumentNullException.ThrowIfNull(image);
    if (sourceOffset == destinationOffset || length <= 0) return;

    var buffer = new byte[Math.Min(length, BucketBytes)];
    var moved = 0L;
    while (moved < length) {
      var chunk = (int)Math.Min(buffer.Length, length - moved);
      image.Position = sourceOffset + moved;
      image.ReadExactly(buffer, 0, chunk);
      image.Position = destinationOffset + moved;
      image.Write(buffer, 0, chunk);
      moved += chunk;
    }

    if (!zeroSource) return;

    Array.Clear(buffer);
    var cleared = 0L;
    while (cleared < length) {
      var chunk = (int)Math.Min(buffer.Length, length - cleared);
      image.Position = sourceOffset + cleared;
      image.Write(buffer, 0, chunk);
      cleared += chunk;
    }
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName,
      long sourceOffset, long destinationOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    _ = fileName;   // a pointer is found by where it points, not by whose bytes they are
    if (sourceOffset == destinationOffset) return;

    var sourceSector = sourceOffset / SectorSize;
    var sectors = (int)((length + SectorSize - 1) / SectorSize);

    // Where the run began is what the pass names it by, and no two runs began in
    // the same place. Where it happens to be now is not usable as a name: another
    // run may have been laid down there in the meantime.
    //
    // One move can carry several pointers. A pointer may not cross a bucket, so a
    // run spanning n buckets holds at least n of them; repointing only the one
    // that starts where the run starts moves everybody's bytes and leaves all but
    // the first pointing at the bytes left behind.
    var delta = (destinationOffset - sourceOffset) / SectorSize;
    var end = sourceSector + sectors;
    var carried = this._slots.Where(s => s.OriginalSector >= sourceSector && s.OriginalSector < end).ToArray();
    if (carried.Length != 0) {
      foreach (var moved in carried)
        moved.Sector = moved.OriginalSector + delta;
      return;
    }

    var slot = this._slots.FirstOrDefault(s => s.Sector == sourceSector && s.Sectors == sectors);
    if (slot == null) return;

    slot.Sector = destinationOffset / SectorSize;
  }

  /// <summary>
  /// Writes every pointer back and re-stamps the node that holds them.
  /// </summary>
  /// <remarks>
  /// A b-tree node's checksum covers all the keys it holds, so this is done once
  /// the whole pass is over: until then the node on disk and the pointers in hand
  /// disagree, and stamping it early would only be undone by the next move.
  /// </remarks>
  public void Settle(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._nodes.Count == 0) return;

    var node = new byte[this._nodeSectors * SectorSize];
    foreach (var nodeOffset in this._nodes) {
      image.Position = nodeOffset;
      image.ReadExactly(node);

      foreach (var slot in this._slots) {
        if (slot.NodeOffset != nodeOffset) continue;

        var word = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(slot.FieldOffset));
        var device = (byte)((word >> 48) & 0xFF);
        var generation = (byte)((word >> 56) & 0xFF);
        BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(slot.FieldOffset),
          ExtentPointer(slot.Sector, device, generation));
      }

      var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));
      var end = BcacheFsNodeBuilder.KeysOffset + words * 8;
      var checksum = MetadataChecksum(node.AsSpan(16, end - 16));
      BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(0), checksum);
      BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(8), 0);

      image.Position = nodeOffset;
      image.Write(node, 0, (end + SectorSize - 1) / SectorSize * SectorSize);
    }

    image.Flush();
  }

  /// <summary>
  /// Rewrites the trees that say which buckets hold data, now that the data is in
  /// different buckets.
  /// </summary>
  /// <remarks>
  /// <para>Moving a run rewrites the one word that says where it is, and that is
  /// enough for a reader to find the bytes — but not enough for the volume to be
  /// consistent. bcachefs keeps a second account of the same facts: the alloc tree
  /// says what each bucket holds, the freespace tree says which buckets hold
  /// nothing, and a backpointer per extent points from the space back at the key
  /// that claims it. A pass that moves data and leaves those alone produces a
  /// volume whose extents point into buckets the alloc tree has never heard of,
  /// which is what <c>fsck</c> reports as "data type user ptr gen 0 missing in
  /// alloc btree" — hundreds of times, once per run.</para>
  ///
  /// <para>Only the entries describing file data are replaced. What the superblock,
  /// the journal and the b-trees themselves occupy has not moved, so those keys are
  /// read off the volume and put back untouched rather than derived again from a
  /// layout rule — the rule that produced them assumes files sit immediately after
  /// the metadata, which is exactly what a defragmentation stops being true.</para>
  /// </remarks>
  public void SettleAllocation(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid) return;

    var bucketSectors = volume.BucketSectorCount;
    if (bucketSectors <= 0) return;

    // Where every run of file data now sits, gathered by the bucket holding it.
    // A run may not straddle a bucket, so a bucket's dirty sectors are the runs
    // that landed in it -- normally one, and summed rather than assumed.
    var userSectors = new SortedDictionary<long, uint>();
    foreach (var slot in this._slots) {
      var bucket = slot.Sector / bucketSectors;
      userSectors.TryGetValue(bucket, out var already);
      userSectors[bucket] = (uint)Math.Min(bucketSectors, already + slot.Sectors);
    }

    // ── alloc: what each bucket holds ──────────────────────────────────────
    var keptAlloc = new List<Key>();
    var usedBuckets = new SortedSet<long>();
    foreach (var (offset, key) in ReadKeys(image, volume, BtreeAlloc)) {
      _ = offset;
      // The data type sits at byte fourteen of a bch_alloc_v4.
      if (key.Type == KeyAllocV4 && key.Value.Length > 14 && key.Value[14] == DataUser) continue;
      keptAlloc.Add(key);
      usedBuckets.Add((long)key.Position.Offset);
    }

    foreach (var (bucket, sectors) in userSectors) {
      keptAlloc.Add(AllocUserKey(bucket, sectors));
      usedBuckets.Add(bucket);
    }

    // ── freespace: the runs of buckets nothing holds ───────────────────────
    var totalBuckets = volume.DeviceSectors / bucketSectors;
    var freespace = new List<Key>();
    var runStart = -1L;
    for (var bucket = 0L; bucket <= totalBuckets; ++bucket) {
      if (bucket < totalBuckets && !usedBuckets.Contains(bucket)) {
        if (runStart < 0) runStart = bucket;
        continue;
      }

      if (runStart >= 0) freespace.Add(FreeRunKey(runStart, bucket));
      runStart = -1;
    }

    // ── backpointers: from the space back to the key that claims it ────────
    var backpointers = new List<Key>();
    foreach (var (offset, key) in ReadKeys(image, volume, BtreeBackpointers)) {
      _ = offset;
      // A backpointer's data type is byte two of the value; the ones naming
      // b-tree nodes describe metadata that has not moved.
      if (key.Type == KeyBackpointer && key.Value.Length > 2 && key.Value[2] == DataUser) continue;
      backpointers.Add(key);
    }

    foreach (var slot in this._slots)
      backpointers.Add(ExtentBackpointer(slot.Sector, slot.Sectors, slot.ExtentPosition));

    RewriteTree(image, volume, BtreeAlloc, keptAlloc);
    RewriteTree(image, volume, BtreeFreespace, freespace);
    RewriteTree(image, volume, BtreeBackpointers, backpointers);
    image.Flush();
  }

  /// <summary>
  /// Where the volume's two accounts of the same facts disagree, in words.
  /// </summary>
  /// <remarks>
  /// <para>An extent says where a file's bytes are; the alloc tree says what the
  /// bucket holding them contains; the freespace tree says that bucket is not
  /// empty. All three are the same fact written down three times, and a volume is
  /// only consistent while they agree. <c>bcachefs fsck</c> is the authority on
  /// that, but it is not installed everywhere, and a check that only runs on the
  /// machines that have it is not a check on the rest.</para>
  ///
  /// <para>An empty list is the healthy answer.</para>
  /// </remarks>
  public IReadOnlyList<string> DescribeAllocationDiscrepancies(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var problems = new List<string>();
    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid) return problems;

    var bucketSectors = volume.BucketSectorCount;
    if (bucketSectors <= 0) return problems;

    // What the extents say, bucket by bucket.
    var claimed = new SortedDictionary<long, uint>();
    var node = new byte[this._nodeSectors * SectorSize];
    foreach (var sector in volume.NodeSectors(BtreeExtents)) {
      var nodeOffset = sector * SectorSize;
      if (nodeOffset + node.Length > image.Length) continue;

      image.Position = nodeOffset;
      image.ReadExactly(node);
      foreach (var (_, extentSector, sectors, _) in EnumeratePointers(node)) {
        var bucket = extentSector / bucketSectors;
        claimed.TryGetValue(bucket, out var already);
        claimed[bucket] = (uint)Math.Min(bucketSectors, already + sectors);
      }
    }

    // What the alloc tree says.
    var recorded = new Dictionary<long, uint>();
    var occupied = new HashSet<long>();
    foreach (var (_, key) in this.ReadKeys(image, volume, BtreeAlloc)) {
      if (key.Type != KeyAllocV4 || key.Value.Length <= 16) continue;

      var bucket = (long)key.Position.Offset;
      occupied.Add(bucket);
      if (key.Value[14] == DataUser)
        recorded[bucket] = BinaryPrimitives.ReadUInt32LittleEndian(key.Value.AsSpan(16));
    }

    foreach (var (bucket, sectors) in claimed) {
      if (!recorded.TryGetValue(bucket, out var said))
        problems.Add($"bucket {bucket} holds {sectors} sectors of file data that the alloc tree does not mention");
      else if (said != sectors)
        problems.Add($"bucket {bucket} holds {sectors} sectors of file data but the alloc tree says {said}");
    }

    foreach (var bucket in recorded.Keys)
      if (!claimed.ContainsKey(bucket))
        problems.Add($"the alloc tree gives bucket {bucket} to file data no extent points at");

    // And what the freespace tree says, which must not be a bucket in use.
    foreach (var (_, key) in this.ReadKeys(image, volume, BtreeFreespace)) {
      if (key.Type != KeySet) continue;

      var end = (long)key.Position.Offset;
      for (var bucket = end - key.Size; bucket < end; ++bucket)
        if (occupied.Contains(bucket))
          problems.Add($"the freespace tree offers bucket {bucket}, which the alloc tree says is in use");
    }

    return problems;
  }

  /// <summary>What one bucket of file data holds, as the alloc tree records it.</summary>
  /// <remarks>
  /// The same forty-eight byte <c>bch_alloc_v4</c> the writer lays down for a
  /// freshly built volume. A bucket a defragmentation moved data into is on its
  /// first use as far as this volume is concerned, so its generation is zero.
  /// </remarks>
  private static Key AllocUserKey(long bucket, uint dirtySectors) {
    var value = new byte[48];
    value[14] = DataUser;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), dirtySectors);
    return new Key(KeyAllocV4, new Bpos(0, (ulong)bucket, 0), 0, value);
  }

  /// <summary>One run of buckets holding nothing, keyed by where it ends.</summary>
  private static Key FreeRunKey(long firstBucket, long endBucket) =>
    new(KeySet, new Bpos(0, (ulong)endBucket, 0), (uint)(endBucket - firstBucket), []);

  /// <summary>Points back from a stretch of file data to the extent naming it.</summary>
  private static Key ExtentBackpointer(long firstSector, int sectors, Bpos extent) {
    var value = new byte[32];
    value[0] = (byte)BtreeExtents;
    value[2] = DataUser;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), extent);
    return new Key(KeyBackpointer, new Bpos(0, (ulong)firstSector << ExtentBpShift, 0), 0, value);
  }

  /// <summary>Every key a tree's nodes hold, with the node each came from.</summary>
  private IEnumerable<(long NodeOffset, Key Key)> ReadKeys(Stream image, BcacheFsVolume volume, int btree) {
    var node = new byte[this._nodeSectors * SectorSize];
    foreach (var sector in volume.NodeSectors(btree)) {
      var nodeOffset = sector * SectorSize;
      if (nodeOffset + node.Length > image.Length) continue;

      image.Position = nodeOffset;
      image.ReadExactly(node);

      var offset = BcacheFsNodeBuilder.KeysOffset;
      var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));
      var end = offset + words * 8;
      if (end > node.Length) continue;

      while (offset + 8 <= end) {
        var keyWords = node[offset];
        if (keyWords == 0) break;

        var bytes = keyWords * 8;
        if (offset + bytes > end) break;

        if ((node[offset + 1] & 0x7F) == KeyFormatCurrent) {
          var value = node[(offset + BkeyBytes)..(offset + bytes)];
          yield return (nodeOffset, new Key(
            node[offset + 2],
            ReadBpos(node.AsSpan(offset + 20)),
            BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(offset + 16)),
            value));
        }

        offset += bytes;
      }
    }
  }

  /// <summary>
  /// Lays a new set of keys into a single-node tree, keeping the node's header.
  /// </summary>
  /// <remarks>
  /// The header carries the node's identity, the range it is responsible for and
  /// the format its keys are read under, none of which a re-account changes; only
  /// the keys and the two things derived from them -- how many words the node holds
  /// and the checksum over them -- are written again. A tree that outgrew the
  /// sectors its pointer claims cannot be fixed this way, because that pointer
  /// lives in the superblock, so this says so and lets the caller write the volume
  /// out again instead of leaving a node the reader would read short.
  /// </remarks>
  private void RewriteTree(Stream image, BcacheFsVolume volume, int btree, List<Key> keys) {
    var offsets = volume.NodeSectors(btree).Select(s => s * SectorSize).ToList();
    if (offsets.Count != 1)
      throw new NotSupportedException(
        $"bcachefs: tree {btree} spans {offsets.Count} nodes; re-accounting one node at a time "
        + "cannot say which keys belong to which.");

    var nodeOffset = offsets[0];
    var node = new byte[this._nodeSectors * SectorSize];
    image.Position = nodeOffset;
    image.ReadExactly(node);

    var claimed = BcacheFsNodeBuilder.KeysOffset
      + BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158)) * 8;
    var claimedSectors = (claimed + SectorSize - 1) / SectorSize;

    keys.Sort((a, b) => Compare(a.Position, b.Position));
    var cursor = BcacheFsNodeBuilder.KeysOffset;
    foreach (var key in keys) {
      if (cursor + key.Bytes > node.Length)
        throw new NotSupportedException($"bcachefs: tree {btree} no longer fits one node.");
      cursor += WriteKey(node.AsSpan(cursor), key);
    }

    var neededSectors = (cursor + SectorSize - 1) / SectorSize;
    if (neededSectors > claimedSectors)
      throw new NotSupportedException(
        $"bcachefs: tree {btree} grew from {claimedSectors} sectors to {neededSectors}, "
        + "which the pointer in the superblock still describes as the shorter one.");

    // Anything the old, longer key list left behind is not part of the node any
    // more, and a reader that trusted the words count would never look at it --
    // but a checker that scans the bucket would.
    node.AsSpan(cursor, claimedSectors * SectorSize - cursor).Clear();

    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(158),
      (ushort)((cursor - BcacheFsNodeBuilder.KeysOffset) / 8));
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(0), MetadataChecksum(node.AsSpan(16, cursor - 16)));
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(8), 0);

    image.Position = nodeOffset;
    image.Write(node, 0, claimedSectors * SectorSize);
  }
}
