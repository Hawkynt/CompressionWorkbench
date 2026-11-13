using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NvList = FileSystem.Zfs.XdrNvListWriter.NvList;

namespace FileSystem.Zfs;

/// <summary>
/// Writes a single-device ZFS pool image (pool version 28, ZPL version 3) holding one
/// filesystem with the given files and directories, laid out as <c>docs/ZFS-ON-DISK.md</c>
/// ("Writing a pool") describes: the objects <c>zpool create</c> leaves in the MOS, one metaslab
/// space map per metaslab that holds blocks, and every block uncompressed with a Fletcher-4
/// checksum, allocated front to back after the 4 MiB label and boot area.
/// </summary>
/// <remarks>
/// The pool is written exported (label state 1), so <c>zpool import</c> takes it without
/// <c>-f</c>. Every block is born in one transaction group.
/// </remarks>
internal sealed class ZfsWriter {
  // ── constants ──────────────────────────────────────────────────────────────────────────
  private const ulong PoolVersion = 28;
  private const ulong ZplVersion = 3;
  private const ulong Txg = 4;
  private const int Sector = 512;
  private const int Ashift = 9;
  private const int DnodeSize = 512;
  private const int DnodeBlockSize = 16 * 1024;
  private const int IndirectShift = 14;                // 16 KiB indirect blocks
  private const int IndirectSize = 1 << IndirectShift;
  private const int PointersPerIndirect = IndirectSize / ZfsConstants.BlockPointerSize;
  private const int MaxDataBlock = 128 * 1024;
  private const int MicroZapEntry = 64;
  private const int MicroZapNameMax = 49;
  private const int FatZapBlock = 16 * 1024;
  private const long MinimumDevice = 64L * 1024 * 1024;
  private const int MetaslabShift = 24;

  private const byte OtObjectDirectory = 1, OtObjectArray = 2, OtPackedNvList = 3, OtPackedNvListSize = 4, OtBpobj = 5,
    OtBpobjHeader = 6, OtSpaceMapHeader = 7, OtSpaceMap = 8, OtDnode = 10, OtObjset = 11, OtDslDir = 12, OtDslDirChildMap = 13,
    OtDslSnapMap = 14, OtDslProps = 15, OtDslDataset = 16, OtZnode = 17, OtPlainFile = 19, OtDirectory = 20, OtMasterNode = 21,
    OtUnlinkedSet = 22, OtNextClones = 37, OtDeadlist = 50, OtDeadlistHeader = 51, OtDslClones = 52;

  private const ulong ZbtMicro = 0x8000000000000003UL, ZbtHeader = 0x8000000000000001UL, ZbtLeaf = 0x8000000000000000UL;
  private const ulong ZapMagic = 0x2F52AB2ABUL;
  private const uint ZapLeafMagic = 0x2AB1EAF;
  private const ulong Crc64Polynomial = 0xC96C5795D7870F42UL;
  private const int ZapHashBits = 28;

  // ── input tree ─────────────────────────────────────────────────────────────────────────
  private sealed class Node {
    public required string Name;
    public bool IsDirectory;
    public byte[] Data = [];
    public DateTime Modified = DateTime.UtcNow;
    public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal);
    public ulong Object;
  }

  private readonly Node _root = new() { Name = "", IsDirectory = true };

  /// <summary>Adds a file; missing parent directories are created.</summary>
  public void AddFile(string path, byte[] data, DateTime? modified = null) {
    var node = this.Walk(path, directory: false);
    node.Data = data;
    if (modified is { } m)
      node.Modified = m.ToUniversalTime();
  }

  /// <summary>Adds an (empty) directory; missing parents are created.</summary>
  public void AddDirectory(string path, DateTime? modified = null) {
    var node = this.Walk(path, directory: true);
    if (modified is { } m)
      node.Modified = m.ToUniversalTime();
  }

  private Node Walk(string path, bool directory) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      return this._root;
    var cur = this._root;
    for (var i = 0; i < parts.Length; ++i) {
      var last = i == parts.Length - 1;
      var name = parts[i];
      if (name is "." or ".." || Encoding.UTF8.GetByteCount(name) > 255)
        throw new ArgumentException($"ZFS: '{name}' is not a usable file name.");
      if (!cur.Children.TryGetValue(name, out var next)) {
        next = new Node { Name = name, IsDirectory = !last || directory };
        cur.Children[name] = next;
      } else if (!next.IsDirectory && (!last || directory))
        throw new ArgumentException($"ZFS: '{path}' needs '{name}' to be a directory, but it is a file.");
      cur = next;
    }
    return cur;
  }

  // ── blocks and objects ─────────────────────────────────────────────────────────────────

  /// <summary>One allocated block: where it sits (after the 4 MiB start) and what it holds once filled.</summary>
  private sealed class Block {
    public long Offset;
    public int Size;                 // logical = physical (uncompressed), a multiple of 512
    public byte Type;
    public int Level;
    public ulong Fill;
    public byte[] Data = [];
    public readonly byte[] Checksum = new byte[32];

    public void Seal() => ZfsChecksum.Fletcher4(this.Data, this.Checksum);
  }

  /// <summary>A DMU object: its dnode fields, its level-0 blocks (null = hole) and its indirect tree.</summary>
  private sealed class DmuObject {
    public byte Type;
    public byte BonusType;
    public byte[] Bonus = [];
    public int DataBlockSize = Sector;
    public readonly List<Block?> Level0 = [];
    public readonly List<List<Block?>> Levels = [];   // Levels[0] = level-1 indirect blocks, …
    public int LevelCount = 1;
    public Block?[] Top = [null];                      // the dnode's block pointer(s)
    public int BlockPointers = 1;                      // dn_nblkptr; the bonus follows them
    public long UsedBytes;
  }

  private long _next;
  private readonly List<Block> _blocks = [];

  private Block Allocate(int size, byte type, int level) {
    size = (size + Sector - 1) & ~(Sector - 1);
    // a block never straddles a metaslab: metaslabs are at least 16 MiB and start on multiples of it
    if (this._next >> MetaslabShift != (this._next + size - 1) >> MetaslabShift)
      this._next = (this._next >> MetaslabShift) + 1 << MetaslabShift;
    var block = new Block { Offset = this._next, Size = size, Type = type, Level = level, Data = new byte[size] };
    this._next += size;
    this._blocks.Add(block);
    return block;
  }

  /// <summary>Allocates an object's data blocks (contents set now) and its indirect blocks (filled by <see cref="SealObject"/>).</summary>
  private DmuObject NewObject(byte type, byte bonusType, byte[] bonus, int blockSize, IReadOnlyList<byte[]?> blocks) {
    var obj = new DmuObject { Type = type, BonusType = bonusType, Bonus = bonus, DataBlockSize = blockSize };
    foreach (var content in blocks) {
      if (content == null) {
        obj.Level0.Add(null);
        continue;
      }
      var b = this.Allocate(blockSize, type, 0);
      content.CopyTo(b.Data, 0);
      b.Fill = 1;
      obj.Level0.Add(b);
    }
    this.AllocateIndirects(obj);
    return obj;
  }

  private void AllocateIndirects(DmuObject obj) {
    List<Block?> level = obj.Level0;
    while (level.Count > 1) {
      var parents = new List<Block?>();
      for (var i = 0; i < level.Count; i += PointersPerIndirect) {
        var any = false;
        for (var j = i; j < Math.Min(level.Count, i + PointersPerIndirect); ++j)
          any |= level[j] != null;
        parents.Add(any ? this.Allocate(IndirectSize, level.First(b => b != null)!.Type, obj.Levels.Count + 1) : null);
      }
      obj.Levels.Add(parents);
      level = parents;
    }
    obj.LevelCount = obj.Levels.Count + 1;
    obj.Top = [level.Count == 0 ? null : level[0]];
  }

  /// <summary>Checksums the object's data blocks, then fills and checksums its indirect blocks bottom-up.</summary>
  private static void SealObject(DmuObject obj) {
    long used = 0;
    foreach (var b in obj.Level0) {
      if (b == null) continue;
      b.Seal();
      used += b.Size;
    }
    var children = obj.Level0;
    foreach (var parents in obj.Levels) {
      for (var p = 0; p < parents.Count; ++p) {
        var parent = parents[p];
        if (parent == null) continue;
        ulong fill = 0;
        for (var j = 0; j < PointersPerIndirect && p * PointersPerIndirect + j < children.Count; ++j) {
          var child = children[p * PointersPerIndirect + j];
          if (child == null) continue;
          WriteBlockPointer(parent.Data.AsSpan(j * ZfsConstants.BlockPointerSize), child);
          fill += child.Fill;
        }
        parent.Fill = fill;
        parent.Seal();
        used += parent.Size;
      }
      children = parents;
    }
    obj.UsedBytes = used;
  }

  private static void WriteBlockPointer(Span<byte> dst, Block? b) {
    dst[..ZfsConstants.BlockPointerSize].Clear();
    if (b == null)
      return;
    var sectors = (ulong)(b.Size / Sector);
    BinaryPrimitives.WriteUInt64LittleEndian(dst, sectors);                                 // DVA 0: asize, vdev 0
    BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], (ulong)b.Offset >> Ashift);          // offset, not ganged
    var prop = (sectors - 1)                                                                 // logical size
      | (sectors - 1) << 16                                                                  // physical size
      | (ulong)ZfsConstants.CompressOff << 32
      | (ulong)ZfsConstants.ChecksumFletcher4 << 40
      | (ulong)b.Type << 48
      | (ulong)b.Level << 56
      | 1UL << 63;                                                                           // little-endian
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x30..], prop);
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x48..], Txg);                             // physical birth
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x50..], Txg);                             // logical birth
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x58..], b.Fill);
    b.Checksum.CopyTo(dst[0x60..]);
  }

  /// <summary>The 512-byte dnode for <paramref name="obj"/>.</summary>
  private static void WriteDnode(Span<byte> dst, DmuObject obj) {
    dst[..DnodeSize].Clear();
    dst[0] = obj.Type;
    dst[1] = IndirectShift;
    dst[2] = (byte)obj.LevelCount;
    dst[3] = (byte)obj.BlockPointers;
    dst[4] = obj.BonusType;
    dst[7] = 1;                                   // DNODE_FLAG_USED_BYTES
    BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], (ushort)(obj.DataBlockSize / Sector));
    BinaryPrimitives.WriteUInt16LittleEndian(dst[10..], (ushort)obj.Bonus.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x10..], (ulong)Math.Max(0, obj.Level0.Count - 1));
    BinaryPrimitives.WriteUInt64LittleEndian(dst[0x18..], (ulong)obj.UsedBytes);
    WriteBlockPointer(dst[0x40..], obj.Top[0]);
    obj.Bonus.CopyTo(dst[(0x40 + obj.BlockPointers * ZfsConstants.BlockPointerSize)..]);
  }

  /// <summary>An object set: its objects by number, the dnode blocks holding them, and the objset block.</summary>
  private sealed class ObjectSet {
    public readonly SortedDictionary<ulong, DmuObject> Objects = [];
    public DmuObject Meta = null!;
    public Block Root = null!;
    public ulong Type;
  }

  /// <summary>Allocates the dnode blocks and the objset block for <paramref name="os"/> (its objects are allocated).</summary>
  private void AllocateObjectSet(ObjectSet os, ulong maxObject) {
    var dnodeBlocks = (int)((maxObject + 1) * DnodeSize + DnodeBlockSize - 1) / DnodeBlockSize;
    var level0 = new List<byte[]?>();
    for (var i = 0; i < dnodeBlocks; ++i) {
      var first = (ulong)i * (DnodeBlockSize / DnodeSize);
      var any = os.Objects.Keys.Any(k => k >= first && k < first + DnodeBlockSize / DnodeSize);
      level0.Add(any ? new byte[DnodeBlockSize] : null);
    }
    os.Meta = this.NewObject(OtDnode, 0, [], DnodeBlockSize, level0);
    os.Root = this.Allocate(1024, OtObjset, 0);
  }

  /// <summary>Writes every object's dnode into the dnode blocks, then seals them and the objset block.</summary>
  private static void SealObjectSet(ObjectSet os) {
    foreach (var obj in os.Objects.Values)
      SealObject(obj);
    var perBlock = DnodeBlockSize / DnodeSize;
    foreach (var b in os.Meta.Level0)
      if (b != null)
        b.Fill = 0;
    foreach (var (number, obj) in os.Objects) {
      var block = os.Meta.Level0[(int)(number / (ulong)perBlock)]!;
      WriteDnode(block.Data.AsSpan((int)(number % (ulong)perBlock) * DnodeSize), obj);
      ++block.Fill;                                  // a dnode block's fill counts its objects
    }
    SealObject(os.Meta);
    var root = os.Root;
    root.Data.AsSpan().Clear();
    WriteDnode(root.Data, os.Meta);
    BinaryPrimitives.WriteUInt64LittleEndian(root.Data.AsSpan(ZfsConstants.ObjsetTypeOffset), os.Type);
    root.Fill = (ulong)os.Objects.Count;
    root.Seal();
  }

  /// <summary>Total allocated bytes of every block of the object set (objects, dnode blocks, objset block).</summary>
  private static long ObjectSetBytes(ObjectSet os) =>
    os.Objects.Values.Sum(o => o.UsedBytes) + os.Meta.UsedBytes + os.Root.Size;

  // ── ZAPs ───────────────────────────────────────────────────────────────────────────────

  private static readonly ulong[] Crc64Table = BuildCrc64();

  private static ulong[] BuildCrc64() {
    var t = new ulong[256];
    for (var i = 0; i < 256; ++i) {
      var c = (ulong)i;
      for (var k = 0; k < 8; ++k)
        c = (c >> 1) ^ ((c & 1) != 0 ? Crc64Polynomial : 0);
      t[i] = c;
    }
    return t;
  }

  /// <summary>A ZAP (microzap when every name fits and the block stays within 128 KiB, else a fat ZAP).</summary>
  internal static (int BlockSize, List<byte[]?> Blocks) BuildZap(IReadOnlyList<(string Name, ulong Value)> entries) {
    var micro = entries.All(e => Encoding.UTF8.GetByteCount(e.Name) <= MicroZapNameMax) && (entries.Count + 1) * MicroZapEntry <= MaxDataBlock;
    if (micro) {
      var size = Sector;
      while (size < (entries.Count + 1) * MicroZapEntry)
        size <<= 1;
      var block = new byte[size];
      var salt = NewSalt();
      BinaryPrimitives.WriteUInt64LittleEndian(block, ZbtMicro);
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), salt);
      var cdByHash = new Dictionary<ulong, uint>();
      for (var i = 0; i < entries.Count; ++i) {
        var at = (i + 1) * MicroZapEntry;
        var name = Encoding.UTF8.GetBytes(entries[i].Name);
        // names whose hashes collide are told apart by the collision differentiator
        var hash = ZapHash(salt, name);
        var cd = cdByHash.TryGetValue(hash, out var taken) ? taken + 1 : 0;
        cdByHash[hash] = cd;
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at), entries[i].Value);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 8), cd);
        name.CopyTo(block.AsSpan(at + 14));
      }
      return (size, [block]);
    }
    return (FatZapBlock, BuildFatZap(entries));
  }

  private static ulong NewSalt() => (BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) | 1) & 0x0000_0000_FFFF_FFFFUL;

  private static ulong ZapHash(ulong salt, ReadOnlySpan<byte> name) {
    var h = salt;
    foreach (var c in name)
      h = (h >> 8) ^ Crc64Table[(h ^ c) & 0xFF];
    return h & ~((1UL << (64 - ZapHashBits)) - 1);
  }

  /// <summary>
  /// A fat ZAP: header block 0 with the pointer table embedded in its second half, then
  /// 2^p leaves, leaf i holding the names whose hash starts with the p bits of i.
  /// </summary>
  private static List<byte[]?> BuildFatZap(IReadOnlyList<(string Name, ulong Value)> entries) {
    const int leafHashShift = 14 - 5;                                // 2^(bs-5) hash buckets
    const int chunkBytes = 24;
    const int leafHeader = 48;
    const int chunkArea = FatZapBlock - leafHeader - (2 << leafHashShift);
    const int chunksPerLeaf = chunkArea / chunkBytes;
    const int tableShift = 10;                                       // (16 KiB / 2) / 8 pointers
    var salt = NewSalt();

    var hashed = entries.Select(e => {
      var name = Encoding.UTF8.GetBytes(e.Name);
      return (Name: name, e.Value, Hash: ZapHash(salt, name));
    }).OrderBy(e => e.Hash).ToList();
    int ChunksFor(byte[] name) => 1 + (name.Length + 1 + 20) / 21 + 1;

    // Smallest prefix length whose leaves all fit.
    var prefix = 0;
    for (;; ++prefix) {
      if (prefix > tableShift)
        throw new NotSupportedException("ZFS: a directory too large for one pointer-table block.");
      var fits = hashed.GroupBy(e => prefix == 0 ? 0UL : e.Hash >> (64 - prefix)).All(g => g.Sum(e => ChunksFor(e.Name)) <= chunksPerLeaf);
      if (fits) break;
    }

    var leaves = 1 << prefix;
    var blocks = new List<byte[]?>();
    var header = new byte[FatZapBlock];
    BinaryPrimitives.WriteUInt64LittleEndian(header, ZbtHeader);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ZapMagic);
    // zap_ptrtbl: blk 0, numblks 0 (embedded), shift, nextblk, blks_copied
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), tableShift);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), (ulong)(leaves + 1));      // zap_freeblk
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(64), (ulong)leaves);            // zap_num_leafs
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), (ulong)entries.Count);     // zap_num_entries
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(80), salt);                     // zap_salt
    var table = header.AsSpan(FatZapBlock / 2);
    for (var i = 0; i < 1 << tableShift; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(table[(i * 8)..], (ulong)((i >> (tableShift - prefix)) + 1));
    blocks.Add(header);

    for (var leafIndex = 0; leafIndex < leaves; ++leafIndex) {
      var leaf = new byte[FatZapBlock];
      var members = hashed.Where(e => (prefix == 0 ? 0 : (int)(e.Hash >> (64 - prefix))) == leafIndex).ToList();
      BinaryPrimitives.WriteUInt64LittleEndian(leaf, ZbtLeaf);
      BinaryPrimitives.WriteUInt64LittleEndian(leaf.AsSpan(16), (ulong)leafIndex);           // lh_prefix
      BinaryPrimitives.WriteUInt32LittleEndian(leaf.AsSpan(24), ZapLeafMagic);
      var buckets = leaf.AsSpan(leafHeader, 2 << leafHashShift);
      for (var i = 0; i < 1 << leafHashShift; ++i)
        BinaryPrimitives.WriteUInt16LittleEndian(buckets[(i * 2)..], 0xFFFF);
      var chunks = leaf.AsSpan(leafHeader + (2 << leafHashShift));
      var nextChunk = 0;
      var cdByHash = new Dictionary<ulong, uint>();
      foreach (var (name, value, hash) in members) {
        var cd = cdByHash.TryGetValue(hash, out var used) ? used + 1 : 0;
        cdByHash[hash] = cd;
        var entry = nextChunk++;
        // the name, NUL included, as 1-byte integers in array chunks
        var nameChunk = nextChunk;
        var nameBytes = new byte[name.Length + 1];
        name.CopyTo(nameBytes, 0);
        nextChunk = WriteArray(chunks, nextChunk, nameBytes, chunkBytes);
        var valueChunk = nextChunk;
        var valueBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(valueBytes, value);
        nextChunk = WriteArray(chunks, nextChunk, valueBytes, chunkBytes);
        var e = chunks.Slice(entry * chunkBytes, chunkBytes);
        e[0] = 252;                                                   // ZAP_CHUNK_ENTRY
        e[1] = 8;                                                     // le_value_intlen
        var bucket = (int)((hash >> (64 - leafHashShift - prefix)) & ((1UL << leafHashShift) - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(e[2..], BinaryPrimitives.ReadUInt16LittleEndian(buckets[(bucket * 2)..]));   // le_next
        BinaryPrimitives.WriteUInt16LittleEndian(buckets[(bucket * 2)..], (ushort)entry);
        BinaryPrimitives.WriteUInt16LittleEndian(e[4..], (ushort)nameChunk);
        BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)valueChunk);
        BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], cd);
        BinaryPrimitives.WriteUInt64LittleEndian(e[16..], hash);
      }
      // free chunks form the free list
      for (var c = nextChunk; c < chunksPerLeaf; ++c) {
        var f = chunks.Slice(c * chunkBytes, chunkBytes);
        f[0] = 253;                                                   // ZAP_CHUNK_FREE
        BinaryPrimitives.WriteUInt16LittleEndian(f[22..], (ushort)(c + 1 < chunksPerLeaf ? c + 1 : 0xFFFF));
      }
      BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(28), (ushort)(chunksPerLeaf - nextChunk));  // lh_nfree
      BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(30), (ushort)members.Count);                 // lh_nentries
      BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(32), (ushort)prefix);                        // lh_prefix_len
      BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(34), (ushort)(nextChunk < chunksPerLeaf ? nextChunk : 0xFFFF)); // lh_freelist
      blocks.Add(leaf);
    }
    return blocks;
  }

  /// <summary>Writes <paramref name="data"/> into array chunks (21 bytes each, chained) from <paramref name="first"/>; returns the next free chunk.</summary>
  private static int WriteArray(Span<byte> chunks, int first, ReadOnlySpan<byte> data, int chunkBytes) {
    var count = (data.Length + 20) / 21;
    for (var i = 0; i < count; ++i) {
      var c = chunks.Slice((first + i) * chunkBytes, chunkBytes);
      c[0] = 251;                                                     // ZAP_CHUNK_ARRAY
      var part = data.Slice(i * 21, Math.Min(21, data.Length - i * 21));
      part.CopyTo(c[1..]);
      BinaryPrimitives.WriteUInt16LittleEndian(c[22..], (ushort)(i + 1 < count ? first + i + 1 : 0xFFFF));
    }
    return first + count;
  }

  private DmuObject ZapObject(byte type, IReadOnlyList<(string Name, ulong Value)> entries, byte bonusType = 0, byte[]? bonus = null) {
    var (size, blocks) = BuildZap(entries);
    return this.NewObject(type, bonusType, bonus ?? [], size, blocks);
  }

  // ── ZPL ────────────────────────────────────────────────────────────────────────────────

  private static byte[] Znode(Node node, ulong parent, ulong size, ulong links) =>
    ZnodeBonus(node.IsDirectory, node.Modified, parent, size, links);

  /// <summary>A version ≤ 4 <c>znode_phys_t</c> (264 bytes) with a trivial ACL matching mode 0644 / 0755.</summary>
  internal static byte[] ZnodeBonus(bool isDirectory, DateTime modified, ulong parent, ulong size, ulong links, ulong generation = Txg) {
    var z = new byte[264];
    var t = modified;
    var seconds = (ulong)Math.Max(0, (long)(t - DateTime.UnixEpoch).TotalSeconds);
    var nanos = (ulong)((t - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerSecond * 100);
    for (var i = 0; i < 4; ++i) {                                    // atime, mtime, ctime, crtime
      BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(i * 16), seconds);
      BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(i * 16 + 8), nanos);
    }
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(64), generation);           // zp_gen
    var mode = isDirectory ? 0x41EDUL : 0x81A4UL;                            // 0755 dir / 0644 file
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(72), mode);
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(80), size);
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(88), parent);
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(96), links);
    BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(120), ZnodeFlags(isDirectory));            // zp_flags
    // zfs_acl_phys_t at 176: a trivial three-entry ACL (owner@, group@, everyone@) matching the mode
    var acl = z.AsSpan(176);
    BinaryPrimitives.WriteUInt32LittleEndian(acl[8..], 24);                       // z_acl_size
    BinaryPrimitives.WriteUInt16LittleEndian(acl[12..], 1);                       // ZFS_ACL_VERSION_FUID
    BinaryPrimitives.WriteUInt16LittleEndian(acl[14..], 3);                       // z_acl_count
    WriteTrivialAces(acl[16..], isDirectory);
    return z;
  }

  /// <summary>Flags of a new znode: a trivial ACL, archive and anti-virus-modified bits, and for directories inheritance.</summary>
  internal static ulong ZnodeFlags(bool isDirectory) => isDirectory ? 0x40800000144UL : 0x40800000004UL;

  /// <summary>The three 8-byte FUID-style ACEs (owner@, group@, everyone@) matching mode 0644 / 0755: 24 bytes.</summary>
  internal static void WriteTrivialAces(Span<byte> at, bool isDirectory) {
    var exec = isDirectory ? 0x20u : 0u;
    WriteAce(at, 0x1000, 0x1E0190 | 0x09 | 0x46 | exec);                         // owner@: rw(x)
    WriteAce(at[8..], 0x4040, 0x120080 | 0x09 | exec);                           // group@: r(x)
    WriteAce(at[16..], 0x4000, 0x120080 | 0x09 | exec);                          // everyone@: r(x)
  }

  private static void WriteAce(Span<byte> at, ushort flags, uint mask) {
    BinaryPrimitives.WriteUInt16LittleEndian(at, 0);                              // ALLOW
    BinaryPrimitives.WriteUInt16LittleEndian(at[2..], flags);
    BinaryPrimitives.WriteUInt32LittleEndian(at[4..], mask);
  }

  private ObjectSet BuildFilesystem() {
    var os = new ObjectSet { Type = ZfsConstants.OstZfs };
    // object numbers: 1 master node, 2 unlinked set, 3 root, then the tree in order
    ulong next = 3;
    void Number(Node n) {
      n.Object = next++;
      foreach (var c in n.Children.Values)
        Number(c);
    }
    Number(this._root);

    void Emit(Node n, ulong parent) {
      if (n.IsDirectory) {
        var entries = n.Children.Values.Select(c => (c.Name, c.Object | (c.IsDirectory ? 4UL : 8UL) << 60)).ToList();
        var subdirs = (ulong)n.Children.Values.Count(c => c.IsDirectory);
        var bonus = Znode(n, parent, (ulong)entries.Count + 2, 2 + subdirs);
        os.Objects[n.Object] = this.ZapObject(OtDirectory, entries, OtZnode, bonus);
        foreach (var c in n.Children.Values)
          Emit(c, n.Object);
        return;
      }
      var data = n.Data;
      var blockSize = data.Length > MaxDataBlock ? MaxDataBlock : Math.Max(Sector, (data.Length + Sector - 1) & ~(Sector - 1));
      var blocks = new List<byte[]?>();
      for (var at = 0; at < data.Length; at += blockSize)
        blocks.Add(data.AsSpan(at, Math.Min(blockSize, data.Length - at)).ToArray());
      var fileBonus = Znode(n, parent, (ulong)data.LongLength, 1);
      os.Objects[n.Object] = this.NewObject(OtPlainFile, OtZnode, fileBonus, blockSize, blocks);
    }
    Emit(this._root, this._root.Object);

    os.Objects[1] = this.ZapObject(OtMasterNode, [
      ("DELETE_QUEUE", 2), ("ROOT", this._root.Object), ("VERSION", ZplVersion),
      ("casesensitivity", 0), ("normalization", 0), ("utf8only", 0),
    ]);
    os.Objects[2] = this.ZapObject(OtUnlinkedSet, []);
    this.AllocateObjectSet(os, next - 1);
    return os;
  }

  // ── build ──────────────────────────────────────────────────────────────────────────────

  /// <summary>Writes the pool image to <paramref name="output"/>.</summary>
  public void Build(Stream output, string poolName = "cwb") {
    ArgumentNullException.ThrowIfNull(output);
    if (string.IsNullOrEmpty(poolName) || !char.IsAsciiLetter(poolName[0]) || poolName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')))
      throw new ArgumentException("ZFS: pool names start with a letter and use letters, digits, '_', '-', '.', ':'.", nameof(poolName));

    var poolGuid = Guid64();
    var vdevGuid = Guid64();
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // 1. the filesystem's objects and its object set
    var fs = this.BuildFilesystem();

    // 2. the MOS objects whose blocks have a known size
    var mos = new ObjectSet { Type = ZfsConstants.OstMeta };
    ulong mosNext = 32;
    ulong Add(DmuObject o) {
      var n = mosNext++;
      mos.Objects[n] = o;
      return n;
    }

    // DSL objects first, with placeholders for the bonuses that need sizes or later numbers.
    var rootDir = Add(this.NewObject(OtDslDir, OtDslDir, new byte[256], Sector, []));
    var rootProps = Add(this.ZapObject(OtDslProps, []));
    var mosDir = Add(this.NewObject(OtDslDir, OtDslDir, new byte[256], Sector, []));
    var mosProps = Add(this.ZapObject(OtDslProps, []));
    var mosChildren = Add(this.ZapObject(OtDslDirChildMap, []));
    var freeDir = Add(this.NewObject(OtDslDir, OtDslDir, new byte[256], Sector, []));
    var freeProps = Add(this.ZapObject(OtDslProps, []));
    var freeChildren = Add(this.ZapObject(OtDslDirChildMap, []));
    var freeBpobj = Add(this.NewObject(OtBpobj, OtBpobjHeader, new byte[48], MaxDataBlock, []));
    var originDir = Add(this.NewObject(OtDslDir, OtDslDir, new byte[256], Sector, []));
    var originProps = Add(this.ZapObject(OtDslProps, []));
    var originChildren = Add(this.ZapObject(OtDslDirChildMap, []));
    var originHead = mosNext++;          // dataset objects created below, numbers reserved now
    var originSnapNames = mosNext++;
    var originSnapDeadlist = mosNext++;
    var originSnap = mosNext++;
    var originHeadDeadlist = mosNext++;
    var originHeadDeadBpobj = mosNext++;
    var headDataset = mosNext++;
    var headSnapNames = mosNext++;
    var headDeadlist = mosNext++;
    var headDeadBpobj = mosNext++;
    var nextClones = mosNext++;
    var dirClones = mosNext++;
    var rootChildren = Add(this.ZapObject(OtDslDirChildMap, [("$FREE", freeDir), ("$MOS", mosDir), ("$ORIGIN", originDir)]));

    byte[] Hex(ulong n) => Encoding.ASCII.GetBytes(n.ToString("x"));
    mos.Objects[originHead] = this.NewObject(OtDslDataset, OtDslDataset, new byte[320], Sector, []);
    mos.Objects[originSnapNames] = this.ZapObject(OtDslSnapMap, [("$ORIGIN", originSnap)]);
    mos.Objects[originSnapDeadlist] = this.ZapObject(OtDeadlist, [], OtDeadlistHeader, new byte[320]);
    mos.Objects[originSnap] = this.NewObject(OtDslDataset, OtDslDataset, new byte[320], Sector, []);
    mos.Objects[originHeadDeadlist] = this.ZapObject(OtDeadlist, [("0", originHeadDeadBpobj)], OtDeadlistHeader, new byte[320]);
    mos.Objects[originHeadDeadBpobj] = this.NewObject(OtBpobj, OtBpobjHeader, new byte[48], MaxDataBlock, []);
    mos.Objects[headDataset] = this.NewObject(OtDslDataset, OtDslDataset, new byte[320], Sector, []);
    mos.Objects[headSnapNames] = this.ZapObject(OtDslSnapMap, []);
    mos.Objects[headDeadlist] = this.ZapObject(OtDeadlist, [("0", headDeadBpobj)], OtDeadlistHeader, new byte[320]);
    mos.Objects[headDeadBpobj] = this.NewObject(OtBpobj, OtBpobjHeader, new byte[48], MaxDataBlock, []);
    mos.Objects[nextClones] = this.ZapObject(OtNextClones, [(Encoding.ASCII.GetString(Hex(headDataset)), headDataset)]);
    mos.Objects[dirClones] = this.ZapObject(OtDslClones, [(Encoding.ASCII.GetString(Hex(headDataset)), headDataset)]);

    var syncBplist = Add(this.NewObject(OtBpobj, OtBpobjHeader, new byte[48], 16 * 1024, []));

    // device geometry: data, then MOS, then a margin; at least 64 MiB.
    var estimate = this._next + 4L * 1024 * 1024;
    var device = Math.Max(MinimumDevice, (estimate * 5 / 4 + 32L * 1024 * 1024 + 4L * 1024 * 1024 + 512 * 1024 + 0x3FFFF) & ~0x3FFFFL);
    var asize = device - 4L * 1024 * 1024 - 512 * 1024;
    var metaslabShift = MetaslabShift;
    while ((asize >> metaslabShift) > 200)
      ++metaslabShift;
    var metaslabCount = (int)(asize >> metaslabShift);
    var metaslabSize = 1L << metaslabShift;

    // config and metaslab array objects: sizes fixed now, contents filled once numbers are known.
    var metaslabArray = Add(this.NewObject(OtObjectArray, 0, [], (metaslabCount * 8 + Sector - 1) & ~(Sector - 1), [new byte[metaslabCount * 8]]));
    NvList LeafVdev(ulong arrayObject) => new NvList()
      .Add("type", "file").Add("id", 0UL).Add("guid", vdevGuid).Add("path", $"/dev/{poolName}")
      .Add("metaslab_array", arrayObject).Add("metaslab_shift", (ulong)metaslabShift).Add("ashift", (ulong)Ashift)
      .Add("asize", (ulong)asize).Add("is_log", 0UL).Add("create_txg", Txg);
    NvList Config(bool label) {
      var l = new NvList().Add("version", PoolVersion).Add("name", poolName).Add("state", 1UL).Add("txg", Txg)
        .Add("pool_guid", poolGuid).Add("errata", 0UL).Add("hostname", "cwb");
      if (label)
        l.Add("top_guid", vdevGuid).Add("guid", vdevGuid).Add("vdev_children", 1UL).Add("vdev_tree", LeafVdev(metaslabArray));
      else
        l.Add("vdev_children", 1UL).Add("vdev_tree", new NvList().Add("type", "root").Add("id", 0UL).Add("guid", poolGuid)
          .Add("create_txg", Txg).Add("children", new[] { LeafVdev(metaslabArray) }));
      l.Add("features_for_read", new NvList());
      return l;
    }
    var configBytes = XdrNvListWriter.Pack(Config(label: false));
    var configBlock = (configBytes.Length + Sector - 1) & ~(Sector - 1);
    var configBonus = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(configBonus, (ulong)configBytes.Length);
    var configObj = Add(this.NewObject(OtPackedNvList, OtPackedNvListSize, configBonus, configBlock, [configBytes]));

    mos.Objects[1] = this.ZapObject(OtObjectDirectory, [
      ("root_dataset", rootDir), ("config", configObj), ("sync_bplist", syncBplist), ("free_bpobj", freeBpobj),
      ("deflate", 1), ("creation_version", PoolVersion),
    ]);

    // space maps: one per metaslab the pool's blocks reach, sized for a debug word and runs.
    // Everything still to come (space map blocks, MOS dnode blocks, MOS objset) is bounded.
    var reserve = metaslabCount * (long)Sector + ((long)mosNext + 64 + metaslabCount) * DnodeSize + 4L * IndirectSize + 4096
      + (long)metaslabCount * MaxDataBlock;                         // a skip to the next metaslab per boundary
    var reach = this._next + reserve;
    var mapsNeeded = (int)Math.Min(metaslabCount, (reach + metaslabSize - 1) / metaslabSize);
    if (reach > (long)metaslabCount * metaslabSize)
      throw new InvalidOperationException("ZFS: device geometry too small for the content.");
    var spaceMaps = new ulong[mapsNeeded];
    for (var m = 0; m < mapsNeeded; ++m)
    {
      // three block pointers leave a 64-byte bonus: a pool-version-28 space map header, no histogram
      var map = this.NewObject(OtSpaceMap, OtSpaceMapHeader, new byte[24], Sector, [new byte[Sector]]);
      map.BlockPointers = 3;
      spaceMaps[m] = Add(map);
    }

    this.AllocateObjectSet(mos, mosNext - 1);
    if (this._next > reach)
      throw new InvalidOperationException("ZFS: allocation outgrew its reserve.");

    // 3. contents that depend on numbers and sizes
    // space maps: allocated space per metaslab (front to back, so one run per 32768 sectors)
    // the allocated ranges: blocks are contiguous except where one skipped to the next metaslab
    var ranges = new List<(long Start, long End)>();
    foreach (var b in this._blocks.OrderBy(b => b.Offset))
      if (ranges.Count > 0 && ranges[^1].End == b.Offset)
        ranges[^1] = (ranges[^1].Start, b.Offset + b.Size);
      else
        ranges.Add((b.Offset, b.Offset + b.Size));
    var metaslabArrayData = new byte[metaslabCount * 8];
    for (var m = 0; m < mapsNeeded; ++m) {
      var start = m * metaslabSize;
      var limit = start + metaslabSize;
      var words = new List<ulong> { 2UL << 62 | 1UL << 50 | Txg };     // debug entry: ALLOC, sync pass 1, txg
      long allocatedHere = 0;
      foreach (var (from, to) in ranges) {
        var lo = Math.Max(from, start);
        var hi = Math.Min(to, limit);
        for (var at = lo; at < hi;) {
          var run = Math.Min(hi - at, 32768L * Sector);
          words.Add((ulong)((at - start) >> Ashift) << 16 | (ulong)((run >> Ashift) - 1));
          allocatedHere += run;
          at += run;
        }
      }
      if (words.Count * 8 > Sector)
        throw new InvalidOperationException("ZFS: a space map outgrew its block.");
      var end = start + allocatedHere;
      var map = mos.Objects[spaceMaps[m]];
      var data = map.Level0[0]!.Data;
      for (var i = 0; i < words.Count; ++i)
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(i * 8), words[i]);
      BinaryPrimitives.WriteUInt64LittleEndian(map.Bonus, spaceMaps[m]);                       // smp_object
      BinaryPrimitives.WriteUInt64LittleEndian(map.Bonus.AsSpan(8), (ulong)(words.Count * 8)); // smp_length
      BinaryPrimitives.WriteInt64LittleEndian(map.Bonus.AsSpan(16), end - start);             // smp_alloc
      BinaryPrimitives.WriteUInt64LittleEndian(metaslabArrayData.AsSpan(m * 8), spaceMaps[m]);
    }
    metaslabArrayData.CopyTo(mos.Objects[metaslabArray].Level0[0]!.Data, 0);

    // seal the filesystem first: the head dataset points at its objset block.
    SealObjectSet(fs);
    var fsBytes = (ulong)ObjectSetBytes(fs);

    // MOS objects sealed for their own sizes (the dnodes are written by SealObjectSet).
    foreach (var o in mos.Objects.Values)
      SealObject(o);
    SealObject(mos.Meta);
    var mosBytes = (ulong)(mos.Objects.Values.Sum(o => o.UsedBytes) + mos.Meta.UsedBytes + mos.Root.Size);

    void DslDir(ulong obj, ulong head, ulong parent, ulong origin, ulong children, ulong props, ulong used, ulong headUsed, ulong childUsed, ulong clones = 0) {
      var b = mos.Objects[obj].Bonus;
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0), now);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), head);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parent);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), origin);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), children);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), used);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), used);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), used);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(80), props);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(96), 1);              // DD_FLAG_USED_BREAKDOWN
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(104), headUsed);      // used_breakdown[HEAD]
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(120), childUsed);     // used_breakdown[CHILD]
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(144), clones);        // dd_clones
    }
    DslDir(rootDir, headDataset, 0, originSnap, rootChildren, rootProps, fsBytes + mosBytes, fsBytes, mosBytes);
    DslDir(mosDir, 0, rootDir, 0, mosChildren, mosProps, mosBytes, mosBytes, 0);
    DslDir(freeDir, 0, rootDir, 0, freeChildren, freeProps, 0, 0, 0);
    DslDir(originDir, originHead, rootDir, 0, originChildren, originProps, 0, 0, 0, dirClones);

    void Dataset(ulong obj, ulong dir, ulong prevSnap, ulong prevSnapTxg, ulong nextSnap, ulong snapNames, ulong children,
      ulong deadlist, ulong bytes, ulong nextClonesObj, Block? root) {
      var b = mos.Objects[obj].Bonus;
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0), dir);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), prevSnap);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), prevSnapTxg);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), nextSnap);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), snapNames);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), children);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), now);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), 1);             // creation txg
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(64), deadlist);
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(72), bytes);         // referenced
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(80), bytes);         // compressed
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(88), bytes);         // uncompressed
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(96), bytes);         // unique
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(104), Guid64() & 0x00FFFFFFFFFFFFFFUL);  // fsid guid
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(112), Guid64());
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(120), 4);            // DS_FLAG_UNIQUE_ACCURATE
      if (root != null) {
        root.Type = OtObjset;
        WriteBlockPointer(b.AsSpan(128), root);
      }
      BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(256), nextClonesObj);
    }
    Dataset(originHead, originDir, originSnap, 1, 0, originSnapNames, 0, originHeadDeadlist, 0, 0, null);
    Dataset(originSnap, originDir, 0, 0, originHead, 0, 2, originSnapDeadlist, 0, nextClones, null);
    Dataset(headDataset, rootDir, originSnap, 1, 0, headSnapNames, 0, headDeadlist, fsBytes, 0, fs.Root);

    // 4. the MOS object set, then the labels and the uberblock
    SealObjectSet(mos);

    var image = output;
    image.SetLength(0);
    image.SetLength(device);
    const long dataStart = 4L * 1024 * 1024;
    foreach (var b in this._blocks) {
      image.Position = dataStart + b.Offset;
      image.Write(b.Data);
    }

    var labelList = XdrNvListWriter.Pack(Config(label: true));
    if (labelList.Length > ZfsConstants.NvListSize - 40)
      throw new InvalidOperationException("ZFS: label nvlist too large.");
    var guidSum = unchecked(poolGuid + vdevGuid);
    var labelStarts = new[] { 0L, ZfsConstants.LabelSize, (device & ~(ZfsConstants.LabelSize - 1L)) - 2 * ZfsConstants.LabelSize,
      (device & ~(ZfsConstants.LabelSize - 1L)) - ZfsConstants.LabelSize };
    foreach (var label in labelStarts) {
      var phys = new byte[ZfsConstants.NvListSize];
      labelList.CopyTo(phys, 0);
      Seal(phys, label + ZfsConstants.NvListOffset);
      image.Position = label + ZfsConstants.NvListOffset;
      image.Write(phys);

      var slot = 1 << Math.Clamp(Ashift, ZfsConstants.MinUberblockShift, ZfsConstants.MaxUberblockShift);
      var ub = new byte[slot];
      BinaryPrimitives.WriteUInt64LittleEndian(ub, ZfsConstants.UberblockMagic);
      BinaryPrimitives.WriteUInt64LittleEndian(ub.AsSpan(8), PoolVersion);
      BinaryPrimitives.WriteUInt64LittleEndian(ub.AsSpan(16), Txg);
      BinaryPrimitives.WriteUInt64LittleEndian(ub.AsSpan(24), guidSum);
      BinaryPrimitives.WriteUInt64LittleEndian(ub.AsSpan(32), now);
      mos.Root.Type = OtObjset;
      WriteBlockPointer(ub.AsSpan(40), mos.Root);
      var slotIndex = (int)(Txg % (ulong)(ZfsConstants.UberblockRingSize / slot));
      var at = label + ZfsConstants.UberblockRingOffset + (long)slotIndex * slot;
      Seal(ub, at);
      image.Position = at;
      image.Write(ub);
    }
    image.Flush();
  }

  /// <summary>Seals a label-area block with its trailing <c>zio_eck_t</c>: SHA-256 with the device offset as verifier.</summary>
  internal static void Seal(byte[] block, long deviceOffset) {
    var tail = block.Length - 40;
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(tail), ZfsConstants.EmbeddedChecksumMagic);
    var checksum = block.AsSpan(tail + 8, 32);
    checksum.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(checksum, deviceOffset);
    Span<byte> digest = stackalloc byte[32];
    ZfsChecksum.Sha256Words(block, digest);
    digest.CopyTo(checksum);
  }

  private static ulong Guid64() {
    ulong g;
    do g = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    while (g == 0);
    return g;
  }
}
