using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Tux3;

/// <summary>
/// Creates a Tux3 volume of revision 2015-06-01 laid out the way the linux-tux3 kernel lays out
/// what it writes (<c>docs/TUX3-ON-DISK.md</c>, "Writing"): every file in one contiguous run of
/// blocks named by a direct data-tree root, directories in ext2-style entry blocks, the inode
/// attributes in inode-tree leaves under one index node, and the block allocation recorded as the
/// log records the kernel replays at mount, as <c>tux3 mkfs</c> leaves it.
/// </summary>
/// <remarks>
/// Written from the on-disk description and checked against the linux-tux3 kernel, which mounts
/// every volume this writer makes and reads back each object; no Tux3 code was carried over.
/// </remarks>
public sealed class Tux3Writer {

  /// <summary>The only block size written: 4 KiB, as <c>tux3 mkfs</c> makes.</summary>
  public const int BlockSize = 4096;

  /// <summary>The smallest volume written, 16 MiB.</summary>
  public const ulong MinBlockCount = 4096;

  private const int BlockBits = 12;
  private const ulong RootInum = 63, AtomTableInum = 4, FirstInum = 64, FirstLeafBase = 4;
  private const ushort MagicBnode = 0xB4DE, MagicIleaf = 0x90DE, MagicDleaf = 0xDA7A, MagicLog = 0x10AD;
  private const byte LogBalloc = 0x33, LogDelta = 0x46;
  private const ulong DirectRootBit = 1UL << 63;

  /// <summary>Owner, permissions and modification time of a new object.</summary>
  /// <param name="Permissions">The low twelve mode bits; null for 0644 (files) or 0755 (directories).</param>
  /// <param name="ModifiedNanoseconds">Modification time in nanoseconds since 1970; null for the volume's time.</param>
  public sealed record Metadata(int? Permissions = null, uint Uid = 0, uint Gid = 0, long? ModifiedNanoseconds = null);

  private sealed class Node {
    public required string Name { get; init; }
    public required int Mode { get; set; }
    public ulong Inum { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public long? Mtime { get; set; }
    public byte[] Data { get; set; } = [];
    public ulong Device { get; set; }
    public Node? Parent { get; set; }
    public List<(string Name, Node Target)> Entries { get; } = [];
    public uint Links { get; set; }
    public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  }

  private readonly Node _root = new() { Name = "", Mode = 0x41ED };
  private readonly List<Node> _objects = [];

  /// <summary>The longest run a direct data-tree root names (its 15-bit count); longer files get a data leaf. Lowered only by tests.</summary>
  internal int MaxDirectBlocks { get; init; } = 0x7FFF;

  /// <summary>Total volume size in 4 KiB blocks; raised to what the content needs and to <see cref="MinBlockCount" />.</summary>
  public ulong BlockCount { get; set; } = MinBlockCount;

  /// <summary>The volume's creation time in nanoseconds since 1970 (ctime of every object); null for now.</summary>
  public long? TimeNanoseconds { get; set; }

  /// <summary>An empty volume: the root directory alone.</summary>
  public Tux3Writer() {
    this._root.Inum = RootInum;
    this._root.Parent = this._root;
  }

  /// <summary>Adds a directory (and any missing parents).</summary>
  public void AddDirectory(string path, Metadata? metadata = null) {
    var dir = this.Directory(Split(path), create: true)!;
    if (dir != this._root) Apply(dir, metadata, 0x4000);
  }

  /// <summary>Adds a regular file.</summary>
  public void AddFile(string path, byte[] data, Metadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(data);
    var node = this.Link(path, 0x8000 | 0x1A4);
    node.Data = data;
    Apply(node, metadata, 0x8000);
  }

  /// <summary>Adds a symbolic link whose target is <paramref name="target" />.</summary>
  public void AddSymlink(string path, string target, Metadata? metadata = null) {
    ArgumentException.ThrowIfNullOrEmpty(target);
    var node = this.Link(path, 0xA1FF);
    node.Data = Encoding.UTF8.GetBytes(target);
    if (node.Data.Length >= BlockSize) throw new NotSupportedException("Tux3: a symlink target must be shorter than a block.");
    node.Uid = metadata?.Uid ?? 0;
    node.Gid = metadata?.Gid ?? 0;
    node.Mtime = metadata?.ModifiedNanoseconds;
  }

  /// <summary>Adds a FIFO, socket or device node; <paramref name="mode" /> carries the file type bits.</summary>
  public void AddSpecialFile(string path, int mode, ulong device = 0, Metadata? metadata = null) {
    var type = mode & 0xF000;
    if (type is not (0x1000 or 0x2000 or 0x6000 or 0xC000)) throw new ArgumentException("Tux3: not a FIFO, socket or device mode.", nameof(mode));
    var node = this.Link(path, mode);
    node.Device = type is 0x2000 or 0x6000 ? device : 0;
    Apply(node, metadata, type);
    if (metadata?.Permissions is null) node.Mode = mode;
  }

  /// <summary>Adds <paramref name="path" /> as a further name of the non-directory at <paramref name="existing" />.</summary>
  public void AddHardLink(string path, string existing) {
    var parts = Split(existing);
    var parent = this.Directory(parts[..^1], create: false) ?? throw new FileNotFoundException("Tux3: the link target was not found.", existing);
    var target = parent.Entries.FirstOrDefault(e => e.Name == parts[^1]).Target
      ?? throw new FileNotFoundException("Tux3: the link target was not found.", existing);
    if (target.IsDirectory) throw new NotSupportedException("Tux3: directories cannot be hard-linked.");
    var (dir, name) = this.ParentOf(path);
    dir.Entries.Add((name, target));
  }

  private static void Apply(Node node, Metadata? metadata, int type) {
    if (metadata is null) return;
    if (metadata.Permissions is { } permissions) node.Mode = type | permissions & 0xFFF;
    node.Uid = metadata.Uid;
    node.Gid = metadata.Gid;
    node.Mtime = metadata.ModifiedNanoseconds;
  }

  private static string[] Split(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    foreach (var part in parts) {
      if (part is "." or "..") throw new NotSupportedException($"Tux3: '{path}' holds a '.' or '..' component.");
      if (Encoding.UTF8.GetByteCount(part) > 255) throw new NotSupportedException($"Tux3: '{part}' is longer than 255 bytes.");
    }
    return parts;
  }

  private Node? Directory(ReadOnlySpan<string> parts, bool create) {
    var current = this._root;
    foreach (var part in parts) {
      var next = current.Entries.FirstOrDefault(e => e.Name == part).Target;
      if (next is null) {
        if (!create) return null;
        next = new Node { Name = part, Mode = 0x41ED, Parent = current };
        current.Entries.Add((part, next));
        this._objects.Add(next);
      } else if (!next.IsDirectory) throw new InvalidDataException($"Tux3: '{part}' is both a file and a directory.");
      current = next;
    }
    return current;
  }

  private (Node Dir, string Name) ParentOf(string path) {
    var parts = Split(path);
    if (parts.Length == 0) throw new ArgumentException("Tux3: an empty path names the root.", nameof(path));
    var dir = this.Directory(parts[..^1], create: true)!;
    if (dir.Entries.Any(e => e.Name == parts[^1])) throw new InvalidDataException($"Tux3: '{path}' already exists.");
    return (dir, parts[^1]);
  }

  private Node Link(string path, int mode) {
    var (dir, name) = this.ParentOf(path);
    var node = new Node { Name = name, Mode = mode, Parent = dir };
    dir.Entries.Add((name, node));
    this._objects.Add(node);
    return node;
  }

  /// <summary>Builds the volume in memory.</summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    this.Write(ms);
    return ms.ToArray();
  }

  /// <summary>Writes the volume to <paramref name="output" />.</summary>
  public void Write(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var now = this.TimeNanoseconds ?? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000);

    // Inode numbers in creation order, link counts from the namespace.
    var inum = FirstInum;
    foreach (var obj in this._objects) obj.Inum = inum++;
    var all = new List<Node> { this._root };
    all.AddRange(this._objects);
    foreach (var obj in all) obj.Links = obj.IsDirectory ? 2u : 0u;
    foreach (var obj in all)
      foreach (var (_, target) in obj.Entries) {
        if (target.IsDirectory) ++obj.Links;
        else ++target.Links;
      }

    // Directory bodies, then every object's data in one contiguous run.
    foreach (var dir in all.Where(static o => o.IsDirectory)) dir.Data = DirectoryBlocks(dir.Entries);
    var blocks = new Dictionary<ulong, byte[]>();
    var next = 2UL;
    var roots = new Dictionary<Node, ulong>();
    foreach (var obj in all) {
      if (obj.Data.Length == 0) continue;
      var count = (ulong)((obj.Data.LongLength + BlockSize - 1) / BlockSize);
      var start = next;
      for (ulong b = 0; b < count; ++b) {
        var block = new byte[BlockSize];
        var at = (long)b * BlockSize;
        obj.Data.AsSpan((int)at, (int)Math.Min(BlockSize, obj.Data.LongLength - at)).CopyTo(block);
        blocks[start + b] = block;
      }
      next += count;
      if (count <= (ulong)this.MaxDirectBlocks) {
        roots[obj] = DirectRootBit | count << 48 | start;
        continue;
      }
      // One data leaf: the run, then the sentinel extent that ends the leaf's range.
      var leaf = new byte[BlockSize];
      BinaryPrimitives.WriteUInt16BigEndian(leaf, MagicDleaf);
      BinaryPrimitives.WriteUInt16BigEndian(leaf.AsSpan(2), 2);
      BinaryPrimitives.WriteUInt64BigEndian(leaf.AsSpan(8), 0);
      BinaryPrimitives.WriteUInt64BigEndian(leaf.AsSpan(16), start);
      BinaryPrimitives.WriteUInt64BigEndian(leaf.AsSpan(24), count);
      BinaryPrimitives.WriteUInt64BigEndian(leaf.AsSpan(32), 0);
      blocks[next] = leaf;
      roots[obj] = 1UL << 48 | next;
      ++next;
    }

    // Inode attributes, packed into inode-tree leaves.
    var attributes = new SortedDictionary<ulong, byte[]> { [AtomTableInum] = Attributes(0, 0, 0, now, 1L << 60, null, null, null, null) };
    foreach (var obj in all) {
      var mtime = obj.Mtime ?? now;
      var defaultLinks = obj.IsDirectory ? 2u : 1u;
      ulong? generic = obj.IsDirectory ? obj.Parent!.Inum : (obj.Mode & 0xF000) is 0x2000 or 0x6000 ? EncodeDevice(obj.Device) : null;
      attributes[obj.Inum] = Attributes(obj.Mode, obj.Uid, obj.Gid, now, obj.Data.LongLength,
        obj.Links == defaultLinks ? null : obj.Links, mtime == now ? null : mtime, roots.TryGetValue(obj, out var root) ? root : null, generic);
    }
    var leaves = PackInodeLeaves(attributes);
    var leafBlocks = new List<(ulong Base, ulong Block)>();
    foreach (var (leafBase, leaf) in leaves) {
      blocks[next] = leaf;
      leafBlocks.Add((leafBase, next++));
    }
    ulong iroot;
    if (leafBlocks.Count == 1) iroot = 1UL << 48 | leafBlocks[0].Block;
    else {
      if (leafBlocks.Count > (BlockSize - 8) / 16) throw new NotSupportedException("Tux3: more inodes than one index level holds.");
      var bnode = new byte[BlockSize];
      BinaryPrimitives.WriteUInt16BigEndian(bnode, MagicBnode);
      BinaryPrimitives.WriteUInt16BigEndian(bnode.AsSpan(6), (ushort)leafBlocks.Count);
      for (var i = 0; i < leafBlocks.Count; ++i) {
        BinaryPrimitives.WriteUInt64BigEndian(bnode.AsSpan(8 + 8 * i), i == 0 ? 0 : leafBlocks[i].Base);
        BinaryPrimitives.WriteUInt64BigEndian(bnode.AsSpan(BlockSize - 8 * (i + 1)), leafBlocks[i].Block);
      }
      blocks[next] = bnode;
      iroot = 2UL << 48 | next++;
    }

    // The allocation, as the log tux3 mkfs leaves: blocks 0 and 1 in the first delta, then
    // everything else; the log blocks themselves are allocated by the replay that reads them.
    var records = new List<byte[]> { Balloc(0, 2), new[] { LogDelta } };
    if (next > 2) records.Add(Balloc(2, next - 2));
    var logBlocks = new List<ulong>();
    var log = NewLog(0);
    var used = 0;
    foreach (var record in records) {
      if (16 + used + record.Length > BlockSize) {
        BinaryPrimitives.WriteUInt16BigEndian(log.AsSpan(2), (ushort)used);
        blocks[next] = log;
        logBlocks.Add(next++);
        log = NewLog(logBlocks[^1]);
        used = 0;
      }
      record.CopyTo(log.AsSpan(16 + used));
      used += record.Length;
    }
    BinaryPrimitives.WriteUInt16BigEndian(log.AsSpan(2), (ushort)used);
    blocks[next] = log;
    logBlocks.Add(next++);

    var volume = Math.Max(Math.Max(this.BlockCount, MinBlockCount), next + 1);
    var super = new byte[BlockSize];
    Tux3Reader.Magic2015.CopyTo(super, 0);
    BinaryPrimitives.WriteUInt16BigEndian(super.AsSpan(0x18), BlockBits);
    BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x20), volume);
    BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x28), iroot);
    BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x38), attributes.Keys.Max() + 1);
    BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x40), next);
    BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x54), 1);
    BinaryPrimitives.WriteUInt64BigEndian(super.AsSpan(0x58), logBlocks[^1]);
    BinaryPrimitives.WriteUInt32BigEndian(super.AsSpan(0x60), (uint)logBlocks.Count);
    blocks[1] = super;

    var basePosition = output.Position;
    output.SetLength(basePosition + checked((long)volume * BlockSize));
    foreach (var (block, bytes) in blocks.OrderBy(static p => p.Key)) {
      output.Position = basePosition + (long)block * BlockSize;
      output.Write(bytes);
    }
    output.Position = basePosition + (long)volume * BlockSize;
    output.Flush();
  }

  private static byte[] NewLog(ulong previous) {
    var log = new byte[BlockSize];
    BinaryPrimitives.WriteUInt16BigEndian(log, MagicLog);
    BinaryPrimitives.WriteUInt64BigEndian(log.AsSpan(8), previous);
    return log;
  }

  private static byte[] Balloc(ulong block, ulong count) {
    var record = new byte[11];
    record[0] = LogBalloc;
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(1), checked((uint)count));
    BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(5), (ushort)(block >> 32));
    BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(7), (uint)block);
    return record;
  }

  /// <summary>The kernel's device encoding: <c>major &lt;&lt; 8 | minor</c> while both fit, the wide form otherwise.</summary>
  private static ulong EncodeDevice(ulong device) {
    var major = device >> 20;
    var minor = device & 0xFFFFF;
    return major < 256 && minor < 256 ? major << 8 | minor : minor & 0xFF | major << 8 | (minor & ~0xFFUL) << 12;
  }

  private static byte[] Attributes(int mode, uint uid, uint gid, long ctime, long size, uint? links, long? mtime, ulong? root, ulong? generic) {
    var bytes = new List<byte>();
    void Head(int kind) { bytes.Add((byte)(kind << 4)); bytes.Add(0); }
    void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
    void U32(uint v) { for (var s = 24; s >= 0; s -= 8) bytes.Add((byte)(v >> s)); }
    void U64(ulong v) { for (var s = 56; s >= 0; s -= 8) bytes.Add((byte)(v >> s)); }
    Head(0); U16(mode); U32(uid); U32(gid);
    Head(1); U64((ulong)ctime); U64((ulong)size);
    if (links is { } l) { Head(2); U32(l); }
    if (mtime is { } m) { Head(3); U64((ulong)m); }
    if (root is { } r) { Head(4); U64(r); }
    if (generic is { } g) { Head(9); U64(g); }
    return [.. bytes];
  }

  /// <summary>Inode leaves, each covering a run of inode numbers whose attributes fit one block.</summary>
  private static List<(ulong Base, byte[] Leaf)> PackInodeLeaves(SortedDictionary<ulong, byte[]> attributes) {
    var result = new List<(ulong, byte[])>();
    var list = attributes.ToList();
    var first = 0;
    var leafBase = FirstLeafBase;
    while (first < list.Count) {
      var last = first;
      var bytes = 0;
      while (last < list.Count) {
        var count = (int)(list[last].Key - leafBase + 1);
        if (16 + 2 * count + bytes + list[last].Value.Length > BlockSize) break;
        bytes += list[last].Value.Length;
        ++last;
      }
      if (last == first) throw new InvalidDataException("Tux3: an inode's attributes do not fit a leaf.");
      var leaf = new byte[BlockSize];
      var span = (int)(list[last - 1].Key - leafBase + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf, MagicIleaf);
      BinaryPrimitives.WriteUInt16BigEndian(leaf.AsSpan(2), (ushort)span);
      BinaryPrimitives.WriteUInt64BigEndian(leaf.AsSpan(8), leafBase);
      var offset = 0;
      var k = first;
      for (var i = 0; i < span; ++i) {
        if (k < last && list[k].Key == leafBase + (ulong)i) {
          var value = list[k++].Value;
          offset += value.Length;
          value.CopyTo(leaf.AsSpan(BlockSize - offset));
        }
        BinaryPrimitives.WriteUInt16BigEndian(leaf.AsSpan(16 + 2 * i), (ushort)offset);
      }
      result.Add((leafBase, leaf));
      first = last;
      if (first < list.Count) leafBase = list[first].Key;
    }
    return result;
  }

  /// <summary>Entry blocks: inode, record length, name length, file type, name; records never cross a block, and the last of a block reaches its end.</summary>
  private static byte[] DirectoryBlocks(List<(string Name, Node Target)> entries) {
    if (entries.Count == 0) return [];
    var blocks = new List<byte[]>();
    var block = new byte[BlockSize];
    var at = 0;
    var lastAt = -1;
    foreach (var (name, target) in entries) {
      var bytes = Encoding.UTF8.GetBytes(name);
      var length = (12 + bytes.Length + 7) & ~7;
      if (at + length > BlockSize) {
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(lastAt + 8), (ushort)(BlockSize - lastAt));
        blocks.Add(block);
        block = new byte[BlockSize];
        at = 0;
      }
      BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(at), target.Inum);
      BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(at + 8), (ushort)length);
      block[at + 10] = (byte)bytes.Length;
      block[at + 11] = (target.Mode & 0xF000) switch {
        0x8000 => 1, 0x4000 => 2, 0x2000 => 3, 0x6000 => 4, 0x1000 => 5, 0xC000 => 6, 0xA000 => 7, _ => 0,
      };
      bytes.CopyTo(block, at + 12);
      lastAt = at;
      at += length;
    }
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(lastAt + 8), (ushort)(BlockSize - lastAt));
    blocks.Add(block);
    var data = new byte[blocks.Count * BlockSize];
    for (var i = 0; i < blocks.Count; ++i) blocks[i].CopyTo(data, i * BlockSize);
    return data;
  }
}
