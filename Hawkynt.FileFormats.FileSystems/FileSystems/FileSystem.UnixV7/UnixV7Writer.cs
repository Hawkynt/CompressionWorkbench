#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.UnixV7.UnixV7Layout;

namespace FileSystem.UnixV7;

/// <summary>
/// Lays out a fresh V7 volume the way V7 <c>mkfs</c> does: boot block, superblock,
/// i-list, then data. Inode 1 is the (empty) bad-block file, inode 2 the root.
/// Files and directories are placed contiguously from <c>s_isize</c>; every block
/// left over goes on the chained free list in descending order (each 50th freed
/// block carries the previous cache, <c>struct fblk</c>), so the lowest free blocks
/// are handed out first.
/// </summary>
public sealed class UnixV7Writer {
  private sealed class Node(string name, Node? parent, bool isDirectory) {
    public string Name { get; } = name;
    public Node? Parent { get; } = parent;
    public bool IsDirectory { get; } = isDirectory;
    public List<Node> Children { get; } = [];
    public byte[] Data { get; set; } = [];
    public DateTime? Modified { get; set; }
    public int Inode { get; set; }
  }

  private readonly Node _root = new("", null, true);

  /// <summary>Timestamp written where an input carries none.</summary>
  public DateTime Now { get; init; } = DateTime.UtcNow;

  /// <summary>Validates a path component: 1–14 bytes, no NUL or slash, Latin-1.</summary>
  public static string? Reject(string component) {
    if (component.Length == 0) return "empty path component";
    if (component is "." or "..") return "'.' and '..' are reserved";
    if (component.Length > MaxNameLength) return $"'{component}' is longer than {MaxNameLength} characters";
    foreach (var c in component)
      if (c == '\0' || c > 0xFF) return $"'{component}' holds a character V7 names cannot store";
    return null;
  }

  public void AddDirectory(string path, DateTime? modified = null) => Walk(path, createLeafAsDirectory: true).Modified ??= modified;

  public void AddFile(string path, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(data);
    var node = Walk(path, createLeafAsDirectory: false);
    node.Data = data;
    node.Modified = modified;
  }

  private Node Walk(string path, bool createLeafAsDirectory) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) {
      if (createLeafAsDirectory) return _root;
      throw new ArgumentException("UNIX V7: a file needs a name.", nameof(path));
    }
    var current = _root;
    for (var i = 0; i < parts.Length; ++i) {
      if (Reject(parts[i]) is { } why) throw new ArgumentException("UNIX V7: " + why, nameof(path));
      var leaf = i == parts.Length - 1;
      var isDir = !leaf || createLeafAsDirectory;
      var existing = current.Children.FirstOrDefault(c => c.Name == parts[i]);
      if (existing != null) {
        if (existing.IsDirectory != isDir)
          throw new ArgumentException($"UNIX V7: '{path}' is both a file and a directory.", nameof(path));
        if (leaf && !isDir) throw new ArgumentException($"UNIX V7: duplicate file '{path}'.", nameof(path));
        current = existing;
        continue;
      }
      var node = new Node(parts[i], current, isDir);
      current.Children.Add(node);
      current = node;
    }
    return current;
  }

  /// <summary>Blocks a file of <paramref name="bytes"/> needs, indirect blocks included.</summary>
  internal static long BlocksFor(long bytes) {
    const long n = AddressesPerBlock;
    var data = (bytes + BlockSize - 1) / BlockSize;
    long meta = 0;
    var rest = data - DirectAddresses;
    if (rest > 0) { meta += 1; rest -= n; }
    if (rest > 0) { meta += 1 + Math.Min((rest + n - 1) / n, n); rest -= n * n; }
    if (rest > 0) { meta += 1 + (rest + n * n - 1) / (n * n) + (rest + n - 1) / n; rest -= n * n * n; }
    if (rest > 0) throw new IOException("UNIX V7: file exceeds the triple-indirect limit.");
    return data + meta;
  }

  public byte[] Build() {
    // Number the inodes breadth-first: 1 = bad blocks, 2 = root.
    var order = new List<Node>();
    var queue = new Queue<Node>([_root]);
    while (queue.Count > 0) {
      var n = queue.Dequeue();
      order.Add(n);
      foreach (var c in n.Children) queue.Enqueue(c);
    }
    for (var i = 0; i < order.Count; ++i) order[i].Inode = RootInode + i;
    foreach (var d in order.Where(o => o.IsDirectory)) d.Data = DirectoryBytes(d);

    var used = order.Count + 1;
    var inodes = used + Math.Max(16, used / 4);
    inodes = (inodes + InodesPerBlock - 1) / InodesPerBlock * InodesPerBlock;
    if (inodes > ushort.MaxValue) throw new IOException("UNIX V7: more than 65535 inodes.");
    var isize = 2 + inodes / InodesPerBlock;

    var dataBlocks = order.Sum(o => BlocksFor(o.Data.Length));
    var fsize = isize + dataBlocks + Math.Max(64, dataBlocks / 4);
    if (fsize > MaxVolumeBlocks) throw new IOException("UNIX V7: volume exceeds 2^24 blocks.");
    if (fsize * BlockSize > Array.MaxLength) throw new IOException("UNIX V7: volume too large to build in memory.");

    var image = new byte[fsize * BlockSize];
    long next = isize;
    foreach (var node in order) next = Place(image, node, next);

    // Bad-block inode: an empty regular file, as mkfs leaves it.
    var bad = image.AsSpan((int)InodeOffset(1), InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(bad[DiMode..], IfReg);
    BinaryPrimitives.WriteInt16LittleEndian(bad[DiNlink..], 1);

    // Free list, mkfs order: a 0 sentinel, then every free block from the top down.
    var sb = image.AsSpan(BlockSize, BlockSize);
    var cache = new int[NicFree];
    var count = 0;
    long free = 0;
    void Free(int block) {
      if (count >= NicFree) {
        var chain = image.AsSpan(block * BlockSize, BlockSize);
        BinaryPrimitives.WriteInt16LittleEndian(chain, (short)count);
        for (var i = 0; i < NicFree; ++i) WritePdpInt32(chain[(2 + i * 4)..], cache[i]);
        count = 0;
      }
      cache[count++] = block;
    }
    Free(0);
    for (var d = (int)fsize - 1; d >= next; --d) { Free(d); ++free; }

    BinaryPrimitives.WriteUInt16LittleEndian(sb[SIsize..], (ushort)isize);
    WritePdpInt32(sb[SFsize..], (int)fsize);
    BinaryPrimitives.WriteInt16LittleEndian(sb[SNfree..], (short)count);
    for (var i = 0; i < NicFree; ++i) WritePdpInt32(sb[(SFree + i * 4)..], cache[i]);
    // s_ninode = 0: the free-inode cache is refilled by scanning the i-list on first use.
    WritePdpInt32(sb[STime..], (int)Seconds(Now));
    WritePdpInt32(sb[STfree..], (int)free);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[STinode..], (ushort)(inodes - used));
    return image;
  }

  private static long Seconds(DateTime t) => Math.Clamp(new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds(), 0, int.MaxValue);

  private static byte[] DirectoryBytes(Node dir) {
    var entries = new List<(int Ino, string Name)> { (dir.Inode, "."), (dir.Parent?.Inode ?? RootInode, "..") };
    entries.AddRange(dir.Children.Select(c => (c.Inode, c.Name)));
    var bytes = new byte[entries.Count * DirectEntrySize];
    for (var i = 0; i < entries.Count; ++i) {
      BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * DirectEntrySize), (ushort)entries[i].Ino);
      Encoding.Latin1.GetBytes(entries[i].Name, bytes.AsSpan(i * DirectEntrySize + 2, MaxNameLength));
    }
    return bytes;
  }

  // Writes a node's data blocks (and the indirect blocks that map them) from `next`; returns the next free block.
  private long Place(byte[] image, Node node, long next) {
    const int n = AddressesPerBlock;
    var inode = image.AsSpan((int)InodeOffset(node.Inode), InodeSize);
    var mode = node.IsDirectory ? (ushort)(IfDir | 0x1ED) : (ushort)(IfReg | 0x1A4); // 0755 / 0644
    BinaryPrimitives.WriteUInt16LittleEndian(inode[DiMode..], mode);
    var links = node.IsDirectory ? 2 + node.Children.Count(c => c.IsDirectory) : 1;
    BinaryPrimitives.WriteInt16LittleEndian(inode[DiNlink..], (short)links);
    WritePdpInt32(inode[DiSize..], node.Data.Length);
    var t = (int)Seconds(node.Modified ?? Now);
    WritePdpInt32(inode[DiAtime..], t);
    WritePdpInt32(inode[DiMtime..], t);
    WritePdpInt32(inode[DiCtime..], t);

    var blocks = (node.Data.Length + BlockSize - 1) / BlockSize;
    int Take() => (int)next++;
    static int[] Table() => new int[n];
    void Flush(int block, int[] table) {
      for (var i = 0; i < n; ++i) WritePdpInt32(image.AsSpan(block * BlockSize + i * 4, 4), table[i]);
    }

    int single = 0, dbl = 0, triple = 0;
    int[]? singleT = null, dblT = null, tripleT = null;
    int[]? dblLeaf = null, tripleMid = null, tripleLeaf = null;
    int dblLeafBlock = 0, tripleMidBlock = 0, tripleLeafBlock = 0;
    for (var i = 0; i < blocks; ++i) {
      int slotOwner;
      if (i < DirectAddresses) {
        var b = Take();
        WriteAddress3(inode[(DiAddr + i * 3)..], b);
        slotOwner = b;
      } else if (i < DirectAddresses + n) {
        if (single == 0) { single = Take(); singleT = Table(); WriteAddress3(inode[(DiAddr + 30)..], single); }
        slotOwner = singleT![i - DirectAddresses] = Take();
      } else if (i < DirectAddresses + n + n * n) {
        var j = i - DirectAddresses - n;
        if (dbl == 0) { dbl = Take(); dblT = Table(); WriteAddress3(inode[(DiAddr + 33)..], dbl); }
        if (j % n == 0) {
          if (dblLeaf != null) Flush(dblLeafBlock, dblLeaf);
          dblLeafBlock = dblT![j / n] = Take();
          dblLeaf = Table();
        }
        slotOwner = dblLeaf![j % n] = Take();
      } else {
        var j = i - DirectAddresses - n - n * n;
        if (triple == 0) { triple = Take(); tripleT = Table(); WriteAddress3(inode[(DiAddr + 36)..], triple); }
        if (j % (n * n) == 0) {
          if (tripleMid != null) Flush(tripleMidBlock, tripleMid);
          tripleMidBlock = tripleT![j / (n * n)] = Take();
          tripleMid = Table();
        }
        if (j % n == 0) {
          if (tripleLeaf != null) Flush(tripleLeafBlock, tripleLeaf);
          tripleLeafBlock = tripleMid![j / n % n] = Take();
          tripleLeaf = Table();
        }
        slotOwner = tripleLeaf![j % n] = Take();
      }
      var count = Math.Min(BlockSize, node.Data.Length - i * BlockSize);
      node.Data.AsSpan(i * BlockSize, count).CopyTo(image.AsSpan(slotOwner * BlockSize));
    }
    if (singleT != null) Flush(single, singleT);
    if (dblLeaf != null) Flush(dblLeafBlock, dblLeaf);
    if (dblT != null) Flush(dbl, dblT);
    if (tripleLeaf != null) Flush(tripleLeafBlock, tripleLeaf);
    if (tripleMid != null) Flush(tripleMidBlock, tripleMid);
    if (tripleT != null) Flush(triple, tripleT);
    return next;
  }
}
