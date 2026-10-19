using System.Buffers.Binary;

namespace FileSystem.HfsPlus;

/// <summary>
/// One B-tree special file (catalog, extents overflow or attributes) of a volume held in memory,
/// edited as the ordered list of its leaf records and written back as a freshly packed tree.
/// </summary>
/// <remarks>
/// <para>
/// Layout per Apple TN1150 "B-Trees": node 0 is the header node (BTHeaderRec, user data record,
/// map record), every record lives in a leaf (kind -1, height 1) and the leaves are doubly linked;
/// index nodes (kind 0) above them hold the first key of each child and its node number, one level
/// per height up to the root; map nodes (kind 2) continue the header's node-allocation bitmap.
/// </para>
/// <para>
/// Writing repacks every record into leaves and rebuilds the index levels, so any insertion or
/// removal — one that splits a node, empties one, or changes the key an index names a node by —
/// leaves a tree in the shape TN1150 requires. Only the records' order and bytes carry over; which
/// node holds which record is free to change, as it is after the node splits and merges a mounted
/// volume makes. The file grows when the tree needs more nodes than it has, and map nodes are kept
/// where they are.
/// </para>
/// </remarks>
internal sealed class HfsPlusBTreeFile {
  private const int NodeDescriptorSize = 14;
  private const sbyte LeafKind = -1;
  private const sbyte IndexKind = 0;
  private const sbyte HeaderKind = 1;
  private const sbyte MapKind = 2;
  private const uint VariableIndexKeysMask = 4; // kBTVariableIndexKeysMask

  private readonly HfsPlusVolumeEditor _volume;
  private readonly Comparison<byte[]> _compare;
  private readonly List<(uint StartBlock, uint BlockCount)> _extents;
  private readonly List<uint> _mapNodes = [];
  private List<byte[]>? _records;

  /// <summary>Where this file's HFSPlusForkData sits in the volume header.</summary>
  internal int ForkOffset { get; }

  internal ushort NodeSize { get; }
  internal uint TotalNodes { get; private set; }
  internal ushort MaxKeyLength { get; }
  internal byte KeyCompareType { get; }
  internal uint BTreeAttributes { get; }

  /// <summary>Whether a record was inserted, removed or replaced since the file was read.</summary>
  internal bool IsDirty { get; private set; }

  private HfsPlusBTreeFile(HfsPlusVolumeEditor volume, int forkOffset, List<(uint, uint)> extents, Comparison<byte[]> compare,
      ushort nodeSize, uint totalNodes, ushort maxKeyLength, byte keyCompareType, uint attributes) {
    this._volume = volume;
    this.ForkOffset = forkOffset;
    this._extents = extents;
    this._compare = compare;
    this.NodeSize = nodeSize;
    this.TotalNodes = totalNodes;
    this.MaxKeyLength = maxKeyLength;
    this.KeyCompareType = keyCompareType;
    this.BTreeAttributes = attributes;
  }

  /// <summary>
  /// Opens the B-tree file whose fork the volume header holds at <paramref name="forkOffset"/>,
  /// or returns null when that fork is empty. <paramref name="compareFor"/> picks the key order
  /// from the header's keyCompareType.
  /// </summary>
  internal static HfsPlusBTreeFile? Open(HfsPlusVolumeEditor volume, int forkOffset, Func<byte, Comparison<byte[]>> compareFor, string name) {
    var extents = volume.ForkExtents(forkOffset, name);
    if (extents.Count == 0) return null;
    var forkBytes = extents.Sum(static e => (long)e.BlockCount) * volume.BlockSize;
    if (forkBytes < 512) throw new InvalidDataException($"HFS+: the {name} is smaller than a B-tree header node.");

    var probe = new HfsPlusBTreeFile(volume, forkOffset, extents, static (_, _) => 0, 512, 1, 0, 0, 0);
    var headerStart = probe.ForkToImage(0, NodeDescriptorSize + 106);
    var header = volume.Image.AsSpan((int)headerStart, NodeDescriptorSize + 106);
    if ((sbyte)header[8] != HeaderKind) throw new InvalidDataException($"HFS+: the {name}'s node 0 is not a header node.");
    var record = header[NodeDescriptorSize..];
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(record[18..]);
    if (nodeSize < 512 || (nodeSize & (nodeSize - 1)) != 0)
      throw new InvalidDataException($"HFS+: the {name} declares a node size of {nodeSize}.");
    var blocksPerNode = Math.Max(1u, nodeSize / volume.BlockSize);
    if (extents.Any(e => e.BlockCount % blocksPerNode != 0))
      throw new NotSupportedException($"HFS+: the {name} has an extent that splits a B-tree node.");
    var totalNodes = BinaryPrimitives.ReadUInt32BigEndian(record[22..]);
    if ((long)totalNodes * nodeSize > forkBytes)
      throw new InvalidDataException($"HFS+: the {name} declares more nodes than its fork holds.");
    var file = new HfsPlusBTreeFile(volume, forkOffset, extents, compareFor(record[37]), nodeSize, totalNodes,
      BinaryPrimitives.ReadUInt16BigEndian(record[20..]), record[37], BinaryPrimitives.ReadUInt32BigEndian(record[38..]));
    file.ReadMapNodes(name);
    return file;
  }

  /// <summary>
  /// Writes an empty B-tree into the fork <paramref name="extents"/> describe (a header node and
  /// nothing else) and opens it. The caller records the fork in the volume header.
  /// </summary>
  internal static HfsPlusBTreeFile CreateEmpty(HfsPlusVolumeEditor volume, int forkOffset, List<(uint, uint)> extents,
      ushort nodeSize, ushort maxKeyLength, byte keyCompareType, uint attributes, Comparison<byte[]> compare) {
    var forkBytes = extents.Sum(static e => (long)e.Item2) * volume.BlockSize;
    var totalNodes = (uint)(forkBytes / nodeSize);
    var file = new HfsPlusBTreeFile(volume, forkOffset, extents, compare, nodeSize, totalNodes, maxKeyLength, keyCompareType, attributes) {
      _records = [],
      IsDirty = true,
    };
    for (var n = 0u; n < totalNodes; n++) file.NodeSpan(n).Clear();
    var header = file.NodeSpan(0);
    header[8] = unchecked((byte)HeaderKind);
    BinaryPrimitives.WriteUInt16BigEndian(header[10..], 3);
    var record = header[NodeDescriptorSize..];
    BinaryPrimitives.WriteUInt16BigEndian(record[18..], nodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(record[20..], maxKeyLength);
    BinaryPrimitives.WriteUInt32BigEndian(record[22..], totalNodes);
    BinaryPrimitives.WriteUInt32BigEndian(record[32..], (uint)forkBytes);  // clumpSize
    record[37] = keyCompareType;
    BinaryPrimitives.WriteUInt32BigEndian(record[38..], attributes);
    // Offsets of BTHeaderRec (14), the 128-byte user data record (120), the map record (248)
    // and the free space that follows the map record, which fills the rest of the node.
    BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 2)..], 14);
    BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 4)..], 120);
    BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 6)..], 248);
    BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 8)..], (ushort)(nodeSize - 8));
    file.Flush();
    return file;
  }

  // ── Records ─────────────────────────────────────────────────────────────

  /// <summary>Every leaf record (key and data) in key order.</summary>
  internal IReadOnlyList<byte[]> Records => this._records ??= this.ReadRecords();

  /// <summary>The index of the record whose key equals <paramref name="key"/>'s, or the bitwise complement of where it would go.</summary>
  internal int Search(byte[] key) {
    var records = (List<byte[]>)this.Records;
    int low = 0, high = records.Count - 1;
    while (low <= high) {
      var mid = (low + high) >>> 1;
      var order = this._compare(records[mid], key);
      if (order == 0) return mid;
      if (order < 0) low = mid + 1;
      else high = mid - 1;
    }
    return ~low;
  }

  /// <summary>Inserts a record in key order; a record with an equal key already present is a caller error.</summary>
  internal void Insert(byte[] record) {
    var at = this.Search(record);
    if (at >= 0) throw new InvalidOperationException("HFS+: a B-tree record with this key already exists.");
    ((List<byte[]>)this.Records).Insert(~at, record);
    this.IsDirty = true;
  }

  /// <summary>Replaces the record at <paramref name="index"/> with one of the same key.</summary>
  internal void Replace(int index, byte[] record) {
    var records = (List<byte[]>)this.Records;
    if (this._compare(records[index], record) != 0) throw new InvalidOperationException("HFS+: a replaced B-tree record must keep its key.");
    records[index] = record;
    this.IsDirty = true;
  }

  /// <summary>Removes every record <paramref name="match"/> selects; returns how many went.</summary>
  internal int RemoveAll(Predicate<byte[]> match) {
    var removed = ((List<byte[]>)this.Records).RemoveAll(match);
    if (removed > 0) this.IsDirty = true;
    return removed;
  }

  /// <summary>The span of a record's key, its keyLength field included.</summary>
  internal static ReadOnlySpan<byte> Key(byte[] record) => record.AsSpan(0, 2 + BinaryPrimitives.ReadUInt16BigEndian(record));

  /// <summary>Where a leaf record's data starts: after the key, on an even offset.</summary>
  internal static int DataOffset(byte[] record) {
    var offset = 2 + BinaryPrimitives.ReadUInt16BigEndian(record);
    return offset + (offset & 1);
  }

  // ── Reading ─────────────────────────────────────────────────────────────

  private List<byte[]> ReadRecords() {
    var header = this.NodeSpan(0)[NodeDescriptorSize..];
    var leafRecords = BinaryPrimitives.ReadUInt32BigEndian(header[6..]);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(header[10..]);
    var records = new List<byte[]>((int)Math.Min(leafRecords, 1u << 20));
    var visited = new HashSet<uint>();
    for (var node = firstLeaf; node != 0;) {
      if (node >= this.TotalNodes || !visited.Add(node)) throw new InvalidDataException("HFS+: a B-tree leaf chain leaves the tree or loops.");
      var nd = this.NodeSpan(node);
      if ((sbyte)nd[8] != LeafKind) throw new InvalidDataException($"HFS+: B-tree node {node} on the leaf chain is not a leaf.");
      var count = BinaryPrimitives.ReadUInt16BigEndian(nd[10..]);
      for (var i = 0; i < count; i++) {
        var start = this.Offset(nd, i);
        var end = this.Offset(nd, i + 1);
        if (start < NodeDescriptorSize || end <= start || end > this.NodeSize - 2 * (count + 1))
          throw new InvalidDataException($"HFS+: B-tree node {node} has a record outside the node.");
        var record = nd[start..end].ToArray();
        if (record.Length < 2 || 2 + BinaryPrimitives.ReadUInt16BigEndian(record) > record.Length)
          throw new InvalidDataException($"HFS+: B-tree node {node} has a key longer than its record.");
        records.Add(record);
      }
      node = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
    if (records.Count != leafRecords)
      throw new InvalidDataException($"HFS+: the B-tree header counts {leafRecords} leaf records, its leaves hold {records.Count}.");
    for (var i = 1; i < records.Count; i++)
      if (this._compare(records[i - 1], records[i]) >= 0)
        throw new NotSupportedException("HFS+: a B-tree's keys are out of order; the in-place editor does not reorder them.");
    return records;
  }

  /// <summary>The map nodes the header node's fLink chain names; they keep their numbers.</summary>
  private void ReadMapNodes(string name) {
    var visited = new HashSet<uint> { 0 };
    for (var node = BinaryPrimitives.ReadUInt32BigEndian(this.NodeSpan(0)); node != 0;) {
      if (node >= this.TotalNodes || !visited.Add(node)) throw new InvalidDataException($"HFS+: the {name}'s map node chain leaves the tree or loops.");
      var nd = this.NodeSpan(node);
      if ((sbyte)nd[8] != MapKind) throw new InvalidDataException($"HFS+: the {name}'s node {node} on the map chain is not a map node.");
      this._mapNodes.Add(node);
      node = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
  }

  private int Offset(ReadOnlySpan<byte> node, int index) => BinaryPrimitives.ReadUInt16BigEndian(node[(this.NodeSize - 2 * (index + 1))..]);

  // ── Writing ─────────────────────────────────────────────────────────────

  /// <summary>Writes the records back as a freshly packed tree when they changed.</summary>
  internal void Flush() {
    if (!this.IsDirty) return;
    var records = (List<byte[]>)this.Records;
    // Each level is a list of nodes, each the (first, count) of its items on the level below
    // (records for the leaves); firstRecord names the leaf record each node starts with.
    var levels = new List<List<(int First, int Count)>>();
    var firstRecord = new List<int[]>();
    var leaves = Pack(records.Select(static r => Even(r.Length)).ToList(), this.NodeSize);
    if (leaves.Count > 0) {
      levels.Add(leaves);
      firstRecord.Add(leaves.Select(static l => l.First).ToArray());
    }
    while (levels.Count > 0 && levels[^1].Count > 1) {
      var childFirst = firstRecord[^1];
      var level = Pack(childFirst.Select(r => Even(this.IndexKeyLength(records[r]) + 4)).ToList(), this.NodeSize);
      levels.Add(level);
      firstRecord.Add(level.Select(n => childFirst[n.First]).ToArray());
    }

    var needed = 1 + this._mapNodes.Count + levels.Sum(static l => l.Count);
    if (needed > this.TotalNodes) this.Grow((uint)needed);
    if (this.MapCapacity() < this.TotalNodes)
      throw new NotSupportedException("HFS+: the B-tree needs more map nodes than it has; the in-place editor does not add map nodes.");

    // Node numbers: leaves first, then each index level, skipping the header and map nodes.
    var reserved = new HashSet<uint>(this._mapNodes) { 0 };
    var next = 1u;
    uint Take() {
      while (reserved.Contains(next)) next++;
      return next++;
    }
    var numbers = levels.Select(level => level.Select(_ => Take()).ToArray()).ToList();

    for (var n = 1u; n < this.TotalNodes; n++)
      if (!reserved.Contains(n)) this.NodeSpan(n).Clear();

    for (var height = 0; height < levels.Count; height++) {
      var level = levels[height];
      var nodes = numbers[height];
      for (var i = 0; i < level.Count; i++) {
        var nd = this.NodeSpan(nodes[i]);
        BinaryPrimitives.WriteUInt32BigEndian(nd, i + 1 < level.Count ? nodes[i + 1] : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(nd[4..], i > 0 ? nodes[i - 1] : 0u);
        nd[8] = unchecked((byte)(height == 0 ? LeafKind : IndexKind));
        nd[9] = (byte)(height + 1);
        var (first, count) = level[i];
        BinaryPrimitives.WriteUInt16BigEndian(nd[10..], (ushort)count);
        var at = NodeDescriptorSize;
        for (var r = 0; r < count; r++) {
          BinaryPrimitives.WriteUInt16BigEndian(nd[(this.NodeSize - 2 * (r + 1))..], (ushort)at);
          if (height == 0) {
            records[first + r].AsSpan().CopyTo(nd[at..]);
            at += records[first + r].Length;
          } else {
            var child = first + r;
            at += this.WriteIndexKey(nd[at..], records[firstRecord[height - 1][child]]);
            BinaryPrimitives.WriteUInt32BigEndian(nd[at..], numbers[height - 1][child]);
            at += 4;
          }
          at += at & 1;
        }
        BinaryPrimitives.WriteUInt16BigEndian(nd[(this.NodeSize - 2 * (count + 1))..], (ushort)at);
      }
    }

    var header = this.NodeSpan(0)[NodeDescriptorSize..];
    var used = 1 + this._mapNodes.Count + numbers.Sum(static n => n.Length);
    BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)levels.Count);                                  // treeDepth
    BinaryPrimitives.WriteUInt32BigEndian(header[2..], levels.Count == 0 ? 0u : numbers[^1][0]);           // rootNode
    BinaryPrimitives.WriteUInt32BigEndian(header[6..], (uint)records.Count);                               // leafRecords
    BinaryPrimitives.WriteUInt32BigEndian(header[10..], levels.Count == 0 ? 0u : numbers[0][0]);           // firstLeafNode
    BinaryPrimitives.WriteUInt32BigEndian(header[14..], levels.Count == 0 ? 0u : numbers[0][^1]);          // lastLeafNode
    BinaryPrimitives.WriteUInt32BigEndian(header[22..], this.TotalNodes);                                   // totalNodes
    BinaryPrimitives.WriteUInt32BigEndian(header[26..], this.TotalNodes - (uint)used);                     // freeNodes
    var inUse = new HashSet<uint>(reserved);
    foreach (var level in numbers) inUse.UnionWith(level);
    this.WriteMap(inUse);
    this.IsDirty = false;
  }

  private static int Even(int length) => length + (length & 1);

  /// <summary>How many bytes an index record's key takes for the given leaf record.</summary>
  private int IndexKeyLength(byte[] record)
    => (this.BTreeAttributes & VariableIndexKeysMask) != 0 ? Key(record).Length : 2 + this.MaxKeyLength;

  /// <summary>
  /// Writes the index key for <paramref name="record"/>: its own key when the tree uses variable
  /// index keys, else a key padded to maxKeyLength with keyLength saying so (TN1150).
  /// </summary>
  private int WriteIndexKey(Span<byte> destination, byte[] record) {
    var key = Key(record);
    if ((this.BTreeAttributes & VariableIndexKeysMask) != 0) {
      key.CopyTo(destination);
      return key.Length;
    }
    destination[..(2 + this.MaxKeyLength)].Clear();
    key[2..].CopyTo(destination[2..]);
    BinaryPrimitives.WriteUInt16BigEndian(destination, this.MaxKeyLength);
    return 2 + this.MaxKeyLength;
  }

  /// <summary>Greedily packs items of the given (even) sizes into nodes; returns (first, count) per node.</summary>
  private static List<(int First, int Count)> Pack(List<int> sizes, int nodeSize) {
    var nodes = new List<(int, int)>();
    int first = 0, used = NodeDescriptorSize;
    for (var i = 0; i < sizes.Count; i++) {
      var count = i - first;
      if (count > 0 && used + sizes[i] + 2 * (count + 2) > nodeSize) {
        nodes.Add((first, count));
        first = i;
        used = NodeDescriptorSize;
      }
      if (NodeDescriptorSize + sizes[i] + 4 > nodeSize) throw new NotSupportedException("HFS+: a B-tree record does not fit a node.");
      used += sizes[i];
    }
    if (sizes.Count > first) nodes.Add((first, sizes.Count - first));
    return nodes;
  }

  // ── Map ─────────────────────────────────────────────────────────────────

  /// <summary>The map records in order: the header node's third record, then each map node's only record.</summary>
  private List<(uint Node, int Start, int Length)> MapRecords() {
    var maps = new List<(uint, int, int)>();
    var header = this.NodeSpan(0);
    maps.Add((0, this.Offset(header, 2), this.Offset(header, 3) - this.Offset(header, 2)));
    foreach (var node in this._mapNodes) {
      var nd = this.NodeSpan(node);
      maps.Add((node, this.Offset(nd, 0), this.Offset(nd, 1) - this.Offset(nd, 0)));
    }
    return maps;
  }

  private long MapCapacity() => this.MapRecords().Sum(static m => (long)m.Length * 8);

  private void WriteMap(HashSet<uint> inUse) {
    var node = 0u;
    foreach (var (mapNode, start, length) in this.MapRecords()) {
      var bits = this.NodeSpan(mapNode).Slice(start, length);
      bits.Clear();
      for (var b = 0; b < length * 8; b++, node++)
        if (node < this.TotalNodes && inUse.Contains(node)) bits[b >> 3] |= (byte)(0x80 >> (b & 7));
    }
  }

  // ── Fork ────────────────────────────────────────────────────────────────

  /// <summary>
  /// Adds allocation blocks so the file holds at least <paramref name="nodes"/> nodes: as many
  /// as the file already has (up to 8 MiB, and at least its clump size) when the volume has them,
  /// so a growing tree takes few extents, else just what is needed. The run continues the last
  /// extent when the blocks after it are free, else becomes a new extent while there are fewer
  /// than eight.
  /// </summary>
  private void Grow(uint nodes) {
    var blockSize = this._volume.BlockSize;
    var forkBlocks = (uint)this._extents.Sum(static e => (long)e.BlockCount);
    var blocksPerNode = Math.Max(1u, this.NodeSize / blockSize);
    uint RoundToNodes(long blocks) => (uint)((blocks + blocksPerNode - 1) / blocksPerNode * blocksPerNode);
    var needed = RoundToNodes(((long)nodes * this.NodeSize - (long)forkBlocks * blockSize + blockSize - 1) / blockSize);
    var clump = BinaryPrimitives.ReadUInt32BigEndian(this._volume.VolumeHeader[(this.ForkOffset + 8)..]) / blockSize;
    var generous = RoundToNodes(Math.Max(needed, Math.Max(clump, Math.Min(forkBlocks, Math.Max(1u, (8u << 20) / blockSize)))));
    var (lastStart, lastCount) = this._extents[^1];
    foreach (var blocks in generous > needed ? new[] { generous, needed } : new[] { needed }) {
      if (this._volume.TryAllocateAt(lastStart + lastCount, blocks)) {
        this._extents[^1] = (lastStart, lastCount + blocks);
        break;
      }
      if (this._extents.Count < 8 && this._volume.AllocateContiguous(blocks) is { } start) {
        this._extents.Add((start, blocks));
        break;
      }
      if (blocks == needed)
        throw new NotSupportedException("HFS+: a B-tree file needs room to grow that the volume does not have in one more extent.");
    }
    this._volume.SetForkExtents(this.ForkOffset, this._extents);
    var grownBytes = this._extents.Sum(static e => (long)e.BlockCount) * blockSize;
    var oldTotal = this.TotalNodes;
    this.TotalNodes = (uint)(grownBytes / this.NodeSize);
    for (var n = oldTotal; n < this.TotalNodes; n++) this.NodeSpan(n).Clear();
  }

  /// <summary>The image offset of fork byte <paramref name="forkOffset"/>, which must not straddle an extent for <paramref name="length"/> bytes.</summary>
  private long ForkToImage(long forkOffset, int length) {
    var at = 0L;
    foreach (var (start, count) in this._extents) {
      var bytes = (long)count * this._volume.BlockSize;
      if (forkOffset < at + bytes) {
        if (forkOffset + length > at + bytes) throw new InvalidDataException("HFS+: a B-tree node straddles two extents.");
        var image = (long)start * this._volume.BlockSize + forkOffset - at;
        if (image + length > this._volume.Image.Length) throw new InvalidDataException("HFS+: a B-tree node lies past the end of the image.");
        return image;
      }
      at += bytes;
    }
    throw new InvalidDataException("HFS+: a B-tree node lies past the end of its fork.");
  }

  private Span<byte> NodeSpan(uint node) => this._volume.Image.AsSpan((int)this.ForkToImage((long)node * this.NodeSize, this.NodeSize), this.NodeSize);
}
