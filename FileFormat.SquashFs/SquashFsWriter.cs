using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;

namespace FileFormat.SquashFs;

/// <summary>
/// Writes a SquashFS version 4 filesystem image using gzip (zlib) compression
/// for data blocks. Metadata blocks (inodes, directories, IDs) use zlib
/// compression with automatic fallback to uncompressed when compression
/// does not reduce size.
/// </summary>
public sealed class SquashFsWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;
  private readonly List<PendingFile> _files = [];
  private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

  private const uint BlockSize = 131072;
  private const ushort BlockLog = 17;
  private const int MetaMax = SquashFsConstants.MetadataBlockMaxSize; // 8192

  /// <summary>
  /// Initializes a new <see cref="SquashFsWriter"/> that writes to <paramref name="stream"/>.
  /// The image is finalized when <see cref="Dispose"/> is called.
  /// </summary>
  public SquashFsWriter(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file entry to the image.</summary>
  public void AddFile(string path, byte[] data, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    path = NormalizePath(path);
    _files.Add(new PendingFile(path, data, lastModified ?? DateTime.UtcNow));
    EnsureParentDirs(path);
  }

  /// <summary>Adds an explicit directory entry to the image.</summary>
  public void AddDirectory(string path, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    path = NormalizePath(path);
    _directories.Add(path);
    EnsureParentDirs(path);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Flush();
    if (!_leaveOpen) _stream.Dispose();
  }

  // ──── Internal types ────

  private sealed record PendingFile(string Path, byte[] Data, DateTime Modified);

  private sealed class Node {
    public string Name = "";
    public string FullPath = "";
    public bool IsFile;
    public byte[] Data = [];
    public DateTime Modified = DateTime.UtcNow;
    public Dictionary<string, Node> Children = new(StringComparer.Ordinal);
    public int InodeNumber;
    public int InodeRawOffset;   // byte offset in raw inode stream
    public long DataStart;       // absolute position of first data block
    public uint[] BlockSizes = [];
    public int DirRawOffset;     // byte offset in raw directory stream
    public int DirListingSize;   // file_size field (listing bytes + 3)
  }

  // ──── Main flush ────

  private void Flush() {
    var root = BuildTree();
    var inodeCount = 0;
    AssignInodeNumbers(root, ref inodeCount);

    var all = new List<Node>();
    Collect(root, all);
    all.Sort((a, b) => a.InodeNumber.CompareTo(b.InodeNumber));

    // 1. Write data blocks (after 96-byte superblock placeholder)
    _stream.Position = SquashFsConstants.SuperblockSize;
    WriteDataBlocks(root);
    var dataEnd = _stream.Position;

    // 2. Assign raw inode offsets (tightly packed)
    { var off = 0; foreach (var n in all) { n.InodeRawOffset = off; off += InodeSize(n); } }

    // 3. Compute raw directory offsets + listing sizes
    { var off = 0; ComputeDirLayout(root, ref off); }

    // 4. Iteratively serialize inode + dir raw data until compressed offsets stabilise.
    //    The dir 'start' field needs compressed inode block byte offsets.
    //    The inode dir block_start needs compressed dir block byte offsets.
    //    We iterate until both are stable.
    List<byte[]> inodeMeta, dirMeta;
    byte[] inodeRaw, dirRaw;

    // First iteration: serialize with placeholder dir byte offsets
    inodeRaw = BuildInodeRaw(all, root, null);
    inodeMeta = PackMeta(inodeRaw);
    dirRaw = BuildDirRaw(root, inodeMeta);
    dirMeta = PackMeta(dirRaw);

    // Second iteration: now we have compressed dir block sizes
    for (var iter = 0; iter < 4; iter++) {
      inodeRaw = BuildInodeRaw(all, root, dirMeta);
      inodeMeta = PackMeta(inodeRaw);
      var dirRaw2 = BuildDirRaw(root, inodeMeta);
      var dirMeta2 = PackMeta(dirRaw2);
      var stable = MetaSame(dirMeta, dirMeta2) && MetaSame(inodeMeta, PackMeta(BuildInodeRaw(all, root, dirMeta2)));
      dirRaw = dirRaw2;
      dirMeta = dirMeta2;
      if (stable) break;
    }
    // Final
    inodeRaw = BuildInodeRaw(all, root, dirMeta);
    inodeMeta = PackMeta(inodeRaw);

    // 5. Write tables
    _stream.Position = dataEnd;
    var inodeTableStart = _stream.Position;
    foreach (var b in inodeMeta) _stream.Write(b);

    var dirTableStart = _stream.Position;
    foreach (var b in dirMeta) _stream.Write(b);

    // ID table: metadata block, then lookup pointer
    var idMetaPos = _stream.Position;
    var idData = new byte[4]; // uid=0
    var idMeta = PackMeta(idData);
    foreach (var b in idMeta) _stream.Write(b);
    var idTableStart = _stream.Position; // pointer table starts here
    Span<byte> ptr = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(ptr, (ulong)idMetaPos);
    _stream.Write(ptr);
    var imageEnd = _stream.Position;

    // 6. Root inode reference
    var rootBlock = root.InodeRawOffset / MetaMax;
    var rootIntra = root.InodeRawOffset % MetaMax;
    var rootBlockOff = MetaByteOffset(inodeMeta, rootBlock);
    var rootRef = ((ulong)rootBlockOff << 16) | (ushort)rootIntra;

    var modTime = DateTime.UtcNow;
    var times = AllTimes(root);
    if (times.Count > 0) modTime = times.Max();

    // 7. Write superblock
    WriteSuperblock(
      (uint)inodeCount, modTime, rootRef, (ulong)imageEnd,
      (ulong)idTableStart, (ulong)inodeTableStart, (ulong)dirTableStart);
  }

  // ──── Tree construction ────

  private Node BuildTree() {
    var root = new Node();
    foreach (var d in _directories) Ensure(root, d, false, [], DateTime.UtcNow);
    foreach (var f in _files) Ensure(root, f.Path, true, f.Data, f.Modified);
    return root;
  }

  private static Node Ensure(Node root, string path, bool isFile, byte[] data, DateTime mod) {
    var parts = path.Split('/');
    var cur = root;
    for (var i = 0; i < parts.Length; i++) {
      if (!cur.Children.TryGetValue(parts[i], out var ch)) {
        ch = new Node { Name = parts[i], FullPath = string.Join('/', parts[..(i + 1)]) };
        cur.Children[parts[i]] = ch;
      }
      if (i == parts.Length - 1 && isFile) { ch.IsFile = true; ch.Data = data; ch.Modified = mod; }
      cur = ch;
    }
    return cur;
  }

  private static void AssignInodeNumbers(Node n, ref int c) {
    n.InodeNumber = ++c;
    foreach (var ch in Sorted(n)) AssignInodeNumbers(ch, ref c);
  }

  // ──── Data blocks ────

  private void WriteDataBlocks(Node n) {
    if (n.IsFile) {
      n.DataStart = _stream.Position;
      if (n.Data.Length > 0) {
        var bc = (int)Math.Ceiling((double)n.Data.Length / BlockSize);
        n.BlockSizes = new uint[bc];
        for (var i = 0; i < bc; i++) {
          var off = i * (int)BlockSize;
          var len = Math.Min(n.Data.Length - off, (int)BlockSize);
          var chunk = n.Data.AsSpan(off, len);
          var comp = CompressZlib(chunk);
          if (comp.Length >= len) {
            _stream.Write(chunk);
            n.BlockSizes[i] = (uint)len | SquashFsConstants.BlockUncompressedFlag;
          } else {
            _stream.Write(comp);
            n.BlockSizes[i] = (uint)comp.Length;
          }
        }
      }
    }
    foreach (var ch in Sorted(n)) WriteDataBlocks(ch);
  }

  // ──── Inode sizing / layout ────

  private static int InodeSize(Node n) =>
    n.IsFile ? 32 + n.BlockSizes.Length * 4 : 32;

  private static void ComputeDirLayout(Node n, ref int off) {
    if (!n.IsFile) {
      n.DirRawOffset = off;
      var children = Sorted(n).ToList();
      if (children.Count > 0) {
        var groups = GroupByBlock(children);
        foreach (var g in groups)
          off += 12 + g.Sum(c => 8 + Encoding.UTF8.GetByteCount(c.Name));
      }
      n.DirListingSize = (off - n.DirRawOffset) + 3;
    }
    foreach (var ch in Sorted(n)) ComputeDirLayout(ch, ref off);
  }

  // ──── Inode serialization ────

  private static byte[] BuildInodeRaw(List<Node> all, Node root, List<byte[]>? dirMeta) {
    var last = all[^1];
    var buf = new byte[last.InodeRawOffset + InodeSize(last)];
    foreach (var n in all) {
      var s = buf.AsSpan(n.InodeRawOffset);
      if (n.IsFile) WriteFileInode(s, n);
      else WriteDirInode(s, n, root, dirMeta);
    }
    return buf;
  }

  private static void WriteFileInode(Span<byte> s, Node n) {
    BinaryPrimitives.WriteUInt16LittleEndian(s, SquashFsConstants.InodeBasicFile);
    BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 0x01A4);
    // uid/gid indices = 0
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], ToUnix(n.Modified));
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)n.InodeNumber);
    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)n.DataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(s[20..], SquashFsConstants.NoFragment);
    BinaryPrimitives.WriteUInt32LittleEndian(s[24..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)n.Data.Length);
    for (var i = 0; i < n.BlockSizes.Length; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(32 + i * 4)..], n.BlockSizes[i]);
  }

  private static void WriteDirInode(Span<byte> s, Node n, Node root, List<byte[]>? dirMeta) {
    var par = FindParent(root, n);
    var parIno = par?.InodeNumber ?? n.InodeNumber;
    var nlink = 2 + n.Children.Values.Count(c => !c.IsFile);

    var dirBlock = n.DirRawOffset / MetaMax;
    var dirIntra = n.DirRawOffset % MetaMax;
    uint dirBlockStart;
    if (dirMeta != null)
      dirBlockStart = (uint)MetaByteOffset(dirMeta, dirBlock);
    else
      dirBlockStart = 0;

    BinaryPrimitives.WriteUInt16LittleEndian(s, SquashFsConstants.InodeBasicDir);
    BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 0x01ED);
    // uid/gid indices = 0
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], ToUnix(n.Modified));
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)n.InodeNumber);
    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], dirBlockStart);
    BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)nlink);
    BinaryPrimitives.WriteUInt16LittleEndian(s[24..], (ushort)Math.Min(n.DirListingSize, 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(s[26..], (ushort)dirIntra);
    BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)parIno);
  }

  // ──── Directory serialization ────

  private static byte[] BuildDirRaw(Node root, List<byte[]> inodeMeta) {
    using var ms = new MemoryStream();
    EmitDir(root, ms, inodeMeta);
    return ms.ToArray();
  }

  private static void EmitDir(Node n, MemoryStream ms, List<byte[]> inodeMeta) {
    if (!n.IsFile) {
      var children = Sorted(n).ToList();
      if (children.Count > 0) {
        var groups = GroupByBlock(children);
        Span<byte> hdr = stackalloc byte[12];
        Span<byte> ent = stackalloc byte[8];
        foreach (var g in groups) {
          var first = g[0];
          var startVal = (uint)MetaByteOffset(inodeMeta, first.InodeRawOffset / MetaMax);

          BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)(g.Count - 1));
          BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], startVal);
          BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], (uint)first.InodeNumber);
          ms.Write(hdr);

          foreach (var ch in g) {
            var name = Encoding.UTF8.GetBytes(ch.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(ent, (ushort)(ch.InodeRawOffset % MetaMax));
            BinaryPrimitives.WriteInt16LittleEndian(ent[2..], (short)(ch.InodeNumber - first.InodeNumber));
            BinaryPrimitives.WriteUInt16LittleEndian(ent[4..],
              ch.IsFile ? SquashFsConstants.InodeBasicFile : SquashFsConstants.InodeBasicDir);
            BinaryPrimitives.WriteUInt16LittleEndian(ent[6..], (ushort)(name.Length - 1));
            ms.Write(ent);
            ms.Write(name);
          }
        }
      }
    }
    foreach (var ch in Sorted(n)) EmitDir(ch, ms, inodeMeta);
  }

  // ──── Metadata block packing ────

  private static List<byte[]> PackMeta(byte[] raw) {
    var blocks = new List<byte[]>();
    var pos = 0;
    while (pos < raw.Length) {
      var len = Math.Min(raw.Length - pos, MetaMax);
      var chunk = raw.AsSpan(pos, len);
      var comp = CompressZlib(chunk);
      byte[] blk;
      if (comp.Length >= len) {
        blk = new byte[2 + len];
        BinaryPrimitives.WriteUInt16LittleEndian(blk,
          (ushort)(len | SquashFsConstants.MetadataUncompressedFlag));
        chunk.CopyTo(blk.AsSpan(2));
      } else {
        blk = new byte[2 + comp.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(blk, (ushort)comp.Length);
        comp.CopyTo(blk.AsSpan(2));
      }
      blocks.Add(blk);
      pos += len;
    }
    if (blocks.Count == 0) {
      var blk = new byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(blk, SquashFsConstants.MetadataUncompressedFlag);
      blocks.Add(blk);
    }
    return blocks;
  }

  private static int MetaByteOffset(List<byte[]> meta, int blockIndex) {
    var off = 0;
    for (var i = 0; i < blockIndex && i < meta.Count; i++) off += meta[i].Length;
    return off;
  }

  private static bool MetaSame(List<byte[]> a, List<byte[]> b) {
    if (a.Count != b.Count) return false;
    for (var i = 0; i < a.Count; i++)
      if (a[i].Length != b[i].Length) return false;
    return true;
  }

  // ──── Superblock ────

  private void WriteSuperblock(uint inodeCount, DateTime modTime, ulong rootRef,
    ulong bytesUsed, ulong idTableStart, ulong inodeTableStart, ulong dirTableStart) {
    Span<byte> b = stackalloc byte[SquashFsConstants.SuperblockSize];
    b.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(b, SquashFsConstants.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(b[4..], inodeCount);
    BinaryPrimitives.WriteUInt32LittleEndian(b[8..], ToUnix(modTime));
    BinaryPrimitives.WriteUInt32LittleEndian(b[12..], BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(b[16..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(b[20..], SquashFsConstants.CompressionGzip);
    BinaryPrimitives.WriteUInt16LittleEndian(b[22..], BlockLog);
    BinaryPrimitives.WriteUInt16LittleEndian(b[24..], SquashFsConstants.FlagNoFragments);
    BinaryPrimitives.WriteUInt16LittleEndian(b[26..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(b[28..], 4);
    BinaryPrimitives.WriteUInt16LittleEndian(b[30..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(b[32..], rootRef);
    BinaryPrimitives.WriteUInt64LittleEndian(b[40..], bytesUsed);
    BinaryPrimitives.WriteUInt64LittleEndian(b[48..], idTableStart);
    BinaryPrimitives.WriteUInt64LittleEndian(b[56..], SquashFsConstants.InvalidTable);
    BinaryPrimitives.WriteUInt64LittleEndian(b[64..], inodeTableStart);
    BinaryPrimitives.WriteUInt64LittleEndian(b[72..], dirTableStart);
    BinaryPrimitives.WriteUInt64LittleEndian(b[80..], SquashFsConstants.InvalidTable);
    BinaryPrimitives.WriteUInt64LittleEndian(b[88..], SquashFsConstants.InvalidTable);
    _stream.Position = 0;
    _stream.Write(b);
  }

  // ──── Zlib ────

  private static byte[] CompressZlib(ReadOnlySpan<byte> data) {
    var deflated = DeflateCompressor.Compress(data);
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
    var adler = (b << 16) | a;
    var res = new byte[2 + deflated.Length + 4];
    res[0] = 0x78; res[1] = 0x9C;
    deflated.CopyTo(res.AsSpan(2));
    BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(2 + deflated.Length), adler);
    return res;
  }

  // ──── Helpers ────

  private static string NormalizePath(string p) => p.Replace('\\', '/').Trim('/');

  private void EnsureParentDirs(string path) {
    var parts = path.Split('/');
    for (var i = 1; i < parts.Length; i++)
      _directories.Add(string.Join('/', parts[..i]));
  }

  private static void Collect(Node n, List<Node> list) {
    list.Add(n);
    foreach (var ch in Sorted(n)) Collect(ch, list);
  }

  private static List<DateTime> AllTimes(Node n) {
    var t = new List<DateTime> { n.Modified };
    foreach (var ch in n.Children.Values) t.AddRange(AllTimes(ch));
    return t;
  }

  private static Node? FindParent(Node root, Node target) {
    if (target == root || target.FullPath.Length == 0) return null;
    var pp = target.FullPath.Contains('/')
      ? target.FullPath[..target.FullPath.LastIndexOf('/')]
      : "";
    return FindByPath(root, pp);
  }

  private static Node? FindByPath(Node root, string path) {
    if (path.Length == 0) return root;
    var cur = root;
    foreach (var p in path.Split('/'))
      if (!cur.Children.TryGetValue(p, out cur!)) return null;
    return cur;
  }

  private static IEnumerable<Node> Sorted(Node n) =>
    n.Children.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

  private static List<List<Node>> GroupByBlock(List<Node> children) {
    var gs = new List<List<Node>>();
    List<Node>? cur = null;
    var cb = -1;
    foreach (var ch in children) {
      var blk = ch.InodeRawOffset / MetaMax;
      if (blk != cb) { cur = []; gs.Add(cur); cb = blk; }
      cur!.Add(ch);
    }
    return gs;
  }

  private static uint ToUnix(DateTime dt) {
    if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    var s = (long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    return s < 0 ? 0 : (uint)Math.Min(s, uint.MaxValue);
  }
}
