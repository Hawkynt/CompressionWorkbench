#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Gfs1;

/// <summary>
/// Minimal but spec-keyed writer for Sistina GFS (the pre-GFS2 distributed
/// filesystem). Emits a real <see cref="Gfs1Superblock.MhMagicConst"/>-tagged
/// metaheader superblock at byte offset 65536 with the GFS-specific
/// <c>sb_multihost_format = 1900</c>, followed by a packed inode + data area
/// in subsequent 4 KB blocks. Directories are stored as 16-byte entry blocks
/// for round-trip simplicity (kernel GFS1 used full ondir entries — out of
/// WORM scope).
///
/// <para><b>Scope.</b> Real GFS1 maintains a journal per cluster node, a
/// distributed lock table (DLM), a resource group bitmap chain, and a
/// hashed-leaf-block directory layout. The WORM writer skips the journal
/// area + lock proto fields beyond the spec-anchored handle in the SB, and
/// emits a non-hashed single-block directory body.</para>
/// </summary>
public sealed class Gfs1Writer {
  internal const int BlockSize = 4096;
  internal const int SuperblockOffset = (int)Gfs1Superblock.SuperblockOffset; // 65536
  internal const int InodeSize = 256;                 // GFS1 dinode is 256 B
  internal const int InodesPerBlock = BlockSize / InodeSize; // 16
  internal const int MaxInodes = 1024;

  // GFS file-type bits (S_IFMT-style)
  private const ushort ModeDir = 0x4000 | 0x1ED;
  private const ushort ModeFile = 0x8000 | 0x1A4;

  // Sistina GFS multihost format = 1900; GFS2 = 1901.
  private const uint GfsMultihostFormat = 1900u;
  private const uint GfsFsFormat = 1309u;

  // Reserved magic for directory body (used by reader to spot writer-emitted dirs).
  private const ushort DirBlockMagic = 0xDEAD;

  private readonly List<(string Path, FilePayload Payload)> _files = [];
  private string _volumeLabel = "WORM";
  private int _journalCount = 1;
  private string _lockProto = "lock_nolock";
  private string _lockTable = "WORM:gfs1";

    /// <summary>
  /// Sets the volume label.
  /// </summary>
public void SetVolumeLabel(string? s) { if (!string.IsNullOrWhiteSpace(s)) _volumeLabel = s; }
    /// <summary>
  /// Sets the journal count.
  /// </summary>
public void SetJournalCount(int n) => _journalCount = Math.Clamp(n, 1, 32);
    /// <summary>
  /// Sets the lock proto.
  /// </summary>
public void SetLockProto(string? s) { if (!string.IsNullOrWhiteSpace(s)) _lockProto = s; }
    /// <summary>
  /// Sets the lock table.
  /// </summary>
public void SetLockTable(string? s) { if (!string.IsNullOrWhiteSpace(s)) _lockTable = s; }

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
  /// it before a byte is read.
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
  /// non-seekable target has to materialise the volume.
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
      throw new InvalidOperationException($"GFS1 writer caps at {MaxInodes} inodes; tree has {tree.Nodes.Count}.");

    // Layout: blocks 0..15 = boot (64 KB); block 16 = superblock; blocks 17.. =
    // inode table; then data blocks.
    var sbBlock = SuperblockOffset / BlockSize; // 16
    var inodeBlocks = Math.Max(1, (tree.Nodes.Count + InodesPerBlock - 1) / InodesPerBlock);
    var inodeStart = sbBlock + 1;
    var dataStart = inodeStart + inodeBlocks;
    var nextData = dataStart;
    var blocksPerNode = new int[tree.Nodes.Count];
    var firstBlockPerNode = new int[tree.Nodes.Count];
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var n = tree.Nodes[i];
      var blks = n.IsDirectory ? 1
        : n.Payload.Size == 0 ? 0 : (int)((n.Payload.Size + BlockSize - 1) / BlockSize);
      firstBlockPerNode[i] = blks > 0 ? nextData : 0;
      blocksPerNode[i] = blks;
      nextData += blks;
    }
    var totalBlocks = nextData;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BlockSize, (long)totalBlocks * BlockSize);
    payloads = new DeferredPayloads();

    // ── Superblock @ block 16 ────────────────────────────────────────────────
    // GFS metaheader (BE): mh_magic(4) + mh_type(4) + mh_generation(8) +
    // mh_format(4) + mh_incarn(4) — first 24 bytes.
    var sb = image.At(SuperblockOffset, BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(sb[0..], Gfs1Superblock.MhMagicConst); // mh_magic
    BinaryPrimitives.WriteUInt32BigEndian(sb[4..], 1);                            // mh_type = GFS_METATYPE_SB
    BinaryPrimitives.WriteUInt64BigEndian(sb[8..], 1);                            // mh_generation
    BinaryPrimitives.WriteUInt32BigEndian(sb[16..], 100);                         // mh_format
    BinaryPrimitives.WriteUInt32BigEndian(sb[20..], 0);                           // mh_incarn

    BinaryPrimitives.WriteUInt32BigEndian(sb[0x18..], GfsFsFormat);              // sb_fs_format
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x1C..], GfsMultihostFormat);       // sb_multihost_format

    // Mirror magic at +0x40 to satisfy the descriptor's MagicSignature anchor.
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x40..], Gfs1Superblock.MhMagicConst);

    BinaryPrimitives.WriteUInt32BigEndian(sb[0x44..], BlockSize);                // sb_bsize
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x48..], (uint)inodeBlocks);        // sb_isize_blocks
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x4C..], (uint)_journalCount);      // sb_journals
    BinaryPrimitives.WriteUInt32BigEndian(sb[0x50..], (uint)totalBlocks);        // sb_size

    // sb_lockproto @ +0x60 (64 chars), sb_locktable @ +0xA0 (64 chars).
    var lp = Encoding.ASCII.GetBytes(_lockProto);
    Array.Resize(ref lp, 64);
    lp.CopyTo(sb[0x60..]);
    var lt = Encoding.ASCII.GetBytes(_lockTable);
    Array.Resize(ref lt, 64);
    lt.CopyTo(sb[0xA0..]);
    // Volume label at +0xE0 (16 chars).
    var vl = Encoding.ASCII.GetBytes(_volumeLabel);
    Array.Resize(ref vl, 16);
    vl.CopyTo(sb[0xE0..]);

    // ── Inode table ──────────────────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      var ino = i + 2; // 1 reserved, 2 = root
      var blockOff = (ino - 2) / InodesPerBlock;
      var slotOff = (ino - 2) % InodesPerBlock;
      var ip = image.At(((long)inodeStart + blockOff) * BlockSize + slotOff * InodeSize, InodeSize);

      // GFS1 dinode (BE): mh(24) + di_num.no_addr(8) + di_num.no_formal_ino(8) +
      // di_mode(4) + di_uid(4) + di_gid(4) + di_nlink(4) + di_size(8) +
      // di_blocks(8) + di_atime(8) + di_mtime(8) + di_ctime(8) + di_major(4) +
      // di_minor(4) + di_first_extent_lo(4) + di_first_extent_hi(4) ...
      BinaryPrimitives.WriteUInt32BigEndian(ip[0..], Gfs1Superblock.MhMagicConst);
      BinaryPrimitives.WriteUInt32BigEndian(ip[4..], 4); // mh_type = GFS_METATYPE_DI
      BinaryPrimitives.WriteUInt64BigEndian(ip[24..], (ulong)(firstBlockPerNode[i])); // no_addr
      BinaryPrimitives.WriteUInt64BigEndian(ip[32..], (ulong)ino);                    // no_formal_ino
      BinaryPrimitives.WriteUInt32BigEndian(ip[40..], node.IsDirectory ? ModeDir : ModeFile);
      BinaryPrimitives.WriteUInt32BigEndian(ip[44..], 0); // uid
      BinaryPrimitives.WriteUInt32BigEndian(ip[48..], 0); // gid
      BinaryPrimitives.WriteUInt32BigEndian(ip[52..], (uint)(node.IsDirectory ? 2 : 1)); // nlink
      var size = node.IsDirectory ? ComputeDirSize(node) : (ulong)node.Payload.Size;
      BinaryPrimitives.WriteUInt64BigEndian(ip[56..], size);
      BinaryPrimitives.WriteUInt64BigEndian(ip[64..], (ulong)blocksPerNode[i]);
      var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      BinaryPrimitives.WriteUInt64BigEndian(ip[72..], now);
      BinaryPrimitives.WriteUInt64BigEndian(ip[80..], now);
      BinaryPrimitives.WriteUInt64BigEndian(ip[88..], now);
    }

    // ── Data ────────────────────────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      if (blocksPerNode[i] == 0) continue;
      // Block index times block size in ints overflows a few gigabytes in,
      // and the payload was then placed at a negative offset.
      var start = (long)firstBlockPerNode[i] * BlockSize;
      if (node.IsDirectory) {
        var blk = image.At(start, BlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(blk[0..], DirBlockMagic);
        BinaryPrimitives.WriteUInt16BigEndian(blk[2..], (ushort)(node.Children.Count + 2));
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
      throw new InvalidOperationException("GFS1 writer: directory block overflow (single-block dirs only).");
    BinaryPrimitives.WriteUInt32BigEndian(blk[off..], inode);
    blk[off + 4] = (byte)nb.Length;
    nb.CopyTo(blk[(off + 5)..]);
    off += slot;
  }

  private static ulong ComputeDirSize(TreeNode node) {
    var size = (ulong)(4 + 1 + 1 + 4 + 1 + 2); // "." and ".."
    foreach (var (n, _) in node.Children) size += (ulong)(4 + 1 + Encoding.UTF8.GetByteCount(n));
    return size;
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

  internal static int DirBlockMagicConst => DirBlockMagic;
}
