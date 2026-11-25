using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Tux3;

/// <summary>One file, directory or symlink of a Tux3 volume (revision 2015-06-01).</summary>
public sealed record Tux3Node(string Path, ulong Inum, int Mode, uint Uid, uint Gid, long Size, uint Links, long ModifiedNanoseconds, string? LinkTarget) {
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
  public bool IsFile => (this.Mode & 0xF000) == 0x8000;

  /// <summary>The device number of a character or block device, as <c>major &lt;&lt; 20 | minor</c>; 0 otherwise.</summary>
  public ulong Device { get; init; }
}

/// <summary>
/// Reads the file tree of a Tux3 volume of revision 2015-06-01: replays the log's index-node
/// records over the nodes it reads, walks the inode tree and the directories from the root, and
/// reads file data through each inode's data tree (<c>docs/TUX3-ON-DISK.md</c>).
/// </summary>
/// <remarks>Written from the on-disk description; nothing parses that it cannot prove, anything
/// unexpected throws <see cref="InvalidDataException"/>.</remarks>
internal sealed class Tux3FileTree {
  private const ushort MagicBnode = 0xB4DE, MagicIleaf = 0x90DE, MagicDleaf = 0xDA7A, MagicLog = 0x10AD;
  private const ulong RootDirInum = 63;
  private const int MaxDepth = 64;

  // Log record sizes by code (0x33 ... 0x46), code byte included
  private static readonly int[] LogSizes = [11, 11, 11, 11, 13, 7, 13, 26, 15, 19, 19, 13, 15, 19, 7, 9, 9, 7, 1, 1];
  private const byte LogFirst = 0x33, LogBnodeRedirect = 0x39, LogBnodeRoot = 0x3A, LogBnodeSplit = 0x3B, LogBnodeAdd = 0x3C,
    LogBnodeUpdate = 0x3D, LogBnodeMerge = 0x3E, LogBnodeDel = 0x3F, LogBnodeAdjust = 0x40, LogUnify = 0x45;

  private readonly ImageAccessor _image;
  private readonly int _blockSize;
  private readonly ulong _volBlocks;
  private readonly Dictionary<ulong, byte[]> _nodes = [];
  private readonly Dictionary<ulong, byte[]> _inodes = [];

  public List<Tux3Node> Nodes { get; } = [];
  public int ReplayedRecords { get; private set; }

  public Tux3FileTree(ImageAccessor image, int blockBits, ulong volBlocks, ulong iroot, ulong logChain, uint logCount) {
    if (blockBits is < 9 or > 16) throw new InvalidDataException($"Tux3: block size 2^{blockBits} is not supported.");
    this._image = image;
    this._blockSize = 1 << blockBits;
    this._volBlocks = volBlocks;
    this.Replay(logChain, logCount);
    foreach (var leaf in this.Leaves(iroot, MagicIleaf)) this.ReadIleaf(leaf);
    var root = this.Inode(RootDirInum, "the root directory");
    if ((root.Mode & 0xF000) != 0x4000) throw new InvalidDataException("Tux3: inode 63 is not a directory.");
    this.Walk(root, "", 0);
  }

  // ── blocks ───────────────────────────────────────────────────────────

  private byte[] Block(ulong block) {
    if (this._nodes.TryGetValue(block, out var node)) return node;
    if (block == 0 || block >= this._volBlocks) throw new InvalidDataException($"Tux3: block {block} lies outside the volume.");
    var offset = (long)block * this._blockSize;
    if (offset + this._blockSize > this._image.Length) throw new InvalidDataException($"Tux3: block {block} lies past the end of the image.");
    return this._image.Read(offset, this._blockSize);
  }

  private static ulong U48(ReadOnlySpan<byte> p) => (ulong)BinaryPrimitives.ReadUInt16BigEndian(p) << 32 | BinaryPrimitives.ReadUInt32BigEndian(p[2..]);

  // ── the log ──────────────────────────────────────────────────────────

  private void Replay(ulong logChain, uint logCount) {
    if (logCount == 0) return;
    if (logCount > 1 << 20) throw new InvalidDataException("Tux3: implausible log length.");
    var blocks = new byte[logCount][];
    var at = logChain;
    for (var i = (int)logCount - 1; i >= 0; --i) {
      var log = this.Block(at);
      if (BinaryPrimitives.ReadUInt16BigEndian(log) != MagicLog) throw new InvalidDataException($"Tux3: log block {at} has no log magic.");
      if (BinaryPrimitives.ReadUInt16BigEndian(log.AsSpan(2)) + 16 > this._blockSize) throw new InvalidDataException("Tux3: log block claims more bytes than it holds.");
      blocks[i] = log;
      at = BinaryPrimitives.ReadUInt64BigEndian(log.AsSpan(8));
    }

    // records before the newest unify mark are already in the trees
    int unifyBlock = -1, unifyAt = 16;
    for (var i = (int)logCount - 1; i >= 0 && unifyBlock < 0; --i)
      foreach (var (code, pos) in Records(blocks[i]))
        if (code == LogUnify) { unifyBlock = i; unifyAt = pos; break; }

    for (var i = Math.Max(unifyBlock, 0); i < logCount; ++i)
      foreach (var (code, pos) in Records(blocks[i])) {
        if (i == unifyBlock && pos < unifyAt) continue;
        this.Apply(code, blocks[i].AsSpan(pos + 1));
      }
  }

  private static IEnumerable<(byte Code, int Position)> Records(byte[] log) {
    var end = 16 + BinaryPrimitives.ReadUInt16BigEndian(log.AsSpan(2));
    var list = new List<(byte, int)>();
    for (var pos = 16; pos < end;) {
      var code = log[pos];
      if (code < LogFirst || code >= LogFirst + LogSizes.Length) throw new InvalidDataException($"Tux3: unknown log record 0x{code:X2}.");
      var size = LogSizes[code - LogFirst];
      if (pos + size > end) throw new InvalidDataException("Tux3: log record runs past its block.");
      list.Add((code, pos));
      pos += size;
    }
    return list;
  }

  private void Apply(byte code, ReadOnlySpan<byte> a) {
    switch (code) {
      case LogBnodeRedirect:
        this._nodes[U48(a[6..])] = (byte[])this.Bnode(U48(a)).Clone();
        break;
      case LogBnodeRoot: {
        var count = a[0];
        var node = this.NewBnode();
        SetCount(node, count);
        this.SetChild(node, 0, U48(a[7..]));
        SetKey(node, 1, U48(a[19..]));
        this.SetChild(node, 1, U48(a[13..]));
        this._nodes[U48(a[1..])] = node;
        break;
      }
      case LogBnodeSplit: {
        int pos = BinaryPrimitives.ReadUInt16BigEndian(a);
        var src = this.Bnode(U48(a[2..]));
        var dst = this.NewBnode();
        var count = Count(src);
        if (pos < count) {
          for (var i = pos; i < count; ++i) {
            SetKey(dst, i - pos, Key(src, i));
            this.SetChild(dst, i - pos, this.Child(src, i));
          }
          SetCount(dst, count - pos);
          for (var i = pos; i < count; ++i) { SetKey(src, i, 0); this.SetChild(src, i, 0); }
          SetCount(src, pos);
        }
        this._nodes[U48(a[8..])] = dst;
        break;
      }
      case LogBnodeAdd: {
        var parent = this.Bnode(U48(a));
        var key = U48(a[12..]);
        this.Insert(parent, Lookup(parent, key) + 1, key, U48(a[6..]));
        break;
      }
      case LogBnodeUpdate: {
        var parent = this.Bnode(U48(a));
        var key = U48(a[12..]);
        var idx = Lookup(parent, key);
        if (Key(parent, idx) != key && idx != 0) throw new InvalidDataException("Tux3: log updates a key the node does not hold.");
        this.SetChild(parent, idx, U48(a[6..]));
        break;
      }
      case LogBnodeMerge: {
        var src = this.Bnode(U48(a));
        var dst = this.Bnode(U48(a[6..]));
        int from = Count(src), into = Count(dst);
        if (from + into > this.MaxCount) throw new InvalidDataException("Tux3: log merges more entries than a node holds.");
        for (var i = 0; i < from; ++i) {
          SetKey(dst, into + i, Key(src, i));
          this.SetChild(dst, into + i, this.Child(src, i));
        }
        SetCount(dst, into + from);
        break;
      }
      case LogBnodeDel: {
        int count = BinaryPrimitives.ReadUInt16BigEndian(a);
        var node = this.Bnode(U48(a[2..]));
        var idx = Lookup(node, U48(a[8..]));
        var n = Count(node);
        if (idx >= n) break;
        count = Math.Min(count, n - idx);
        for (var i = idx; i + count < n; ++i) {
          SetKey(node, i, Key(node, i + count));
          this.SetChild(node, i, this.Child(node, i + count));
        }
        for (var i = n - count; i < n; ++i) { SetKey(node, i, 0); this.SetChild(node, i, 0); }
        SetCount(node, n - count);
        break;
      }
      case LogBnodeAdjust: {
        var node = this.Bnode(U48(a));
        var idx = Lookup(node, U48(a[6..]));
        SetKey(node, idx, U48(a[12..]));
        break;
      }
      default:
        return; // allocation and orphan records change no tree a reader walks
    }
    ++this.ReplayedRecords;
  }

  // ── index nodes ──────────────────────────────────────────────────────

  private int MaxCount => (this._blockSize - 6 - 2) / 16;

  private byte[] Bnode(ulong block) {
    var node = this.Block(block);
    if (BinaryPrimitives.ReadUInt16BigEndian(node) != MagicBnode) throw new InvalidDataException($"Tux3: block {block} is not an index node.");
    if (!this._nodes.ContainsKey(block)) this._nodes[block] = node; // the working copy the log edits
    return node;
  }

  private byte[] NewBnode() {
    var node = new byte[this._blockSize];
    BinaryPrimitives.WriteUInt16BigEndian(node, MagicBnode);
    return node;
  }

  private static int Count(byte[] node) => BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(6));
  private static void SetCount(byte[] node, int count) => BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(6), (ushort)count);
  private static ulong Key(byte[] node, int idx) => BinaryPrimitives.ReadUInt64BigEndian(node.AsSpan(8 + idx * 8));
  private static void SetKey(byte[] node, int idx, ulong key) => BinaryPrimitives.WriteUInt64BigEndian(node.AsSpan(8 + idx * 8), key);
  private ulong Child(byte[] node, int idx) => BinaryPrimitives.ReadUInt64BigEndian(node.AsSpan(this._blockSize - 8 * (idx + 1)));
  private void SetChild(byte[] node, int idx, ulong child) => BinaryPrimitives.WriteUInt64BigEndian(node.AsSpan(this._blockSize - 8 * (idx + 1)), child);

  /// <summary>The last entry from index 1 on whose key is not greater than <paramref name="key"/>; 0 when none is.</summary>
  private static int Lookup(byte[] node, ulong key) {
    var count = Count(node);
    var found = 0;
    for (var i = 1; i < count; ++i)
      if (Key(node, i) <= key) found = i;
      else break;
    return found;
  }

  private void Insert(byte[] node, int idx, ulong key, ulong child) {
    var count = Count(node);
    if (count >= this.MaxCount || idx > count) throw new InvalidDataException("Tux3: log inserts into a full or short node.");
    for (var i = count; i > idx; --i) {
      SetKey(node, i, Key(node, i - 1));
      this.SetChild(node, i, this.Child(node, i - 1));
    }
    SetKey(node, idx, key);
    this.SetChild(node, idx, child);
    SetCount(node, count + 1);
  }

  /// <summary>Every leaf of the tree a packed root names, left to right.</summary>
  private List<byte[]> Leaves(ulong packedRoot, ushort leafMagic) {
    var leaves = new List<byte[]>();
    if ((packedRoot >> 63) != 0) throw new InvalidDataException("Tux3: a direct extent where a tree was expected.");
    var depth = (int)((packedRoot >> 48) & 0x7FFF);
    var block = packedRoot & 0xFFFF_FFFF_FFFF;
    if (depth == 0) return leaves;
    if (depth > MaxDepth) throw new InvalidDataException("Tux3: tree deeper than any volume holds.");
    this.Descend(block, depth, leafMagic, leaves);
    return leaves;
  }

  private void Descend(ulong block, int levels, ushort leafMagic, List<byte[]> leaves) {
    if (levels == 1) {
      var leaf = this.Block(block);
      if (BinaryPrimitives.ReadUInt16BigEndian(leaf) != leafMagic) throw new InvalidDataException($"Tux3: block {block} is not the expected leaf.");
      leaves.Add(leaf);
      return;
    }
    var node = this.Bnode(block);
    var count = Count(node);
    if (count == 0 || count > this.MaxCount) throw new InvalidDataException($"Tux3: index node {block} holds {count} entries.");
    for (var i = 0; i < count; ++i) this.Descend(this.Child(node, i), levels - 1, leafMagic, leaves);
  }

  // ── inodes ───────────────────────────────────────────────────────────

  private void ReadIleaf(byte[] leaf) {
    int count = BinaryPrimitives.ReadUInt16BigEndian(leaf.AsSpan(2));
    var ibase = BinaryPrimitives.ReadUInt64BigEndian(leaf.AsSpan(8));
    if (16 + count * 2 > leaf.Length) throw new InvalidDataException("Tux3: inode leaf offsets run past the block.");
    var previous = 0;
    for (var i = 0; i < count; ++i) {
      int offset = BinaryPrimitives.ReadUInt16BigEndian(leaf.AsSpan(16 + i * 2));
      if (offset < previous || offset > leaf.Length - 16 - count * 2) throw new InvalidDataException("Tux3: inode leaf offsets are out of order.");
      if (offset > previous) this._inodes[ibase + (ulong)i] = leaf.AsSpan(leaf.Length - offset, offset - previous).ToArray();
      previous = offset;
    }
  }

  private sealed record Attrs(ulong Inum, int Mode, uint Uid, uint Gid, long Size, uint Links, long Mtime, ulong DataRoot, ulong Generic);

  private Attrs Inode(ulong inum, string what) {
    if (!this._inodes.TryGetValue(inum, out var raw)) throw new InvalidDataException($"Tux3: {what} (inode {inum}) has no attributes.");
    int mode = -1; uint uid = 0, gid = 0, links = 0; long size = 0, ctime = 0, mtime = long.MinValue; ulong root = 0, generic = 0;
    bool hasLinks = false, hasTimes = false;
    var p = raw.AsSpan();
    var at = 0;
    while (at + 2 < p.Length) {
      int head = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
      at += 2;
      var kind = head >> 12;
      var length = kind switch { 0 => 10, 1 => 16, 2 => 4, 3 => 8, 4 => 8, 9 => 8, 11 or 12 => -1, _ => throw new InvalidDataException($"Tux3: inode {inum} has attribute kind {kind}.") };
      if (length < 0) {
        if (at + 2 > p.Length) throw new InvalidDataException($"Tux3: inode {inum} attribute is truncated.");
        length = 2 + BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
      }
      if (at + length > p.Length) throw new InvalidDataException($"Tux3: inode {inum} attribute runs past its record.");
      var v = p.Slice(at, length);
      if ((head & 0xFFF) == 0)
        switch (kind) {
          case 0: mode = BinaryPrimitives.ReadUInt16BigEndian(v); uid = BinaryPrimitives.ReadUInt32BigEndian(v[2..]); gid = BinaryPrimitives.ReadUInt32BigEndian(v[6..]); break;
          case 1: ctime = BinaryPrimitives.ReadInt64BigEndian(v); size = BinaryPrimitives.ReadInt64BigEndian(v[8..]); hasTimes = true; break;
          case 2: links = BinaryPrimitives.ReadUInt32BigEndian(v); hasLinks = true; break;
          case 3: mtime = BinaryPrimitives.ReadInt64BigEndian(v); break;
          case 4: root = BinaryPrimitives.ReadUInt64BigEndian(v); break;
          case 9: generic = BinaryPrimitives.ReadUInt64BigEndian(v); break;
        }
      at += length;
    }
    if (at != p.Length) throw new InvalidDataException($"Tux3: inode {inum} attributes do not end where their record does.");
    if (mode < 0 || !hasTimes) throw new InvalidDataException($"Tux3: inode {inum} lacks its mode or size.");
    if (!hasLinks) links = (mode & 0xF000) == 0x4000 ? 2u : 1u;
    if (mtime == long.MinValue) mtime = ctime;
    if (size < 0) throw new InvalidDataException($"Tux3: inode {inum} has a negative size.");
    return new(inum, mode, uid, gid, size, links, mtime, root, generic);
  }

  // ── data ─────────────────────────────────────────────────────────────

  /// <summary>The extents of an inode's data tree: (logical, physical, blocks); physical 0 is a hole.</summary>
  private List<(ulong Logical, ulong Physical, ulong Count)> Extents(ulong packedRoot) {
    var list = new List<(ulong, ulong, ulong)>();
    if ((packedRoot >> 63) != 0) {
      list.Add((0, packedRoot & 0xFFFF_FFFF_FFFF, (packedRoot >> 48) & 0x7FFF));
      return list;
    }
    foreach (var leaf in this.Leaves(packedRoot, MagicDleaf)) {
      int count = BinaryPrimitives.ReadUInt16BigEndian(leaf.AsSpan(2));
      if (8 + count * 16 > leaf.Length) throw new InvalidDataException("Tux3: data leaf extents run past the block.");
      for (var i = 0; i + 1 < count; ++i) {
        var logical = BinaryPrimitives.ReadUInt64BigEndian(leaf.AsSpan(8 + i * 16)) & 0xFFFF_FFFF_FFFF;
        var physical = BinaryPrimitives.ReadUInt64BigEndian(leaf.AsSpan(16 + i * 16)) & 0xFFFF_FFFF_FFFF;
        var next = BinaryPrimitives.ReadUInt64BigEndian(leaf.AsSpan(8 + (i + 1) * 16)) & 0xFFFF_FFFF_FFFF;
        if (next < logical) throw new InvalidDataException("Tux3: data leaf extents are out of order.");
        if (next > logical) list.Add((logical, physical, next - logical));
      }
      if (count > 0 && (BinaryPrimitives.ReadUInt64BigEndian(leaf.AsSpan(16 + (count - 1) * 16)) & 0xFFFF_FFFF_FFFF) != 0)
        throw new InvalidDataException("Tux3: data leaf does not end in a sentinel extent.");
    }
    return list;
  }

  private byte[] Data(ulong packedRoot, long size) {
    if (size > Array.MaxLength) throw new InvalidDataException("Tux3: file is too large to buffer.");
    var data = new byte[size];
    if (size == 0) return data;
    var blocks = (ulong)((size + this._blockSize - 1) / this._blockSize);
    foreach (var (logical, physical, count) in this.Extents(packedRoot)) {
      if (physical == 0 || logical >= blocks) continue;
      for (ulong b = 0; b < count && logical + b < blocks; ++b) {
        var block = this.Block(physical + b);
        var at = (long)(logical + b) * this._blockSize;
        var n = (int)Math.Min(this._blockSize, size - at);
        block.AsSpan(0, n).CopyTo(data.AsSpan((int)at));
      }
    }
    return data;
  }

  /// <summary>The bytes of a file or symlink target.</summary>
  public byte[] Read(Tux3Node node) {
    var attrs = this.Inode(node.Inum, node.Path);
    return this.Data(attrs.DataRoot, attrs.Size);
  }

  // ── directories ──────────────────────────────────────────────────────

  private void Walk(Attrs dir, string prefix, int depth) {
    if (depth > MaxDepth) throw new InvalidDataException("Tux3: directories nest deeper than 64 levels (a loop?).");
    var data = this.Data(dir.DataRoot, dir.Size);
    for (var block = 0; block < data.Length; block += this._blockSize) {
      var end = Math.Min(block + this._blockSize, data.Length);
      for (var at = block; at < end;) {
        if (at + 12 > end) throw new InvalidDataException("Tux3: directory entry runs past its block.");
        var inum = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));
        int recLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 8));
        int nameLen = data[at + 10];
        if (recLen < 12 || at + recLen > end || 12 + nameLen > recLen) throw new InvalidDataException("Tux3: directory entry has an invalid length.");
        if (nameLen > 0) {
          var name = Encoding.UTF8.GetString(data, at + 12, nameLen);
          if (name is "." or ".." || name.Contains('/')) throw new InvalidDataException($"Tux3: directory holds an unusable name '{name}'.");
          var path = prefix.Length == 0 ? name : prefix + "/" + name;
          var a = this.Inode(inum, path);
          string? target = null;
          if ((a.Mode & 0xF000) == 0xA000) target = Encoding.UTF8.GetString(this.Data(a.DataRoot, a.Size));
          // A device number is kept as the kernel encodes it: major << 8 | minor, the minor's upper bits from bit 20.
          var device = (a.Mode & 0xF000) is 0x2000 or 0x6000 ? ((a.Generic >> 8) & 0xFFF) << 20 | (a.Generic & 0xFF) | ((a.Generic >> 12) & 0xFFF00) : 0;
          this.Nodes.Add(new Tux3Node(path, inum, a.Mode, a.Uid, a.Gid, (a.Mode & 0xF000) == 0x4000 ? 0 : a.Size, a.Links, a.Mtime, target) { Device = device });
          if ((a.Mode & 0xF000) == 0x4000) this.Walk(a, path, depth + 1);
        }
        at += recLen;
      }
    }
  }
}
