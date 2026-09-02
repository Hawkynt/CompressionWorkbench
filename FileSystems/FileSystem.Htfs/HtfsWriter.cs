#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Htfs;

/// <summary>
/// Minimal but spec-keyed writer for SCO HTFS (High Throughput File System).
/// Emits a real <see cref="HtfsSuperblock.HtfsMagic"/>-tagged superblock at
/// byte offset 512 (sector 1) followed by an inode array (one block per 4
/// inodes) and per-file single-extent layout in subsequent blocks.
///
/// <para><b>Scope.</b> S5-derived HTFS uses 512-byte blocks (BlockSize knob
/// can override), block-based inode array immediately after the SB, and
/// directory bodies storing 16-byte name + inode entries. The on-disk magic +
/// s_isize/s_fsize fields are spec-compliant so <see cref="HtfsSuperblock.TryParse"/>
/// recognises the image. Real SCO HTFS additionally maintains a journal, a
/// duplicate superblock at sector S, and extent btrees — all out of scope
/// for the WORM writer.</para>
/// </summary>
public sealed class HtfsWriter {
  internal const int DefaultBlockSize = 512;
  internal const int SuperblockOffset = 512;        // sector 1
  internal const int InodeSize = 64;
  internal const int MaxInodes = 256;
  internal const int MaxNameLen = 14;               // S5-style directory entry name

  // S5 mode bits (matching SCO htfs_fs.h)
  private const ushort ModeDir = 0x4000 | 0x1ED;    // 0o755 dir
  private const ushort ModeFile = 0x8000 | 0x1A4;   // 0o644 file

  private readonly List<(string Path, FilePayload Payload)> _files = [];
  private string _volumeLabel = "WORM";
  private int _blockSize = DefaultBlockSize;

  /// <summary>Sets the volume label written into the SB tail area (truncated to 16 chars).</summary>
  public void SetVolumeLabel(string? label) {
    if (!string.IsNullOrWhiteSpace(label)) _volumeLabel = label;
  }

  /// <summary>Sets the block size. Valid: 512, 1024, 2048.</summary>
  public void SetBlockSize(int blockSize) {
    if (blockSize is not (512 or 1024 or 2048))
      throw new ArgumentOutOfRangeException(nameof(blockSize), "HTFS block size must be 512/1024/2048.");
    _blockSize = blockSize;
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
      throw new InvalidOperationException(
        $"HTFS writer caps at {MaxInodes} inodes; tree has {tree.Nodes.Count}.");

    var inodesPerBlock = _blockSize / InodeSize;
    var inodeBlocks = Math.Max(1, (tree.Nodes.Count + inodesPerBlock - 1) / inodesPerBlock);

    // Layout: SB block + inode blocks + data blocks (one per dir + N per file).
    var sbBlock = SuperblockOffset / _blockSize > 0 ? SuperblockOffset / _blockSize : 1;
    // For 512 B blocks SB is block 1; for 1024/2048 it's block 0 plus offset.
    var inodeStart = sbBlock + 1;
    var dataStart = inodeStart + inodeBlocks;
    var nextData = dataStart;
    var blocksPerNode = new int[tree.Nodes.Count];
    var firstBlockPerNode = new int[tree.Nodes.Count];
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var n = tree.Nodes[i];
      int blks = n.IsDirectory ? 1
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

    // ── Superblock @ sector 1 (LE per htfs_fs.h) ────────────────────────────
    var sb = image.At(SuperblockOffset, _blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x00..], HtfsSuperblock.HtfsMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x04..], (uint)inodeBlocks);   // s_isize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x08..], (uint)totalBlocks);   // s_fsize
    BinaryPrimitives.WriteUInt16LittleEndian(sb[0x0C..], 0);                   // s_nfree
    BinaryPrimitives.WriteUInt16LittleEndian(sb[0xD6..], 0);                   // s_ninode
    // s_label at +0x1E0 (loose surface; HTFS uses a 16-char label slot)
    var labelOff = 0x1E0;
    if (labelOff + 16 <= sb.Length) {
      var lb = Encoding.ASCII.GetBytes(_volumeLabel);
      Array.Resize(ref lb, 16);
      lb.CopyTo(sb[labelOff..]);
    }

    // ── Inode array ──────────────────────────────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      var ino = i + 2;                            // 1 reserved, 2 = root
      var blockOff = (ino - 2) / inodesPerBlock;
      var slotOff = (ino - 2) % inodesPerBlock;
      var ip = image.At(((long)inodeStart + blockOff) * _blockSize + slotOff * InodeSize, InodeSize);

      // di_mode(2), di_nlink(2), di_uid(2), di_gid(2), di_size(4),
      // di_atime(4), di_mtime(4), di_ctime(4), di_first_blk(4), di_block_count(4),
      // padding to 64 bytes.
      BinaryPrimitives.WriteUInt16LittleEndian(ip[0..], node.IsDirectory ? ModeDir : ModeFile);
      BinaryPrimitives.WriteUInt16LittleEndian(ip[2..], (ushort)(node.IsDirectory ? 2 : 1));
      BinaryPrimitives.WriteUInt16LittleEndian(ip[4..], 0);
      BinaryPrimitives.WriteUInt16LittleEndian(ip[6..], 0);
      var size = node.IsDirectory ? ComputeDirSize(node) : node.Payload.Size;
      BinaryPrimitives.WriteUInt32LittleEndian(ip[8..], (uint)size);
      var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], now);
      BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], now);
      BinaryPrimitives.WriteUInt32LittleEndian(ip[20..], now);
      BinaryPrimitives.WriteUInt32LittleEndian(ip[24..], (uint)firstBlockPerNode[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(ip[28..], (uint)blocksPerNode[i]);
    }

    // ── Data: directory bodies + file content ────────────────────────────────
    for (var i = 0; i < tree.Nodes.Count; i++) {
      var node = tree.Nodes[i];
      if (blocksPerNode[i] == 0) continue;
      // Block index times block size in ints overflows a few gigabytes in,
      // and the payload was then placed at a negative offset.
      var start = (long)firstBlockPerNode[i] * _blockSize;
      if (node.IsDirectory) {
        var blk = image.At(start, _blockSize);
        // dir entry = 2-byte LE inode + 14-byte name (S5 d_ino + d_name).
        var off = 0;
        WriteDirEntry(blk, ref off, (ushort)(i + 2), ".");
        WriteDirEntry(blk, ref off, (ushort)node.ParentInode, "..");
        foreach (var (childName, childInode) in node.Children)
          WriteDirEntry(blk, ref off, (ushort)childInode, childName);
      } else {
        payloads.Add(start, node.Payload);
      }
    }
    return image;
  }


  private static void WriteDirEntry(Span<byte> blk, ref int off, ushort inode, string name) {
    if (off + 16 > blk.Length)
      throw new InvalidOperationException("HTFS writer: directory block overflow (single-block dirs only).");
    var nameBytes = Encoding.ASCII.GetBytes(name);
    if (nameBytes.Length > MaxNameLen) Array.Resize(ref nameBytes, MaxNameLen);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[off..], inode);
    // Clear name slot then copy.
    for (var i = 0; i < MaxNameLen; i++) blk[off + 2 + i] = 0;
    nameBytes.CopyTo(blk[(off + 2)..]);
    off += 16;
  }

  private static int ComputeDirSize(TreeNode node) {
    return (2 + node.Children.Count) * 16;
  }

  internal sealed class TreeNode {
    public required bool IsDirectory;
    public required string Name;
    public required int ParentInode;
    public FilePayload Payload;
    public readonly List<(string Name, int Inode)> Children = [];
  }

  internal sealed class TreeModel {
    public required List<TreeNode> Nodes;
  }

  private TreeModel BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "", ParentInode = 2 };
    var nodes = new List<TreeNode> { root };
    var byPath = new Dictionary<string, TreeNode>(StringComparer.Ordinal) { [""] = root };
    foreach (var (path, payload) in _files) {
      var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length == 0) continue;
      var parent = root;
      var parentInode = 2;
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
}
