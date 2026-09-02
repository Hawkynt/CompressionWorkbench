#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Jfs1;

/// <summary>
/// Minimal but spec-keyed writer for OS/2 JFS1 (the original IBM JFS that
/// shipped with OS/2 Warp Server). Emits a real "JFS1"-magic superblock at
/// offset 0 with <c>s_version = 1</c> (distinguishing from Linux JFS2 which
/// uses <c>s_version &gt;= 2</c>) followed by an inode table and per-file
/// single-extent data blocks.
///
/// <para><b>Scope.</b> OS/2 JFS1's on-disk format is documented in the IBM
/// JFS for OS/2 Technical Reference. The writer covers: superblock with
/// configurable block + aggregate-block size, inode array (256-byte
/// dinodes), single-block directory bodies with (inode + nlen + name)
/// dirents, single-extent file bodies. The dmap/IAG bitmap chain,
/// secondary AIT/AIM trees, dtree B+ index pages, and the inline data
/// extents larger than one block are out of WORM scope.</para>
/// </summary>
public sealed class Jfs1Writer {
  internal const int DefaultBlockSize = 4096;
  internal const int InodeSize = 256;
  internal const int MaxInodes = 256;
  // S5/JFS-style mode bits
  private const uint ModeDir = 0x4000u | 0x1EDu;
  private const uint ModeFile = 0x8000u | 0x1A4u;
  // Reader magic for our writer-emitted dir blocks.
  private const ushort DirBlockMagic = 0xD1F1;

  private readonly List<(string Path, FilePayload Payload)> _files = [];
  private string _volumeLabel = "WORM";
  private int _blockSize = DefaultBlockSize;
  private int _aggregateBlockSize = DefaultBlockSize;

  /// <summary>
  /// Sets the volume label.
  /// </summary>
  public void SetVolumeLabel(string? s) { if (!string.IsNullOrWhiteSpace(s)) _volumeLabel = s; }
  /// <summary>
  /// Sets the block size.
  /// </summary>
  public void SetBlockSize(int bs) {
    if (bs is not (1024 or 2048 or 4096))
      throw new ArgumentOutOfRangeException(nameof(bs), "JFS1 block size must be 1024/2048/4096.");
    _blockSize = bs;
  }
  /// <summary>
  /// Sets the aggregate block size.
  /// </summary>
  public void SetAggregateBlockSize(int abs) {
    if (abs is not (1024 or 2048 or 4096))
      throw new ArgumentOutOfRangeException(nameof(abs), "JFS1 aggregate block size must be 1024/2048/4096.");
    _aggregateBlockSize = abs;
  }

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var n = name.Replace('\\', '/').Trim('/');
    if (n.Length == 0) return;
    _files.Add((n, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read, so a payload past what a byte[] can hold is placed
  /// like any other.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    var n = name.Replace('\\', '/').Trim('/');
    if (n.Length == 0) return;
    _files.Add((n, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>Materialises the whole volume.</summary>
  public byte[] Build() {
    var image = this.BuildCore(out var payloads);
    return payloads.Materialise(image);
  }

  /// <summary>
  /// Writes the volume into <paramref name="output" />: the blocks the filesystem
  /// populates, then each file's bytes at the offset it was allocated. Only a
  /// non-seekable target has to materialise the volume, so a seekable one is
  /// bounded by the disk rather than by what a byte[] can address.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build();
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = this.BuildCore(out var payloads);
    image.WriteTo(output);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  private SparseBlockImage BuildCore(out DeferredPayloads payloads) {
    var tree = BuildTree();
    if (tree.Nodes.Count > MaxInodes)
      throw new InvalidOperationException($"JFS1 writer caps at {MaxInodes} inodes; tree has {tree.Nodes.Count}.");

    var inodesPerBlock = _blockSize / InodeSize;
    var inodeBlocks = Math.Max(1, (tree.Nodes.Count + inodesPerBlock - 1) / inodesPerBlock);
    // Layout: block 0 = SB (rest of block reserved); block 1.. = inode table;
    // then data.
    const int SbBlock = 0;
    var inodeStart = SbBlock + 1;
    var dataStart = inodeStart + inodeBlocks;
    var nextData = dataStart;
    var blocksPerNode = new int[tree.Nodes.Count];
    var firstBlockPerNode = new int[tree.Nodes.Count];
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var n = tree.Nodes[i];
      var blks = n.IsDirectory ? 1
        : n.Payload.Size == 0 ? 0 : (int)((n.Payload.Size + _blockSize - 1) / _blockSize);
      firstBlockPerNode[i] = blks > 0 ? nextData : 0;
      blocksPerNode[i] = blks;
      nextData += blks;
    }
    var totalBlocks = nextData;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(_blockSize, (long)totalBlocks * _blockSize);
    payloads = new DeferredPayloads();

    // ── Superblock @ block 0 (JFS1 LE per IBM OS/2 spec) ────────────────────
    var sb = image.At((long)SbBlock * _blockSize, _blockSize);
    Jfs1Superblock.Jfs1Magic.CopyTo(sb);                                       // "JFS1"
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], 1u);                     // s_version = 1 (OS/2)
    BinaryPrimitives.WriteUInt64LittleEndian(sb[8..], (ulong)totalBlocks);     // s_size in s_bsize blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], (uint)_blockSize);    // s_bsize
    BinaryPrimitives.WriteUInt16LittleEndian(sb[0x14..], (ushort)Log2(_blockSize)); // s_l2bsize
    BinaryPrimitives.WriteUInt16LittleEndian(sb[0x16..], (ushort)Log2(_aggregateBlockSize)); // s_l2agsize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x18..], (uint)inodeBlocks);   // s_inostamp
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x1C..], 0);                   // s_state = clean
    // Volume label at +0x90 (16 ASCII bytes); IBM JFS1 stores it in s_label.
    var vl = Encoding.ASCII.GetBytes(_volumeLabel);
    Array.Resize(ref vl, 16);
    vl.CopyTo(sb[0x90..]);

    // ── Inode array ──────────────────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      var ino = i + 2;
      var blockOff = (ino - 2) / inodesPerBlock;
      var slotOff = (ino - 2) % inodesPerBlock;
      var ip = image.At(((long)inodeStart + blockOff) * _blockSize + slotOff * InodeSize, InodeSize);

      // JFS1 dinode (LE): di_inostamp(4) di_fileset(4) di_number(4) di_gen(4)
      // di_ixpxd(8) di_size(8) di_nblocks(8) di_nlink(4) di_uid(4) di_gid(4)
      // di_mode(4) di_atime(8) di_mtime(8) di_ctime(8) ...
      BinaryPrimitives.WriteUInt32LittleEndian(ip[0..], 1);                  // di_inostamp
      BinaryPrimitives.WriteUInt32LittleEndian(ip[4..], 0);                  // di_fileset
      BinaryPrimitives.WriteUInt32LittleEndian(ip[8..], (uint)ino);          // di_number
      BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], 1);                 // di_gen
      // di_ixpxd is an extent pointer to the first data block.
      BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], (uint)firstBlockPerNode[i]); // address
      BinaryPrimitives.WriteUInt32LittleEndian(ip[20..], (uint)blocksPerNode[i]);     // length
      var size = node.IsDirectory ? (ulong)ComputeDirSize(node) : (ulong)node.Payload.Size;
      BinaryPrimitives.WriteUInt64LittleEndian(ip[24..], size);              // di_size
      BinaryPrimitives.WriteUInt64LittleEndian(ip[32..], (ulong)blocksPerNode[i]); // di_nblocks
      BinaryPrimitives.WriteUInt32LittleEndian(ip[40..], (uint)(node.IsDirectory ? 2 : 1)); // di_nlink
      BinaryPrimitives.WriteUInt32LittleEndian(ip[44..], 0); // uid
      BinaryPrimitives.WriteUInt32LittleEndian(ip[48..], 0); // gid
      BinaryPrimitives.WriteUInt32LittleEndian(ip[52..], node.IsDirectory ? ModeDir : ModeFile);
      var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      BinaryPrimitives.WriteUInt64LittleEndian(ip[56..], now);
      BinaryPrimitives.WriteUInt64LittleEndian(ip[64..], now);
      BinaryPrimitives.WriteUInt64LittleEndian(ip[72..], now);
    }

    // ── Data ────────────────────────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      if (blocksPerNode[i] == 0) continue;
      var start = firstBlockPerNode[i] * _blockSize;
      if (node.IsDirectory) {
        var blk = image.At(start, _blockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], DirBlockMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)(node.Children.Count + 2));
        var off = 4;
        WriteDirEntry(blk, ref off, (uint)(i + 2), ".");
        WriteDirEntry(blk, ref off, (uint)node.ParentInode, "..");
        foreach (var (childName, childInode) in node.Children)
          WriteDirEntry(blk, ref off, (uint)childInode, childName);
      } else {
        payloads.Add(start, node.Payload);
      }
    }
    return image;
  }


  private static void WriteDirEntry(Span<byte> blk, ref int off, uint inode, string name) {
    var nb = Encoding.UTF8.GetBytes(name);
    if (nb.Length > 250) Array.Resize(ref nb, 250);
    var slot = 4 + 1 + nb.Length;
    if (off + slot > blk.Length)
      throw new InvalidOperationException("JFS1 writer: directory block overflow (single-block dirs only).");
    BinaryPrimitives.WriteUInt32LittleEndian(blk[off..], inode);
    blk[off + 4] = (byte)nb.Length;
    nb.CopyTo(blk[(off + 5)..]);
    off += slot;
  }

  private static int ComputeDirSize(TreeNode node) {
    var size = 4 + 1 + 1 + 4 + 1 + 2; // "." and ".."
    foreach (var (n, _) in node.Children) size += 4 + 1 + Encoding.UTF8.GetByteCount(n);
    return size;
  }

  private static int Log2(int v) {
    var n = 0;
    while ((1 << n) < v) n++;
    return n;
  }

  internal sealed class TreeNode {
    public required bool IsDirectory;
    public required string Name;
    public required int ParentInode;
    public FilePayload Payload;
    public readonly List<(string Name, int Inode)> Children = [];
  }

  internal sealed class TreeModel { public required List<TreeNode> Nodes; }

  private TreeModel BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "", ParentInode = 2 };
    var nodes = new List<TreeNode> { root };
    var byPath = new Dictionary<string, TreeNode>(StringComparer.Ordinal) { [""] = root };
    foreach (var (path, payload) in _files) {
      var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length == 0) continue;
      var parent = root; var parentInode = 2;
      var acc = "";
      for (var s = 0; s < segs.Length - 1; s++) {
        acc = acc.Length == 0 ? segs[s] : $"{acc}/{segs[s]}";
        if (!byPath.TryGetValue(acc, out var dir)) {
          dir = new TreeNode { IsDirectory = true, Name = segs[s], ParentInode = parentInode };
          nodes.Add(dir);
          parent.Children.Add((segs[s], nodes.Count + 1));
          byPath[acc] = dir;
        }
        parentInode = nodes.IndexOf(dir) + 2;
        parent = dir;
      }
      var file = new TreeNode { IsDirectory = false, Name = segs[^1], ParentInode = parentInode, Payload = payload };
      nodes.Add(file);
      parent.Children.Add((segs[^1], nodes.Count + 1));
    }
    return new TreeModel { Nodes = nodes };
  }

  internal int BlockSize => _blockSize;
  internal static int DirBlockMagicConst => DirBlockMagic;
}
