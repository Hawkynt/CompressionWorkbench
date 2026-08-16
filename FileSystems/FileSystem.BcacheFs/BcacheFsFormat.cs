#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.BcacheFs;

/// <summary>
/// The on-disk vocabulary of a bcachefs volume: where its numbers live, how its
/// checksums are taken, and how its keys are shaped.
/// </summary>
/// <remarks>
/// <para>bcachefs keeps everything — inodes, directory entries, extents — in
/// b-trees, and a b-tree node is a header, a key format, and one or more sorted
/// runs of keys called bsets. A key is a position and a value; the position is a
/// triple of inode, offset and snapshot, and which of the three carries what
/// depends on the tree. This file holds the pieces every part of that shares.</para>
///
/// <para>The two checksums are not the same function. A metadata block's is
/// CRC-32C seeded with ones and inverted at the end — what the format calls
/// <c>crc32c_nonzero</c> — while an extent's is the same polynomial seeded with
/// zero and not inverted. Using one where the other belongs produces a number
/// that looks right and is refused.</para>
/// </remarks>
internal static class BcacheFsFormat {

  // ── Geometry ────────────────────────────────────────────────────────────

  internal const int SectorSize = 512;

  /// <summary>Sectors per bucket, and therefore per b-tree node.</summary>
  internal const int BucketSectors = 128;

  internal const int BucketBytes = BucketSectors * SectorSize;

  /// <summary>Where the primary superblock starts.</summary>
  internal const long PrimarySbSector = 8;

  /// <summary>Where the standalone copy of the layout lives.</summary>
  internal const long LayoutSector = 7;

  /// <summary>Base-2 log of the sectors a superblock slot may occupy.</summary>
  internal const byte SbMaxSizeBits = 11;

  internal const int SbSlotSectors = 1 << SbMaxSizeBits;

  // ── Superblock ──────────────────────────────────────────────────────────

  internal const int SbFixedBytes = 752;      // everything before start[0]
  internal const int SbLayoutOffset = 240;
  internal const int SbLayoutBytes = 16 + 8 + 61 * 8;

  internal static readonly byte[] Magic = [
    0xC6, 0x85, 0x73, 0xF6, 0x66, 0xCE, 0x90, 0xA9,
    0xD9, 0x6A, 0x60, 0xCF, 0x80, 0x3D, 0xF7, 0xEF,
  ];

  /// <summary>Metadata version 1.38, which is what a current kernel writes.</summary>
  internal const ushort Version = (1 << 10) | 38;

  /// <summary>The floor an initialised volume is held to.</summary>
  internal const ushort VersionMin = (0 << 10) | 14;

  // Superblock section types.
  internal const uint FieldJournal = 0;
  internal const uint FieldClean = 6;
  internal const uint FieldJournalV2 = 9;
  internal const uint FieldMembersV2 = 11;
  internal const uint FieldErrors = 12;
  internal const uint FieldExt = 13;

  /// <summary>Bytes one member entry occupies in a members_v2 section.</summary>
  internal const int MemberBytes = 296;

  // Feature bits.
  internal const ulong FeatureNewSiphash = 1UL << 7;
  internal const ulong FeatureNewExtentOverwrite = 1UL << 9;
  internal const ulong FeatureBtreePtrV2 = 1UL << 11;
  internal const ulong FeatureExtentsAboveBtreeUpdates = 1UL << 12;
  internal const ulong FeatureBtreeUpdatesJournalled = 1UL << 13;
  internal const ulong FeatureNewVarint = 1UL << 15;
  internal const ulong FeatureJournalNoFlush = 1UL << 16;
  internal const ulong FeatureAllocV2 = 1UL << 17;
  internal const ulong FeatureExtentsAcrossBtreeNodes = 1UL << 18;
  internal const ulong FeatureIncompatVersionField = 1UL << 19;
  internal const ulong FeatureNoAllocInfo = 1UL << 21;

  /// <summary>
  /// Says the volume is an image file that was never sized to a device.
  /// </summary>
  /// <remarks>
  /// It is exactly what a volume written whole is, and saying so is what lets a
  /// mount skip building the free-space information it would otherwise stop to
  /// build — which, on a read-only mount, it cannot. A kernel that reads this bit
  /// mounts the volume read-only and says why.
  /// </remarks>
  internal const ulong FeatureSmallImage = 1UL << 22;

  /// <summary>Compat bits a volume written whole can claim.</summary>
  internal const ulong CompatAllocInfo = 1UL << 0;
  internal const ulong CompatAllocMetadata = 1UL << 1;
  internal const ulong CompatExtentsAboveBtreeUpdatesDone = 1UL << 2;
  internal const ulong CompatBformatOverflowDone = 1UL << 3;
  internal const ulong CompatNoStalePtrs = 1UL << 5;

  // ── B-tree ids ──────────────────────────────────────────────────────────

  internal const int BtreeExtents = 0;
  internal const int BtreeInodes = 1;
  internal const int BtreeDirents = 2;
  internal const int BtreeAlloc = 4;
  internal const int BtreeSubvolumes = 8;
  internal const int BtreeSnapshots = 9;
  internal const int BtreeFreespace = 11;
  internal const int BtreeBackpointers = 13;
  internal const int BtreeAccounting = 20;
  internal const int BtreeNeedDiscard = 12;
  internal const int BtreeBucketGens = 14;
  internal const int BtreeSnapshotTrees = 15;
  internal const int BtreeLoggedOps = 17;

  // ── Key types ───────────────────────────────────────────────────────────

  internal const byte KeyExtent = 6;
  internal const byte KeyDirent = 10;
  internal const byte KeyBtreePtrV2 = 18;
  internal const byte KeySubvolume = 21;
  internal const byte KeySnapshot = 22;
  internal const byte KeySet = 25;
  internal const byte KeyAllocV4 = 27;
  internal const byte KeyInodeV3 = 29;
  internal const byte KeyBucketGens = 30;
  internal const byte KeyBackpointer = 28;
  internal const byte KeyAccounting = 34;
  internal const byte KeySnapshotTree = 31;
  internal const byte KeyInodeAllocCursor = 35;

  /// <summary>What a bucket holds, as the alloc key records it.</summary>
  internal const byte DataFree = 0;
  internal const byte DataSb = 1;
  internal const byte DataJournal = 2;
  internal const byte DataBtree = 3;
  internal const byte DataUser = 4;

  /// <summary>Accounting key types, as the type tag that opens the position.</summary>
  internal const byte AccountingNrInodes = 0;
  internal const byte AccountingReplicas = 2;
  internal const byte AccountingDevDataType = 3;
  internal const byte AccountingSnapshot = 5;
  internal const byte AccountingBtree = 6;

  /// <summary>
  /// How far a backpointer's position is shifted above the sector it names.
  /// </summary>
  /// <remarks>
  /// A backpointer is keyed by where in the device its target sits, with room
  /// below for an offset inside the bucket, so the sector is shifted up and the
  /// low bits carry that offset.
  /// </remarks>
  internal const int ExtentBpShift = 16;

  /// <summary>How many buckets one bucket_gens key covers.</summary>
  internal const int BucketGensNr = 256;

  /// <summary>A bkey header is five 64-bit words before the value starts.</summary>
  internal const int BkeyU64s = 5;
  internal const int BkeyBytes = BkeyU64s * 8;

  /// <summary>Keys written with this format carry their fields unpacked.</summary>
  internal const byte KeyFormatCurrent = 1;

  internal const uint SnapshotIdMax = uint.MaxValue;

  /// <summary>The first inode number a file may take; below it is reserved.</summary>
  internal const ulong FirstUserInode = 4096;

  /// <summary>
  /// Where a volume's own structures end and its files may begin: the two front
  /// superblock slots, the journal, and one bucket for each b-tree.
  /// </summary>
  internal const long MetadataEndBytes = (33L + 16 + 64) * BucketBytes;

  /// <summary>The root directory's inode number.</summary>
  internal const ulong RootInode = 4096;

  /// <summary>The only subvolume a volume written here has.</summary>
  internal const uint RootSubvolume = 1;

  // Directory entry types, as in POSIX d_type.
  internal const byte DtDir = 4;
  internal const byte DtReg = 8;

  // ── Checksums ───────────────────────────────────────────────────────────

  private static readonly uint[] Crc32CTable = BuildCrc32CTable();

  private static uint[] BuildCrc32CTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var bit = 0; bit < 8; ++bit)
        c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78u : c >> 1;
      table[i] = c;
    }

    return table;
  }

  /// <summary>CRC-32C over <paramref name="data" />, continuing from <paramref name="crc" />.</summary>
  internal static uint Crc32C(uint crc, ReadOnlySpan<byte> data) {
    foreach (var b in data)
      crc = (crc >> 8) ^ Crc32CTable[(crc ^ b) & 0xFF];
    return crc;
  }

  /// <summary>
  /// The checksum a metadata block carries: CRC-32C seeded with ones and inverted.
  /// </summary>
  internal static ulong MetadataChecksum(ReadOnlySpan<byte> data)
    => Crc32C(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

  /// <summary>The checksum an extent carries: the same polynomial, seeded with zero.</summary>
  internal static uint DataChecksum(ReadOnlySpan<byte> data) => Crc32C(0, data);

  /// <summary>Checksum type 1: CRC-32C with the seed and the final inversion.</summary>
  internal const int CsumTypeCrc32CNonzero = 1;

  /// <summary>Checksum type 5: CRC-32C with neither.</summary>
  internal const int CsumTypeCrc32C = 5;

  // ── SipHash-2-4 ─────────────────────────────────────────────────────────

  /// <summary>
  /// The hash a directory entry's position is taken from.
  /// </summary>
  /// <remarks>
  /// The key is the directory inode's own hash seed in the first half and zero in
  /// the second, and the result is shifted right by one — the top bit of a
  /// directory entry's offset is not the hash's to use.
  /// </remarks>
  internal static ulong SipHash24(ulong k0, ulong k1, ReadOnlySpan<byte> data) {
    var v0 = 0x736f6d6570736575UL ^ k0;
    var v1 = 0x646f72616e646f6dUL ^ k1;
    var v2 = 0x6c7967656e657261UL ^ k0;
    var v3 = 0x7465646279746573UL ^ k1;

    void Round() {
      v0 += v1; v1 = System.Numerics.BitOperations.RotateLeft(v1, 13); v1 ^= v0;
      v0 = System.Numerics.BitOperations.RotateLeft(v0, 32);
      v2 += v3; v3 = System.Numerics.BitOperations.RotateLeft(v3, 16); v3 ^= v2;
      v0 += v3; v3 = System.Numerics.BitOperations.RotateLeft(v3, 21); v3 ^= v0;
      v2 += v1; v1 = System.Numerics.BitOperations.RotateLeft(v1, 17); v1 ^= v2;
      v2 = System.Numerics.BitOperations.RotateLeft(v2, 32);
    }

    var length = data.Length;
    var whole = length & ~7;
    for (var i = 0; i < whole; i += 8) {
      var m = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
      v3 ^= m;
      Round(); Round();
      v0 ^= m;
    }

    var tail = ((ulong)length & 0xFF) << 56;
    for (var i = 0; i < length - whole; ++i)
      tail |= (ulong)data[whole + i] << (8 * i);

    v3 ^= tail;
    Round(); Round();
    v0 ^= tail;

    v2 ^= 0xFF;
    Round(); Round(); Round(); Round();
    return v0 ^ v1 ^ v2 ^ v3;
  }

  /// <summary>Where a directory entry sits inside its directory.</summary>
  internal static ulong DirentHash(ulong hashSeed, string name) {
    var bytes = Encoding.UTF8.GetBytes(name);
    var hash = SipHash24(hashSeed, 0, bytes) >> 1;
    // Offsets zero and one are the directory's own two dots.
    return Math.Max(hash, 2);
  }

  // ── Varints ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes one inode field, in the encoding the inode's field list uses.
  /// </summary>
  /// <remarks>
  /// The length is in the low bits of the first byte: <c>n - 1</c> set bits then a
  /// clear one, with the value shifted up above them. Nine bytes is the escape,
  /// where the first byte is all ones and the value follows whole.
  /// </remarks>
  internal static int WriteVarint(Span<byte> destination, ulong value) {
    var bits = 64 - System.Numerics.BitOperations.LeadingZeroCount(value | 1);
    var bytes = (bits + 6) / 7;

    if (bytes < 9) {
      var shifted = value << bytes;
      if (bytes > 1) shifted |= ~(~0UL << (bytes - 1));
      Span<byte> word = stackalloc byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(word, shifted);
      word[..bytes].CopyTo(destination);
      return bytes;
    }

    destination[0] = 0xFF;
    BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], value);
    return 9;
  }

  /// <summary>Reads one inode field back, returning how many bytes it took.</summary>
  internal static int ReadVarint(ReadOnlySpan<byte> source, out ulong value) {
    if (source.IsEmpty) { value = 0; return 0; }

    var first = source[0];
    if (first == 0xFF) {
      value = source.Length >= 9 ? BinaryPrimitives.ReadUInt64LittleEndian(source[1..]) : 0;
      return 9;
    }

    var bytes = System.Numerics.BitOperations.TrailingZeroCount((uint)~first) + 1;
    if (bytes > source.Length) { value = 0; return source.Length; }

    Span<byte> word = stackalloc byte[8];
    source[..bytes].CopyTo(word);
    value = BinaryPrimitives.ReadUInt64LittleEndian(word) >> bytes;
    return bytes;
  }

  // ── Keys ────────────────────────────────────────────────────────────────

  /// <summary>A position in a b-tree: which inode, where in it, and in which snapshot.</summary>
  internal readonly record struct Bpos(ulong Inode, ulong Offset, uint Snapshot) {
    internal static readonly Bpos Min = new(0, 0, 0);
    internal static readonly Bpos Max = new(ulong.MaxValue, ulong.MaxValue, uint.MaxValue);
  }

  /// <summary>Writes a position where a structure embeds one: snapshot first, then offset, then inode.</summary>
  internal static void WriteBpos(Span<byte> destination, Bpos position) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination, position.Snapshot);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], position.Offset);
    BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], position.Inode);
  }

  /// <summary>The position immediately after this one.</summary>
  /// <remarks>
  /// It is what separates one b-tree node from the next: a node's range ends at a
  /// key it holds, and its neighbour's begins at the position after it, so that no
  /// position falls in both or in neither.
  /// </remarks>
  internal static Bpos Successor(Bpos position) {
    if (position.Snapshot != uint.MaxValue)
      return position with { Snapshot = position.Snapshot + 1 };
    if (position.Offset != ulong.MaxValue)
      return new Bpos(position.Inode, position.Offset + 1, 0);
    return new Bpos(position.Inode + 1, 0, 0);
  }

  internal static Bpos ReadBpos(ReadOnlySpan<byte> source) => new(
    BinaryPrimitives.ReadUInt64LittleEndian(source[12..]),
    BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
    BinaryPrimitives.ReadUInt32LittleEndian(source));

  /// <summary>One key and its value, ready to be laid into a b-tree node.</summary>
  /// <param name="Type">Which of the key types the value is.</param>
  /// <param name="Position">Where the key sorts.</param>
  /// <param name="Size">For an extent, how many sectors it covers; otherwise zero.</param>
  /// <param name="Value">The value's bytes, which are padded out to a whole number of words.</param>
  internal readonly record struct Key(byte Type, Bpos Position, uint Size, byte[] Value) {

    /// <summary>How many 64-bit words the key and its value occupy together.</summary>
    internal int U64s => BkeyU64s + (this.Value.Length + 7) / 8;

    internal int Bytes => this.U64s * 8;
  }

  /// <summary>Lays one key into <paramref name="destination" /> and returns its length.</summary>
  internal static int WriteKey(Span<byte> destination, Key key) {
    var bytes = key.Bytes;
    destination[..bytes].Clear();
    destination[0] = (byte)key.U64s;
    destination[1] = KeyFormatCurrent;
    destination[2] = key.Type;
    // bversion stays zero: nothing here is written twice.
    BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], key.Size);
    WriteBpos(destination[20..], key.Position);
    key.Value.CopyTo(destination[BkeyBytes..]);
    return bytes;
  }

  /// <summary>Orders keys the way a b-tree node must have them.</summary>
  internal static int Compare(Bpos a, Bpos b) {
    if (a.Inode != b.Inode) return a.Inode < b.Inode ? -1 : 1;
    if (a.Offset != b.Offset) return a.Offset < b.Offset ? -1 : 1;
    return a.Snapshot == b.Snapshot ? 0 : a.Snapshot < b.Snapshot ? -1 : 1;
  }

  // ── Extent pointers ─────────────────────────────────────────────────────

  /// <summary>
  /// The word that says where an extent's bytes are.
  /// </summary>
  /// <remarks>
  /// The low bit marks the word as a pointer rather than a checksum, and the
  /// position is a sector on the device, not a byte.
  /// </remarks>
  internal static ulong ExtentPointer(long sector, byte device = 0, byte generation = 0)
    => 1UL | ((ulong)sector << 4) | ((ulong)device << 48) | ((ulong)generation << 56);

  /// <summary>The sector an extent pointer names.</summary>
  internal static long PointerSector(ulong pointer) => (long)((pointer >> 4) & ((1UL << 44) - 1));

  /// <summary>Whether a word in an extent's value is a pointer.</summary>
  internal static bool IsPointer(ulong entry) => (entry & 1) != 0;

  /// <summary>
  /// The word pair that describes an extent's checksum and its size.
  /// </summary>
  /// <remarks>
  /// The sizes are stored one less than they are, so a one-sector extent records
  /// zero; the entry type is two, which is the second bit rather than the second
  /// value.
  /// </remarks>
  internal static ulong ExtentCrc32(int sectors, uint checksum) {
    var word = 2UL;                                   // entry type: crc32
    word |= (ulong)(sectors - 1) << 2;                // compressed size
    word |= (ulong)(sectors - 1) << 9;                // uncompressed size
    word |= (ulong)CsumTypeCrc32C << 24;
    return word | ((ulong)checksum << 32);
  }
}
