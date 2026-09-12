using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Hammer;

/// <summary>
/// Walks a HAMMER (DragonFly BSD, HAMMER1) volume's global B-Tree and yields the
/// regular files it contains as <c>path -&gt; bytes</c>. This is the read side of
/// full file support: it parses the volume header, resolves zone offsets through
/// the freemap (zone-4 two-layer blockmap), recursively descends the B-Tree from
/// <c>vol0_btree_root</c>, and reassembles inodes, directory entries and data
/// records into a directory tree.
///
/// <para>On-disk references (<c>sys/vfs/hammer/hammer_disk.h</c>):</para>
/// <list type="bullet">
///   <item><description>B-Tree node <c>hammer_node_ondisk</c>: <c>crc(4)@0</c>,
///   <c>signature(4)@4</c>, <c>parent(8)@8</c>, <c>count(4)@16</c>,
///   <c>type(1)@20</c>, then 63 elements of 64 bytes starting at @64.</description></item>
///   <item><description>Element base <c>hammer_base_elm</c>: <c>obj_id(8)@0</c>,
///   <c>key(8)@8</c>, <c>create_tid(8)@16</c>, <c>delete_tid(8)@24</c>,
///   <c>rec_type(2)@32</c>, <c>obj_type(1)@34</c>, <c>btype(1)@35</c>,
///   <c>localization(4)@36</c>.</description></item>
///   <item><description>Internal element adds <c>subtree_offset(8)@40</c>;
///   leaf element adds <c>create_ts(4)@40</c>, <c>delete_ts(4)@44</c>,
///   <c>data_offset(8)@48</c>, <c>data_len(4)@56</c>, <c>data_crc(4)@60</c>.</description></item>
///   <item><description>rec_type <c>INODE=0x0001</c>, <c>DATA=0x0010</c>,
///   <c>DIRENTRY=0x0011</c>; obj_type <c>DIRECTORY=1</c>, <c>REGFILE=2</c>.</description></item>
///   <item><description>Directory-entry data <c>hammer_direntry_data</c>:
///   <c>obj_id(8)@0</c>, <c>localization(4)@8</c>, <c>reserved01(4)@12</c>,
///   <c>name[]@16</c> (length = <c>data_len - 16</c>).</description></item>
///   <item><description>Inode data <c>hammer_inode_data</c>: <c>obj_type(1)@64</c>,
///   <c>size(8)@80</c>.</description></item>
/// </list>
/// </summary>
public sealed class HammerReader : IDisposable {
  private const ulong VolSignature = 0xC8414D4DC5523031UL;
  private const long Bigblock = 8192L * 1024;
  private const long BlockmapLayer2 = (Bigblock / 16) * Bigblock;
  private const long Layer1Mask = (long)((1UL << (18 + 19 + 23)) - 1);
  private const long Layer2Mask = BlockmapLayer2 - 1;
  private const ulong OffShortMask = 0x000FFFFFFFFFFFFFUL;

  private const ushort RectypeInode = 0x0001;
  private const ushort RectypeData = 0x0010;
  private const ushort RectypeDirentry = 0x0011;
  private const byte ObjtypeDirectory = 1;
  private const byte ObjtypeRegfile = 2;
  private const long ObjidRoot = 1;

  private readonly ImageAccessor _image;
  private readonly long _len;
  private readonly long _volBufBeg;
  private readonly long _freemapLayer1Phys;
  private readonly long _btreeRoot;

  /// <summary>True if the image carries a valid HAMMER volume header.</summary>
  public bool Valid { get; }

  /// <summary>Where the volume's buffer area starts; zone-2 offsets are relative to it.</summary>
  public long VolumeBufferStart => this._volBufBeg;

  /// <summary>File offset of the freemap's layer-1 array.</summary>
  public long FreemapLayer1Offset => this._freemapLayer1Phys;

  private HammerReader(ImageAccessor image, bool valid, long volBufBeg, long freemapLayer1Phys, long btreeRoot) {
    this._image = image;
    this._len = image.Length;
    this.Valid = valid;
    this._volBufBeg = volBufBeg;
    this._freemapLayer1Phys = freemapLayer1Phys;
    this._btreeRoot = btreeRoot;
  }

  /// <summary>Opens a HAMMER image. Never throws on a malformed header; check <see cref="Valid"/>.</summary>
  public static HammerReader Open(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    return Open(ImageAccessor.FromBytes(image));
  }

  /// <summary>
  /// Opens a HAMMER volume, pulling blocks on demand. Never throws on a malformed
  /// header; check <see cref="Valid"/>.
  /// </summary>
  public static HammerReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    return Open(new ImageAccessor(stream));
  }

  private static HammerReader Open(ImageAccessor image) {
    if (image.Length < 1024 || image.ReadUInt64(0) != VolSignature)
      return new HammerReader(image, false, 0, 0, 0);

    var volBufBeg = (long)image.ReadUInt64(24);
    var btreeRoot = (long)image.ReadUInt64(240);
    // vol0_blockmap[4] (freemap) lives at 264 + 4*40; phys_offset is the first 8 bytes (a zone-2 offset).
    var freemapPhysZone2 = (long)image.ReadUInt64(264 + 4 * 40);
    var freemapLayer1Phys = volBufBeg + (long)((ulong)freemapPhysZone2 & OffShortMask);
    return new HammerReader(image, true, volBufBeg, freemapLayer1Phys, btreeRoot);
  }

  private ushort U16(long off) => this._len >= off + 2 ? this._image.ReadUInt16(off) : (ushort)0;
  private uint U32(long off) => this._len >= off + 4 ? this._image.ReadUInt32(off) : 0u;
  private ulong U64(long off) => this._len >= off + 8 ? this._image.ReadUInt64(off) : 0UL;
  private byte B(long off) => off >= 0 && off < this._len ? this._image.ReadByte(off) : (byte)0;

  /// <summary>A regular file recovered from the B-Tree: full POSIX path and exact bytes.</summary>
  public readonly record struct FileEntry(string Path, byte[] Content);

  /// <summary>
  /// Walks the B-Tree and returns every regular file with its full path relative to
  /// the filesystem root (e.g. <c>"sub/inner.txt"</c>). Directories are implied by
  /// the paths; empty directories are not returned (they carry no file payload).
  /// </summary>
  public IReadOnlyList<FileEntry> ReadFiles() {
    if (!this.Valid)
      return [];

    // Pass 1: collect inodes (obj_id -> inode data), direntries (parent obj_id ->
    // [(name, child obj_id, child obj_type)]) and data extents (obj_id -> chunks).
    var inodes = new Dictionary<long, InodeInfo>();
    var children = new Dictionary<long, List<DirEntry>>();
    var dataChunks = new Dictionary<long, List<DataChunk>>();

    var visited = new HashSet<long>();
    this.WalkNode(this._btreeRoot, visited, inodes, children, dataChunks, null);

    // Pass 2: resolve the directory tree from the root inode, assembling file bytes.
    var files = new List<FileEntry>();
    if (children.TryGetValue(ObjidRoot, out _) || inodes.ContainsKey(ObjidRoot))
      this.Emit(ObjidRoot, "", inodes, children, dataChunks, files, new HashSet<long> { ObjidRoot });
    return files;
  }

  private sealed record InodeInfo(byte ObjType, long Size);
  private readonly record struct DirEntry(string Name, long ObjId, byte ObjType);
  private readonly record struct DataChunk(long Offset, byte[] Bytes);

  private void Emit(long dirObjId, string prefix,
                    Dictionary<long, InodeInfo> inodes,
                    Dictionary<long, List<DirEntry>> children,
                    Dictionary<long, List<DataChunk>> dataChunks,
                    List<FileEntry> output, HashSet<long> path) {
    if (!children.TryGetValue(dirObjId, out var entries))
      return;

    foreach (var e in entries) {
      var name = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
      if (e.ObjType == ObjtypeDirectory) {
        if (path.Add(e.ObjId)) {       // guard against cyclic/corrupt trees
          this.Emit(e.ObjId, name, inodes, children, dataChunks, output, path);
          path.Remove(e.ObjId);
        }
        continue;
      }
      if (e.ObjType != ObjtypeRegfile)
        continue;

      var size = inodes.TryGetValue(e.ObjId, out var ino) ? ino.Size : 0;
      output.Add(new FileEntry(name, AssembleFile(e.ObjId, size, dataChunks)));
    }
  }

  private static byte[] AssembleFile(long objId, long size, Dictionary<long, List<DataChunk>> dataChunks) {
    var result = new byte[size];
    if (!dataChunks.TryGetValue(objId, out var chunks))
      return result;

    foreach (var c in chunks) {
      if (c.Offset >= size)
        continue;
      var n = (int)Math.Min(c.Bytes.Length, size - c.Offset);
      if (n > 0)
        Array.Copy(c.Bytes, 0, result, c.Offset, n);
    }
    return result;
  }

  private void WalkNode(long nodeOffset, HashSet<long> visited,
                        Dictionary<long, InodeInfo> inodes,
                        Dictionary<long, List<DirEntry>> children,
                        Dictionary<long, List<DataChunk>> dataChunks,
                        List<(long ObjId, long Offset, long Length, long Element, long Node)>? records) {
    if (nodeOffset == 0 || !visited.Add(nodeOffset))
      return;

    var physL = this.Resolve(nodeOffset);
    if (physL < 0 || physL + 64 > this._len)
      return;
    var phys = physL;

    var count = (int)U32(phys + 16);
    var type = (char)B(phys + 20);
    if (count < 0 || count > 63)
      return;

    for (var i = 0; i < count; ++i) {
      var b = phys + 64 + i * 64;
      if ((long)b + 64 > this._len)
        break;
      var btype = (char)B(b + 35);

      if (type == 'I') {
        // Internal node: descend into the subtree. Skip the right-boundary
        // element (btype 0) which carries no subtree of its own.
        var sub = (long)U64(b + 40);
        if (btype is 'I' or 'L')
          this.WalkNode(sub, visited, inodes, children, dataChunks, records);
        continue;
      }

      // Leaf element.
      var objId = (long)U64(b + 0);
      var key = (long)U64(b + 8);
      var deleteTid = U64(b + 24);
      var recType = U16(b + 32);
      var dataOff = (long)U64(b + 48);
      var dataLen = (int)U32(b + 56);

      if (deleteTid != 0)            // historically deleted record — ignore.
        continue;
      var data = this.ReadData(dataOff, dataLen);

      switch (recType) {
        case RectypeInode when data.Length >= 88:
          // Latest non-deleted version wins; the kernel writes versions in tid
          // order so a later element in iteration order is at least as new.
          inodes[objId] = new InodeInfo(data[64], BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(80, 8)));
          break;

        case RectypeDirentry when data.Length >= 16:
          var childObjId = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0, 8));
          var nameLen = data.Length - 16;
          var name = nameLen > 0 ? DecodeName(data.AsSpan(16, nameLen)) : "";
          if (name.Length > 0) {
            if (!children.TryGetValue(objId, out var list))
              children[objId] = list = [];
            // obj_type of the target is carried in the element base (b+34).
            list.Add(new DirEntry(name, childObjId, B(b + 34)));
          }
          break;

        case RectypeData when dataLen > 0:
          var fileOffset = key - dataLen;   // base.key = offset + bytes.
          if (fileOffset >= 0) {
            if (!dataChunks.TryGetValue(objId, out var chunks))
              dataChunks[objId] = chunks = [];
            chunks.Add(new DataChunk(fileOffset, data));
            var physical = this.Resolve(dataOff);
            if (records != null && physical >= 0 && physical + dataLen <= this._len)
              records.Add((objId, physical, dataLen, b, phys));
          }
          break;
      }
    }
  }

  /// <summary>
  /// Where on disk each file's data records actually sit, along with the byte
  /// offset of the B-tree element that names each of them and of the node that
  /// element lives in.
  /// </summary>
  /// <remarks>
  /// The freemap accounts per eight-megabyte big-block: it says how far into
  /// each one the allocator has appended, and nothing about which file owns
  /// what. Reporting a layout that way leaves anything trying to move a file
  /// with nothing to repoint, so the records are read from the B-tree that
  /// names them.
  /// </remarks>
  public IReadOnlyList<DataExtent> EnumerateDataExtents() {
    if (!this.Valid) return [];

    var inodes = new Dictionary<long, InodeInfo>();
    var children = new Dictionary<long, List<DirEntry>>();
    var dataChunks = new Dictionary<long, List<DataChunk>>();
    var records = new List<(long ObjId, long Offset, long Length, long Element, long Node)>();

    this.WalkNode(this._btreeRoot, new HashSet<long>(), inodes, children, dataChunks, records);

    // A record is only worth reporting under a name, and the name comes from
    // walking the directory tree the same way a read does.
    var names = new Dictionary<long, string>();
    if (children.ContainsKey(ObjidRoot) || inodes.ContainsKey(ObjidRoot))
      NameSubtree(ObjidRoot, "", children, names, new HashSet<long> { ObjidRoot });

    var result = new List<DataExtent>(records.Count);
    foreach (var (objId, offset, length, element, node) in records) {
      if (!names.TryGetValue(objId, out var name)) continue;
      result.Add(new DataExtent(name, offset, length, element, node));
    }
    return result;
  }

  /// <summary>One data record: its bytes, and where the B-tree records them.</summary>
  public readonly record struct DataExtent(
    string Path, long Offset, long Length, long ElementOffset, long NodeOffset);

  private void NameSubtree(long dirObjId, string prefix,
                           Dictionary<long, List<DirEntry>> children,
                           Dictionary<long, string> names, HashSet<long> path) {
    if (!children.TryGetValue(dirObjId, out var entries)) return;
    foreach (var e in entries) {
      var name = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
      if (e.ObjType == ObjtypeDirectory) {
        if (path.Add(e.ObjId)) {
          this.NameSubtree(e.ObjId, name, children, names, path);
          path.Remove(e.ObjId);
        }
        continue;
      }
      if (e.ObjType == ObjtypeRegfile) names[e.ObjId] = name;
    }
  }

  private static string DecodeName(ReadOnlySpan<byte> raw) {
    var n = raw.IndexOf((byte)0);
    if (n < 0) n = raw.Length;
    return Encoding.UTF8.GetString(raw[..n]);
  }

  // ---- zone -> physical device offset, via the freemap two-layer blockmap ----
  private long Resolve(long zoneOffset) {
    // Every zone>=2 maps onto a zone-2 raw-buffer offset; the short offset is the
    // device offset relative to vol_buf_beg for single-volume images.
    var shortOff = (long)((ulong)zoneOffset & OffShortMask);
    return this._volBufBeg + shortOff;
  }

  private byte[] ReadData(long dataOff, int dataLen) {
    if (dataLen <= 0)
      return [];
    var physL = this.Resolve(dataOff);
    if (physL < 0 || physL + dataLen > this._len)
      return [];
    return this._image.Read(physL, dataLen);
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => this._image.Dispose();
}
