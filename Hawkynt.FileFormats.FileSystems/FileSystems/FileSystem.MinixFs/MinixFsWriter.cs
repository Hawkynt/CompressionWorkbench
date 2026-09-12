#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// Builds minimal Minix v3 filesystem images. Uses 1024-byte blocks and creates
/// real directory inodes for every path component, so a file added as
/// <c>"a/b/c.txt"</c> is stored under nested directories <c>a</c> and <c>a/b</c>.
/// Files are stored using direct zone pointers (up to 7 direct zones per file =
/// up to 7168 bytes with 1K blocks). Directories may span multiple zones: their
/// fixed-size entries fill the 7 direct zones and then a single-indirect zone,
/// allowing a directory to hold thousands of entries (7 + 1024/4 = 263 zones =
/// 4208 entries with 1K blocks).
/// </summary>
public sealed class MinixFsWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  // Per-streaming-file post-write copy descriptors, collected during Finish:
  // (absolute byte offset of the file's first data zone, logical size, opener).
  private readonly List<(long ByteOffset, long Size, Func<Stream> Opener)> _streamingSink = [];

  private const ushort MagicV3 = 0x4D5A;
  private const int BlockSize = 1024;
  private const int BootBlockSize = 1024; // block 0: boot block (unused)
  // Superblock is always at byte offset 1024
  private const int SuperblockOff = 1024;
  private const int V3InodeSize = 64;

  /// <summary>
  /// Initializes a new instance of <see cref="MinixFsWriter"/>.
  /// </summary>
  public MinixFsWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string path, byte[] data) => _files.Add((path, data, null, null));

  /// <summary>
  /// Registers a streaming file: <paramref name="size"/> drives zone allocation
  /// and inode sizing during <see cref="Finish"/>; bytes are pulled from
  /// <paramref name="openStream"/> after the metadata image is written, copied
  /// directly into the file's data zones in 64 KB chunks (only when the output
  /// stream is seekable). Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string path, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((path, System.Array.Empty<byte>(), size, openStream));
  }

  // V3 dir entry: uint32 inode (4 bytes) + char[60] name (60 bytes) = 64 bytes per entry.
  private const int DirEntrySize = 64;
  private const int MaxNameLength = 59; // 60-byte name field minus a trailing NUL.
  // Fixed-size directory entries per zone.
  private const int EntriesPerZone = BlockSize / DirEntrySize; // 16
  // V3 inode zone slots: 7 direct, then single/double/triple indirect.
  private const int DirectZones = 7;
  private const int IndirectSlot = 7;
  private const int ZonePointersPerBlock = BlockSize / 4; // 256
  // A directory addresses its zones through 7 direct slots plus one
  // single-indirect zone, so it can hold at most this many entries.
  private const int MaxDirZones = DirectZones + ZonePointersPerBlock; // 263
  private const int MaxEntriesPerDir = MaxDirZones * EntriesPerZone;   // 4208

  private const ushort ModeDirectory   = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644

  // A node in the in-memory directory tree built from the registered paths.
  /// <summary>
  /// Leave a zone unallocated where the file holds nothing but zeros.
  /// </summary>
  /// <remarks>
  /// Minix says a file's zones one pointer at a time, and a pointer of zero
  /// names no zone at all: the driver hands back a block of zeros for it and
  /// reads on. So a run of zeros need not occupy anything — the file keeps its
  /// length and every one of its bytes, and the volume is sized for what was
  /// actually written.
  /// </remarks>
  public bool MakeSparse { get; set; }

  /// <summary>
  /// Store one copy of files whose bytes are identical and give the rest a
  /// second name for it.
  /// </summary>
  /// <remarks>
  /// One inode, one set of zones, and a count in the inode of how many directory
  /// entries name it — that is all a hard link is, and minix has had it since
  /// the beginning. <c>fsck.minix</c> counts the entries pointing at each inode
  /// and compares them against that field, so linking is a matter of naming the
  /// same inode twice and saying so.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>A v3 inode counts its names in sixteen bits.</summary>
  private const int MaxLinks = ushort.MaxValue;

  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;

    /// <summary>How many names point at this inode.</summary>
    public int Links = 1;

    /// <summary>Which of this file's zones hold nothing but zeros.</summary>
    public bool[] Holes = [];
    // Child directories/files keyed by their (single-component) name, in
    // insertion order so the on-disk layout is deterministic.
    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];

    // Logical byte length: declared streaming size for a streaming entry, else
    // the buffered byte[] length.
    public long EffectiveLength => this.StreamingSize ?? this.FileData.Length;
  }

  /// <summary>
  /// Builds and writes the Minix v3 filesystem image to the output stream.
  /// </summary>
  public void Finish() {
    // --- Build the directory tree from the registered paths ---
    // Every path component becomes a real directory inode; the final
    // component becomes a file inode. The root is inode 1.
    var root = new TreeNode { IsDirectory = true };
    var allDirs = new List<TreeNode> { root };
    var allFiles = new List<TreeNode>();
    this._streamingSink.Clear();

    foreach (var (rawPath, data, streamingSize, streamOpener) in _files) {
      var parts = SplitPath(rawPath);
      if (parts.Count == 0) continue;

      var cursor = root;
      // Walk/create every intermediate directory component.
      for (var i = 0; i < parts.Count - 1; i++) {
        var component = parts[i];
        if (!cursor.ChildIndex.TryGetValue(component, out var next)) {
          next = new TreeNode { IsDirectory = true };
          AddChild(cursor, component, next);
          allDirs.Add(next);
        } else if (!next.IsDirectory) {
          throw new InvalidOperationException(
            $"MinixFs: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }

      // Final component: the file itself.
      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"MinixFs: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data, StreamingSize = streamingSize, StreamOpener = streamOpener };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }

    // Enforce the addressable-zone limit per directory early with a clear message.
    EnsureDirectoriesAddressable(root, "");

    // --- Assign 1-based inode numbers: root = 1, then dirs, then files ---
    // Root keeps inode 1 so the reader (which starts at inode 1) finds it.
    root.Inode = 1;
    var nextInode = 2u;
    foreach (var dir in allDirs) {
      if (dir == root) continue;
      dir.Inode = nextInode++;
    }
    // Files whose bytes are identical share one inode when asked, so the rest of
    // the build only ever sees the stored ones and the content is laid down once
    // however many names lead to it. Reading each entry once settles both which
    // files are copies of each other and which of their zones are nothing but
    // zeros — the size the volume is laid out for depends on the second, so it
    // has to be decided before anything is placed.
    var stored = new List<TreeNode>(allFiles.Count);
    var firstWithContent = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
    foreach (var file in allFiles) {
      var (key, fileHoles) = this.Fingerprint(file);
      if (this.DeduplicateWithLinks && firstWithContent.TryGetValue(key, out var first)) {
        file.Inode = first.Inode;
        ++first.Links;
        // An inode that has run out of room to count its names starts a fresh
        // one for the next copy rather than wrapping round to none.
        if (first.Links >= MaxLinks) firstWithContent.Remove(key);
        continue;
      }

      file.Inode = nextInode++;
      file.Holes = fileHoles;
      if (this.DeduplicateWithLinks) firstWithContent[key] = file;
      stored.Add(file);
    }
    var totalInodes = (int)nextInode - 1;

    // --- Layout calculation ---
    // Block 0:  boot block (1024 bytes, unused)
    // Block 1:  superblock (1024 bytes at offset 1024)
    // Block 2 onwards: inode bitmap (1 block), zone bitmap (1 block),
    //                  inode table, then data zones.
    var inodesPerBlock = BlockSize / V3InodeSize;
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;

    // Zones needed: per directory, ceil(entries/EntriesPerZone) data zones plus
    // one indirect zone when it spills past the 7 direct slots; per file,
    // ceil(size/blocksize) data zones.
    var dataZonesNeeded = 0;
    foreach (var dir in allDirs) {
      var dirZones = DirectoryDataZoneCount(dir);
      dataZonesNeeded += dirZones + (dirZones > DirectZones ? 1 : 0);
    }
    // What a file costs is counted by laying it out with nowhere to write it,
    // so the number the volume is sized for and the layout it ends up with come
    // from one piece of code rather than from two descriptions of it.
    foreach (var file in stored) {
      var counter = 0;
      LayoutFile(null, 0, 0, 0, ref counter, file, null);
      dataZonesNeeded += counter;
    }

    // Both bitmaps reserve bit 0, and a block of bitmap covers BlockSize * 8
    // entries. Fixing either at one block silently caps the volume at that many
    // — and the bits past the end land in whatever follows, which for the zone
    // bitmap is the inode table. An 8 MB volume was the last one that fitted.
    const int bitsPerBlock = BlockSize * 8;
    var imapBlocks = (totalInodes + 1 + bitsPerBlock - 1) / bitsPerBlock;
    var zmapBlocks = (dataZonesNeeded + 1 + bitsPerBlock - 1) / bitsPerBlock;

    // firstdatazone = block index of first data zone
    // Layout: block0 (boot) + block1 (superblock) + imapBlocks + zmapBlocks + inodeTableBlocks
    var firstdatazone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;

    var totalZones = firstdatazone + dataZonesNeeded;
    var totalBlocks = totalZones; // zones == blocks for log_zone_size=0
    // The image is built in memory, so a payload past what an array can hold
    // has to be refused here. Multiplying it out instead produced an
    // arithmetic overflow that said nothing about the volume's capacity.
    var diskSize = (long)totalBlocks * BlockSize;
    if (diskSize > System.Array.MaxLength)
      throw new InvalidOperationException(
        $"Minix: the payload needs {totalBlocks:N0} blocks ({diskSize:N0} bytes), more than " +
        $"the {System.Array.MaxLength:N0} bytes this writer lays out in memory.");
    var disk = new byte[diskSize];

    // --- Bitmap / inode-table offsets ---
    var imapOff = 2 * BlockSize;                              // inode bitmap follows the superblock
    var zmapOff = (2 + imapBlocks) * BlockSize;                // zone bitmap follows the inode bitmap
    var inodeTableOff = (2 + imapBlocks + zmapBlocks) * BlockSize;

    // Inode N occupies bit N of the inode bitmap, and bit 0 is reserved for the
    // inode number that means "none". mkfs.minix leaves bits 0 and 1 set on a
    // fresh volume: the reserved one and the root.
    SetBit(disk, imapOff, 0, imapBlocks * BlockSize);
    for (var ino = 1; ino <= totalInodes; ino++)
      SetBit(disk, imapOff, ino, imapBlocks * BlockSize);

    // The zone bitmap covers the data zones only, and counts from the first of
    // them: absolute zone Z occupies bit Z - firstdatazone + 1, with bit 0
    // reserved as the inode bitmap's is. The metadata zones below firstdatazone
    // are not in it at all.
    SetBit(disk, zmapOff, 0, zmapBlocks * BlockSize);

    var nextZone = firstdatazone;

    // --- Allocate and write each directory's zone, then its inode ---
    // Parent link counts: a directory's i_nlinks = 2 (self "." + parent's entry)
    // plus one extra per child directory (the child's ".." points back).
    foreach (var dir in allDirs) {
      // The full entry stream: ".", "..", then one entry per child.
      var entries = new List<(uint Inode, string Name)>(dir.Children.Count + 2) {
        (dir.Inode, "."),
        (ParentInode(root, dir), ".."),
      };
      foreach (var (childName, child) in dir.Children)
        entries.Add((child.Inode, childName));

      var zoneCount = (entries.Count + EntriesPerZone - 1) / EntriesPerZone;
      var dirZones = new uint[zoneCount];

      // Render and place each data zone; entries never cross a zone boundary.
      for (var z = 0; z < zoneCount; z++) {
        var zone = (uint)nextZone++;
        SetBit(disk, zmapOff, (int)zone - firstdatazone + 1, zmapBlocks * BlockSize);
        dirZones[z] = zone;

        var zoneData = new byte[BlockSize];
        var first = z * EntriesPerZone;
        var last = Math.Min(first + EntriesPerZone, entries.Count);
        for (var e = first; e < last; e++) {
          var (ino, name) = entries[e];
          WriteDirEntry(zoneData, (e - first) * DirEntrySize, ino, name);
        }
        zoneData.CopyTo(disk, (int)zone * BlockSize);
      }

      // Inode zone slots: up to 7 direct, then a single-indirect zone listing
      // the remaining data zones.
      var inodeZones = new uint[10];
      for (var z = 0; z < zoneCount && z < DirectZones; z++)
        inodeZones[z] = dirZones[z];
      if (zoneCount > DirectZones) {
        var indirectZone = (uint)nextZone++;
        SetBit(disk, zmapOff, (int)indirectZone - firstdatazone + 1, zmapBlocks * BlockSize);
        inodeZones[IndirectSlot] = indirectZone;
        var table = new byte[BlockSize];
        for (var z = DirectZones; z < zoneCount; z++)
          BinaryPrimitives.WriteUInt32LittleEndian(
            table.AsSpan((z - DirectZones) * 4), dirZones[z]);
        table.CopyTo(disk, (int)indirectZone * BlockSize);
      }

      var childDirCount = 0;
      foreach (var (_, child) in dir.Children)
        if (child.IsDirectory) childDirCount++;

      WriteV3Inode(disk, inodeTableOff, inodeIndex: (int)(dir.Inode - 1),
        mode: ModeDirectory,
        size: (uint)(zoneCount * BlockSize),
        nlinks: (ushort)(2 + childDirCount),
        zones: inodeZones);
    }

    // --- Allocate and write each file's zones, then its inode ---
    foreach (var file in stored) {
      var placements = file.StreamOpener == null ? null : new List<(long To, long From, int Span)>();
      var fileZones = LayoutFile(disk, zmapOff, zmapBlocks * BlockSize, firstdatazone, ref nextZone, file, placements);

      WriteV3Inode(disk, inodeTableOff, inodeIndex: (int)(file.Inode - 1),
        mode: ModeRegularFile,
        size: (uint)file.EffectiveLength,
        nlinks: (ushort)file.Links,
        zones: fileZones);

      // A streaming entry leaves its data zones empty here; BuildToStreaming
      // fills them afterwards from the source. One run per zone rather than one
      // for the file, because indirect addressing and holes both mean the zones
      // a file owns need not be consecutive.
      if (placements == null) continue;
      var opener = file.StreamOpener!;
      foreach (var (to, from, span) in placements) {
        var at = from;
        this._streamingSink.Add((to, span, () => OpenAt(opener, at)));
      }
    }

    // --- Superblock at offset 1024 ---
    // V3 superblock layout (little-endian):
    //  uint32 s_ninodes        [0]
    //  uint16 s_pad0           [4]
    //  uint16 s_imap_blocks    [6]
    //  uint16 s_zmap_blocks    [8]
    //  uint16 s_firstdatazone  [10]
    //  uint16 s_log_zone_size  [12]
    //  uint16 s_pad1           [14]
    //  uint32 s_max_size       [16]
    //  uint32 s_zones          [20]
    //  uint16 s_magic          [24]
    //  uint16 s_pad2           [26]
    //  uint16 s_blocksize      [28]
    //  uint8  s_disk_version   [30]
    var sb = disk.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt32LittleEndian(sb,              (uint)totalInodes);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),     (ushort)imapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(8),     (ushort)zmapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(10),    (ushort)firstdatazone);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(12),    0); // log_zone_size = 0 (zone==block)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(16),    (uint)diskSize); // s_max_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(20),    (uint)totalZones);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(24),    MagicV3);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(28),    BlockSize);

    if (this._streamingSink.Count == 0) {
      // No streaming entries — emit the fully populated image as before.
      _output.Write(disk);
      return;
    }

    // Streaming entries present: the metadata image is complete with their data
    // zones left zero. Write it, then seek to each entry's first data zone and
    // stream its bytes in 64 KB chunks. Byte-identical to the buffered path.
    if (!_output.CanSeek || !_output.CanWrite)
      throw new InvalidOperationException(
        "MinixFs: streaming entries require a writable, seekable output stream.");

    _output.Position = 0;
    _output.Write(disk);

    var buf = new byte[64 * 1024];
    foreach (var (byteOffset, size, opener) in this._streamingSink) {
      if (size <= 0) continue;
      if (byteOffset < 0 || byteOffset >= disk.Length) continue;
      _output.Position = byteOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        _output.Write(buf, 0, n);
        copied += n;
      }
    }
    _output.Flush();
  }

  // Splits a registered path into its directory/file components, discarding
  // empty segments produced by leading/trailing/duplicate separators.
  private static List<string> SplitPath(string path) {
    var parts = new List<string>();
    foreach (var raw in path.Replace('\\', '/').Split('/')) {
      if (raw.Length == 0 || raw is "." or "..") continue;
      var component = raw.Length > MaxNameLength ? raw[..MaxNameLength] : raw;
      parts.Add(component);
    }
    return parts;
  }

  private static void AddChild(TreeNode parent, string name, TreeNode child) {
    parent.Children.Add(new KeyValuePair<string, TreeNode>(name, child));
    parent.ChildIndex[name] = child;
  }

  // Resolves the parent inode of a directory node (root's parent is itself).
  private static uint ParentInode(TreeNode root, TreeNode target) {
    if (target == root) return root.Inode;
    var found = FindParent(root, target);
    return found?.Inode ?? root.Inode;
  }

  private static TreeNode? FindParent(TreeNode node, TreeNode target) {
    foreach (var (_, child) in node.Children) {
      if (child == target) return node;
      if (child.IsDirectory) {
        var deeper = FindParent(child, target);
        if (deeper != null) return deeper;
      }
    }
    return null;
  }

  // Number of data zones a directory's entries (".", "..", children) occupy.
  /// <summary>
  /// What a file is worth knowing before it is placed: a key two identical
  /// files share, and which of its zones hold nothing but zeros.
  /// </summary>
  /// <remarks>
  /// Both come out of one read, because a streaming entry is expensive to open
  /// and there is no reason to open it twice. Either half is skipped when the
  /// switch that needs it is off.
  /// </remarks>
  private (string Key, bool[] Holes) Fingerprint(TreeNode file) {
    var length = file.EffectiveLength;
    if (!this.MakeSparse && !this.DeduplicateWithLinks) return (string.Empty, []);

    var zoneCount = length <= 0 ? 0 : (int)((length + BlockSize - 1) / BlockSize);
    var holes = this.MakeSparse ? new bool[zoneCount] : [];
    var digest = System.Security.Cryptography.SHA256.Create();

    if (file.StreamOpener != null) {
      using var source = file.StreamOpener();
      var buffer = new byte[BlockSize];
      for (var z = 0; z < zoneCount; z++) {
        var want = (int)Math.Min(BlockSize, length - (long)z * BlockSize);
        var read = 0;
        while (read < want) {
          var n = source.Read(buffer, read, want - read);
          if (n <= 0) break;
          read += n;
        }
        if (this.MakeSparse) holes[z] = !buffer.AsSpan(0, read).ContainsAnyExcept((byte)0);
        if (this.DeduplicateWithLinks) digest.TransformBlock(buffer, 0, read, null, 0);
      }
    } else {
      for (var z = 0; z < zoneCount; z++) {
        var at = z * BlockSize;
        var want = Math.Min(BlockSize, file.FileData.Length - at);
        if (this.MakeSparse)
          holes[z] = want <= 0 || !file.FileData.AsSpan(at, want).ContainsAnyExcept((byte)0);
        if (this.DeduplicateWithLinks && want > 0)
          digest.TransformBlock(file.FileData, at, want, null, 0);
      }
    }

    if (!this.DeduplicateWithLinks) { digest.Dispose(); return (string.Empty, holes); }

    digest.TransformFinalBlock([], 0, 0);
    // The length goes in the key as well as the digest: the digest is what says
    // two files are the same, and the length is what makes saying so cheap to be
    // sure of.
    var key = length.ToString(System.Globalization.CultureInfo.InvariantCulture)
      + ":" + Convert.ToHexString(digest.Hash!);
    digest.Dispose();
    return (key, holes);
  }

  /// <summary>
  /// Places one file's zones and returns its ten inode slots.
  /// </summary>
  /// <remarks>
  /// <para>A null <paramref name="disk" /> hands out zone numbers without
  /// writing anything, which is how the volume is sized: the count and the
  /// layout then come from one piece of code and cannot disagree.</para>
  ///
  /// <para>Seven direct slots, then a single-, double- and triple-indirect tree
  /// of 32-bit pointers, 256 to a block. This used to refuse any file past the
  /// seven direct zones outright — 7 168 bytes — while the reader beside it read
  /// all four levels quite happily, so a volume from a real minix system could be
  /// read here and never written. It also meant three formats were passed over
  /// by the checks that ask an outside tool for an opinion, because they could
  /// not build the probe volume at all.</para>
  /// </remarks>
  private uint[] LayoutFile(byte[]? disk, int zmapOff, int zmapBytes, int firstdatazone, ref int nextZone,
      TreeNode file, List<(long To, long From, int Span)>? placements) {
    var slots = new uint[10];
    var length = file.EffectiveLength;
    if (length <= 0) return slots;

    var zoneCount = (int)((length + BlockSize - 1) / BlockSize);
    var holes = file.Holes;
    var dataZones = new uint[zoneCount];
    for (var z = 0; z < zoneCount; z++) {
      // A hole keeps its place in the pointer list and takes no zone: the
      // pointer stays zero, and a reader hands back a block of zeros for it.
      if (z < holes.Length && holes[z]) continue;

      var zone = (uint)nextZone++;
      dataZones[z] = zone;
      if (disk == null) continue;

      SetBit(disk, zmapOff, (int)zone - firstdatazone + 1, zmapBytes);
      var at = (long)z * BlockSize;
      var span = (int)Math.Min(BlockSize, length - at);
      if (placements != null) {
        placements.Add(((long)zone * BlockSize, at, span));
        continue;
      }

      var available = (int)Math.Min(span, file.FileData.Length - at);
      if (available > 0) Array.Copy(file.FileData, at, disk, (long)zone * BlockSize, available);
    }

    var idx = 0;
    for (; idx < zoneCount && idx < DirectZones; idx++)
      slots[idx] = dataZones[idx];
    if (idx == zoneCount) return slots;

    for (var level = 1; level <= 3 && idx < zoneCount; ++level)
      slots[DirectZones + level - 1] =
        BuildIndirect(disk, zmapOff, zmapBytes, firstdatazone, ref nextZone, dataZones, ref idx, zoneCount, level);

    if (idx < zoneCount)
      throw new InvalidOperationException(
        $"MinixFs: a file of {length:N0} bytes is past what triple-indirect addressing reaches.");
    return slots;
  }

  /// <summary>
  /// Builds one indirect tree of the given level and returns the zone it is
  /// rooted at, or zero — having taken no zone — when everything it would
  /// address is hole.
  /// </summary>
  /// <remarks>
  /// A slot of zero is how a volume records a gap nobody wrote: the kernel never
  /// asks for a block it is not filling. A block of nothing but zero pointers
  /// would read back the same and still be a volume no minix system produced.
  /// </remarks>
  private static uint BuildIndirect(byte[]? disk, int zmapOff, int zmapBytes, int firstdatazone, ref int nextZone,
      uint[] dataZones, ref int idx, int total, int level) {
    var reach = ZonePointersPerBlock;
    for (var i = 1; i < level; ++i) reach *= ZonePointersPerBlock;

    var end = Math.Min(total, idx + reach);
    var allHole = true;
    for (var probe = idx; probe < end; probe++)
      if (dataZones[probe] != 0) { allHole = false; break; }

    if (allHole) {
      idx = end;
      return 0;
    }

    var root = (uint)nextZone++;
    var baseByte = (long)root * BlockSize;
    if (disk != null) SetBit(disk, zmapOff, (int)root - firstdatazone + 1, zmapBytes);

    if (level == 1) {
      var count = Math.Min(ZonePointersPerBlock, total - idx);
      for (var i = 0; i < count; i++) {
        var zone = dataZones[idx++];
        if (disk != null)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), zone);
      }
      return root;
    }

    for (var i = 0; i < ZonePointersPerBlock && idx < total; i++) {
      var child = BuildIndirect(disk, zmapOff, zmapBytes, firstdatazone, ref nextZone, dataZones, ref idx, total, level - 1);
      if (disk != null)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), child);
    }
    return root;
  }

  /// <summary>Reopens a streaming entry positioned at the zone that needs it.</summary>
  /// <remarks>
  /// A sparse file's zones are no longer one run, so each is filled from its own
  /// offset in the source rather than by one copy from the start.
  /// </remarks>
  private static Stream OpenAt(Func<Stream> opener, long offset) {
    var source = opener();
    if (source.CanSeek) { source.Position = offset; return source; }

    var skipped = 0L;
    var scratch = new byte[BlockSize];
    while (skipped < offset) {
      var n = source.Read(scratch, 0, (int)Math.Min(scratch.Length, offset - skipped));
      if (n <= 0) break;
      skipped += n;
    }
    return source;
  }

  private static int DirectoryDataZoneCount(TreeNode dir) {
    var entryCount = 2 + dir.Children.Count; // "." and ".."
    return (entryCount + EntriesPerZone - 1) / EntriesPerZone;
  }

  // A directory's data zones must be reachable through 7 direct slots plus one
  // single-indirect zone.
  private static void EnsureDirectoriesAddressable(TreeNode dir, string path) {
    var entryCount = 2 + dir.Children.Count; // "." and ".."
    if (entryCount > MaxEntriesPerDir)
      throw new InvalidOperationException(
        $"MinixFs writer addresses a directory through {DirectZones} direct zones plus one " +
        $"single-indirect zone (max {MaxEntriesPerDir - 2} entries); directory " +
        $"'{(path.Length == 0 ? "/" : path)}' has {dir.Children.Count} entries.");
    foreach (var (name, child) in dir.Children)
      if (child.IsDirectory)
        EnsureDirectoriesAddressable(child, path.Length == 0 ? name : $"{path}/{name}");
  }

  private static void WriteDirEntry(byte[] dirData, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(offset), inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    nameBytes.AsSpan(0, copyLen).CopyTo(dirData.AsSpan(offset + 4));
    // null terminator already present (array zero-initialized)
  }

  private static void WriteV3Inode(byte[] disk, int tableOff, int inodeIndex,
      ushort mode, uint size, ushort nlinks, uint[] zones) {
    // Real Minix3 inode layout (little-endian, 64 bytes total):
    //   uint16 i_mode   [0]
    //   uint16 i_nlinks [2]
    //   uint16 i_uid    [4]
    //   uint16 i_gid    [6]
    //   uint32 i_size   [8]
    //   uint32 i_atime  [12]
    //   uint32 i_mtime  [16]
    //   uint32 i_ctime  [20]
    //   uint32 i_zone[10] [24..63]
    var off = tableOff + inodeIndex * V3InodeSize;
    var span = disk.AsSpan(off, V3InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span,          mode);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), nlinks); // i_nlinks
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), size); // i_size
    // i_zone[10] at offset 24
    for (var i = 0; i < Math.Min(zones.Length, 10); i++)
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24 + i * 4), zones[i]);
  }

  private static void SetBit(byte[] data, int bitmapOffset, int bitIndex, int bitmapBytes) {
    var at = bitmapOffset + bitIndex / 8;
    // Running off the end of a bitmap used to write into whatever followed it,
    // which for the zone bitmap is the inode table: the volume then read back as
    // empty rather than as broken. Say so instead.
    if (bitIndex < 0 || at >= bitmapOffset + bitmapBytes)
      throw new InvalidOperationException(
        $"Minix: bit {bitIndex} falls outside the {bitmapBytes:N0}-byte bitmap at offset {bitmapOffset:N0}.");

    data[at] |= (byte)(1 << (bitIndex % 8));
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}
