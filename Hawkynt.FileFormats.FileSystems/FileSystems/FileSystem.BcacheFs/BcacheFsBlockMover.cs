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
  public bool RepointsRunsIndependently => true;

  /// <inheritdoc />
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
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
  /// <para>The whole description is derived again from the extents as they now
  /// stand — alloc, freespace, LRU and backpointers, and the usage totals the
  /// superblock's clean section carries — because the checker cross-examines all
  /// of them against each other, and a pass that patches some is how they come to
  /// disagree.</para>
  /// </remarks>
  public void SettleAllocation(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    // Every derived tree and the clean section's totals are rebuilt together from
    // the extents as they now stand; rewriting only some of them is how two
    // accounts of the same bucket come to disagree.
    image.Position = 0;
    BcacheFsInPlaceModifier.NormalizeMetadata(image);
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
      // An alloc key is not the same thing as an occupied bucket. A bucket that
      // has been emptied keeps its key so the generation it was bumped to
      // survives the emptying, and that key says data type free — which is the
      // volume agreeing that the freespace tree may offer the bucket, not
      // disagreeing with it.
      if (key.Value[14] != DataFree) occupied.Add(bucket);
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

      // The top byte carries generation bits, not the bucket.
      var end = (long)(key.Position.Offset & ((1UL << 56) - 1));
      for (var bucket = end - key.Size; bucket < end; ++bucket)
        if (occupied.Contains(bucket))
          problems.Add($"the freespace tree offers bucket {bucket}, which the alloc tree says is in use");
    }

    return problems;
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
}
