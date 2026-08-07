#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Assembles one b-tree node: its header, the format its keys are read under, and
/// the sorted run of keys itself.
/// </summary>
/// <remarks>
/// <para>A node is a whole bucket on disk, but only the part actually written
/// counts — the pointer to the node records how many sectors that is, and the
/// checksum covers exactly the words the node says it holds.</para>
///
/// <para>Every key here is written with its fields laid out in full rather than
/// packed against the node's format. Both are legal, a reader tells them apart by
/// a byte in each key, and the unpacked form is the one whose meaning is on the
/// face of it.</para>
/// </remarks>
internal sealed class BcacheFsNodeBuilder {

  private const int HeaderBytes = 136;   // csum, magic, flags, min/max key, unused pointer, format
  private const int BsetHeaderBytes = 24;
  internal const int KeysOffset = HeaderBytes + BsetHeaderBytes;

  private readonly List<Key> _keys = [];

  /// <summary>Which tree the node belongs to.</summary>
  internal required int BtreeId { get; init; }

  /// <summary>The node's own identity, which its pointer repeats.</summary>
  internal required ulong Seq { get; init; }

  /// <summary>The volume's magic, from which the node's is derived.</summary>
  internal required ulong SuperblockMagic { get; init; }

  /// <summary>Zero for a node holding keys, one for a node holding pointers to those.</summary>
  internal int Level { get; init; }

  /// <summary>The lowest position this node is responsible for.</summary>
  internal Bpos MinKey { get; init; } = Bpos.Min;

  /// <summary>The highest, inclusive.</summary>
  internal Bpos MaxKey { get; init; } = Bpos.Max;

  /// <summary>The keys the node holds, in order, once it has been written.</summary>
  internal IReadOnlyList<Key> Keys => this._keys;

  /// <summary>Adds a key. Keys are sorted before the node is written.</summary>
  internal void Add(Key key) => this._keys.Add(key);

  internal int Count => this._keys.Count;

  /// <summary>How many bytes the node needs, before rounding to whole sectors.</summary>
  internal int Bytes {
    get {
      var total = KeysOffset;
      foreach (var key in this._keys) total += key.Bytes;
      return total;
    }
  }

  /// <summary>How many sectors the node occupies, which its pointer records.</summary>
  internal int Sectors => (this.Bytes + SectorSize - 1) / SectorSize;

  /// <summary>
  /// Writes the node into <paramref name="destination" />, which must be at least a
  /// bucket long, and returns the sectors written.
  /// </summary>
  internal int Write(Span<byte> destination) {
    this._keys.Sort((a, b) => Compare(a.Position, b.Position));

    // A node is one bucket. Growing past it means a tree of more than one node,
    // with interior nodes indexing the leaves, which this does not write — so it
    // says so rather than laying keys over whatever follows.
    if (this.Bytes > destination.Length)
      throw new NotSupportedException(
        $"A bcachefs b-tree of {this._keys.Count} keys needs {this.Bytes:N0} bytes, "
        + $"more than the {destination.Length:N0} of a single node; this writer emits one node per tree.");

    var sectors = this.Sectors;
    destination[..(sectors * SectorSize)].Clear();

    // The magic is the volume's, folded with a constant that says "b-tree node".
    BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], this.SuperblockMagic ^ 0x90135c78b99e07f5UL);

    // flags: the tree's id in two pieces, the level, and the bit that says extents
    // in this node may overwrite one another. The sequence here counts node writes
    // and is not the identity the pointer matches — that is the bset's.
    var flags = (ulong)(uint)(this.BtreeId & 0xF)
      | ((ulong)(uint)(this.BtreeId >> 4) << 9)
      | ((ulong)(uint)(this.Level & 0xF) << 4)
      | (1UL << 8)
      | (1UL << 32);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], flags);

    // What this node is responsible for. A tree of one node covers all of it; a
    // tree of several splits the range between its leaves, and every position in
    // between has to fall inside exactly one of them.
    WriteBpos(destination[32..], this.MinKey);
    WriteBpos(destination[52..], this.MaxKey);

    // The key format: every field at its full width, which is what an unpacked key is.
    var format = destination[80..];
    format[0] = BkeyU64s;
    format[1] = 6;
    format[2] = 64; format[3] = 64; format[4] = 32; format[5] = 32; format[6] = 32; format[7] = 64;

    var keyBytes = 0;
    var cursor = destination[KeysOffset..];
    foreach (var key in this._keys) {
      var written = WriteKey(cursor, key);
      cursor = cursor[written..];
      keyBytes += written;
    }

    var bset = destination[HeaderBytes..];
    BinaryPrimitives.WriteUInt64LittleEndian(bset, this.Seq);
    BinaryPrimitives.WriteUInt64LittleEndian(bset[8..], 0);        // journal_seq
    BinaryPrimitives.WriteUInt32LittleEndian(bset[16..], CsumTypeCrc32CNonzero);
    BinaryPrimitives.WriteUInt16LittleEndian(bset[20..], BcacheFsFormat.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(bset[22..], (ushort)(keyBytes / 8));

    // The checksum covers everything after itself, up to the last word the node claims.
    var checksum = MetadataChecksum(destination[16..(KeysOffset + keyBytes)]);
    BinaryPrimitives.WriteUInt64LittleEndian(destination, checksum);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], 0);
    return sectors;
  }

  /// <summary>
  /// The pointer that names this node: where it is, how much of it was written, and
  /// the identity a reader checks against the node itself.
  /// </summary>
  internal Key Pointer(long sector, int sectorsWritten) {
    var value = new byte[48];
    BinaryPrimitives.WriteUInt64LittleEndian(value, 0);                        // mem_ptr
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), this.Seq);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(16), (ushort)sectorsWritten);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(18), 0);             // flags
    // A pointer repeats the range the node it names is responsible for: the low end
    // here, and the high end as the key's own position.
    WriteBpos(value.AsSpan(20), this.MinKey);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40), ExtentPointer(sector));
    return new Key(KeyBtreePtrV2, this.MaxKey, 0, value);
  }
}
