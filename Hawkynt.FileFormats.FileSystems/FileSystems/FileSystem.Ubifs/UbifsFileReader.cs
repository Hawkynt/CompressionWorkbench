#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileSystem.Ubifs;

/// <summary>
/// Reads a UBIFS image and extracts file contents by linearly scanning the
/// log for inode, data, and dentry nodes, replaying them in sequence-number
/// (sqnum) order, and reassembling each file from its DATA blocks.
/// </summary>
/// <remarks>
/// <para><b>What this reader handles</b>: stored (uncompressed) DATA blocks,
/// zlib-compressed DATA blocks (the UBIFS default), inode size/mode metadata,
/// dentry parent/name/target tuples, recursive path reconstruction from the
/// dentry tree.</para>
/// <para><b>What's NOT covered</b>: (less common
/// — these images return empty per-block payload with a TODO marker in
/// metadata); TNC / LPT / wandering-tree traversal (we use a linear log scan
/// instead, which is correct for normal UBIFS images but may miss versions
/// in pathological recovery scenarios); xattrs; hardlinks beyond first-seen.</para>
/// <para><b>UBIFS key layout</b>: 16 bytes. Lower 32 bits = inode number (LE).
/// Upper 32 bits at offset 4 hold type in the top 3 bits and a per-type value
/// (block index for DATA, dirent-hash for DENT) in the low 29 bits.</para>
/// </remarks>
public sealed class UbifsFileReader {

  // Node types (UBIFS_INO_NODE = 0, UBIFS_DATA_NODE = 1, UBIFS_DENT_NODE = 2).
  private const byte NodeTypeInode = 0;
  private const byte NodeTypeData = 1;
  private const byte NodeTypeDentry = 2;

  // Key types in the top 3 bits of the upper key word.
  private const uint KeyTypeIno = 0;
  private const uint KeyTypeData = 1;
  private const uint KeyTypeDent = 2;

  // Common-header layout: magic(4) crc(4) sqnum(8) len(4) type(1) group(1) pad(2)
  private const int CommonHeaderSize = 24;

  // Inode-node payload layout (after common header):
  //   key[16] creat_sqnum(8) size(8) atime(8) ctime(8) mtime(8)
  //   atime_nsec(4) ctime_nsec(4) mtime_nsec(4) nlink(4) uid(4) gid(4) mode(4)
  //   flags(4) data_len(4) xattr_cnt(4) xattr_size(4) pad(4) xattr_names(4)
  //   compr_type(2) pad2(26)
  // file mode at offset 24 + 16 + 8 + 8 + 3*8 + 3*4 + 3*4 = 24 + 76 = 100
  private const int InodeKeyOffset = CommonHeaderSize;
  private const int InodeSizeOffset = CommonHeaderSize + 16 + 8;
  private const int InodeModeOffset = CommonHeaderSize + 16 + 8 + 8 + 3 * 8 + 3 * 4 + 3 * 4;

  // Data-node payload (after common header):
  //   key[16] size(4) compr_type(2) compr_size(2) data[compr_size]
  private const int DataKeyOffset = CommonHeaderSize;
  private const int DataSizeOffset = CommonHeaderSize + 16;
  private const int DataComprTypeOffset = CommonHeaderSize + 16 + 4;
  private const int DataPayloadOffset = CommonHeaderSize + 16 + 4 + 2 + 2;

  // Dentry-node payload (after common header):
  //   key[16] inum(8) padding(1) type(1) nlen(2) cookie(4) name[nlen + 1]
  //
  // The cookie sits between the name length and the name itself, and leaving it
  // out of the layout put the name four bytes early -- on the cookie, which is
  // zero -- so every name came back as its own first character with four nulls in
  // front of it. B.BIN read as "B".
  private const int DentKeyOffset = CommonHeaderSize;
  private const int DentInumOffset = CommonHeaderSize + 16;
  private const int DentTypeOffset = CommonHeaderSize + 16 + 8 + 1;
  private const int DentNlenOffset = CommonHeaderSize + 16 + 8 + 1 + 1;
  private const int DentNameOffset = CommonHeaderSize + 16 + 8 + 1 + 1 + 2 + 4;

  private const int DefaultBlockSize = 4096; // UBIFS standard logical block

  // UBIFS compression types
  private const ushort ComprNone = 0;
  private const ushort ComprLzo = 1;
  private const ushort ComprZlib = 2;
  private const ushort ComprZstd = 3;

  /// <summary>Mode bits: S_IFMT mask and S_IFDIR value.</summary>
  private const uint ModeFormatMask = 0xF000;
  private const uint ModeDirectory = 0x4000;
  private const uint RootInode = 1;

  /// <summary>
  /// A file or directory entry from the UBIFS image. <see cref="Name"/> is the
  /// full path from the volume root (e.g. <c>etc/passwd</c>).
  /// </summary>
  public sealed record FileEntry(string Name, uint Inode, long Size, bool IsDirectory);

  private readonly byte[] _image;
  private readonly List<FileEntry> _entries = [];

  // inode → (size, mode, sqnum-of-last-write) — highest-sqnum INO node wins.
  private readonly Dictionary<uint, (long Size, uint Mode, ulong Sqnum)> _inodes = new();

  // inode → block index → (raw decompressed data bytes, sqnum-of-last-write).
  // Highest-sqnum DATA node per (inum, blockIdx) wins, matching the kernel's
  // TNC lookup semantic for a log-structured filesystem.
  private readonly Dictionary<uint, SortedDictionary<uint, (byte[] Data, ulong Sqnum)>> _dataBlocks = new();

  // Latest dentry per (parent, name) keyed by highest sqnum. Tombstones
  // (child == 0) are kept too — they suppress earlier-sqnum dentries the
  // same way the kernel UBIFS journal replay does.
  private readonly Dictionary<(uint Parent, string Name), (uint Child, byte DtType, ulong Sqnum)> _dentries = new();

  /// <summary>True if at least one inode and at least one parseable dentry was found.</summary>
  public bool ParseOk { get; private set; }

  /// <summary>List of unsupported compression types encountered (e.g. "lzo", "zstd"). Empty when all data was stored or zlib.</summary>
  public IReadOnlyList<string> UnsupportedCompressors { get; private set; } = [];

  /// <summary>
  /// Initializes a new instance of <see cref="UbifsFileReader"/>.
  /// </summary>
  public UbifsFileReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._image = ms.ToArray();
    this.Parse();
  }

  /// <summary>
  /// Initializes a new instance of <see cref="UbifsFileReader"/>.
  /// </summary>
  public UbifsFileReader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this._image = image;
    this.Parse();
  }

  /// <summary>Lists all parseable files and directories (full paths from root).</summary>
  public IReadOnlyList<FileEntry> Entries => this._entries;

  /// <summary>Extracts the decompressed bytes of the given file entry.</summary>
  public byte[] Extract(FileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (!this._dataBlocks.TryGetValue(entry.Inode, out var blocks) || blocks.Count == 0)
      return [];

    var size = entry.Size > 0 ? (int)entry.Size : (int)blocks.Sum(b => (long)b.Value.Data.Length);
    var result = new byte[size];
    foreach (var (blockIdx, slot) in blocks) {
      var dst = (int)blockIdx * DefaultBlockSize;
      if (dst >= size) break;
      var copy = Math.Min(slot.Data.Length, size - dst);
      if (copy > 0)
        Array.Copy(slot.Data, 0, result, dst, copy);
    }
    return result;
  }

  private void Parse() {
    var span = this._image.AsSpan();
    var unsupportedCompressors = new HashSet<string>();

    for (var off = 0; off + CommonHeaderSize <= span.Length; ++off) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off, 4)) != UbifsScanner.NodeMagic) continue;

      var nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
      var nodeType = span[off + 20];

      if (nodeLen < CommonHeaderSize || nodeLen > span.Length - off) {
        continue;
      }

      switch (nodeType) {
        case NodeTypeInode:
          this.ParseInodeNode(span, off, nodeLen);
          break;
        case NodeTypeData:
          this.ParseDataNode(span, off, nodeLen, unsupportedCompressors);
          break;
        case NodeTypeDentry:
          this.ParseDentryNode(span, off, nodeLen);
          break;
      }

      // Advance past the node to reduce redundant magic scans; the linear
      // scan accommodates UBIFS nodes that don't sit on LEB boundaries.
      if (nodeLen >= CommonHeaderSize)
        off += nodeLen - 1; // -1 because the outer loop also increments
    }

    this.UnsupportedCompressors = [.. unsupportedCompressors];
    this.BuildEntries();
    this.ParseOk = this._entries.Count > 0;
  }

  private void ParseInodeNode(ReadOnlySpan<byte> span, int off, int nodeLen) {
    if (off + InodeModeOffset + 4 > span.Length) return;
    var sqnum = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + 8, 8));
    var inum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + InodeKeyOffset, 4));
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + InodeSizeOffset, 8));
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + InodeModeOffset, 4));
    // Highest-sqnum INO node wins (matches kernel UBIFS TNC lookup after
    // journal replay).
    if (this._inodes.TryGetValue(inum, out var prev) && prev.Sqnum > sqnum) return;
    this._inodes[inum] = (size, mode, sqnum);
    _ = nodeLen;
  }

  private void ParseDataNode(ReadOnlySpan<byte> span, int off, int nodeLen, HashSet<string> unsupported) {
    if (off + DataPayloadOffset > span.Length) return;
    var sqnum = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + 8, 8));
    var inum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + DataKeyOffset, 4));
    var keyHi = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + DataKeyOffset + 4, 4));
    var blockIdx = keyHi & 0x1FFFFFFFu; // low 29 bits = block index for DATA keys
    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + DataSizeOffset, 4));
    var comprType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + DataComprTypeOffset, 2));
    var payloadLen = nodeLen - DataPayloadOffset;
    if (payloadLen < 0 || off + DataPayloadOffset + payloadLen > span.Length) return;

    byte[] decompressed;
    switch (comprType) {
      case ComprNone:
        decompressed = span.Slice(off + DataPayloadOffset, payloadLen).ToArray();
        break;
      case ComprZlib:
        decompressed = TryInflateZlib(span.Slice(off + DataPayloadOffset, payloadLen), (int)uncompressedSize);
        if (decompressed.Length == 0 && uncompressedSize > 0) return; // skip blocks we couldn't inflate
        break;
      case ComprLzo:
        // mkfs.ubifs reaches for LZO by default, so declining it meant declining
        // most of the data on most volumes -- and the block was skipped, which
        // reports the file as short rather than as unread.
        decompressed = Compression.Core.Dictionary.Lzo.Lzo1xDecompressor.Decompress(
          span.Slice(off + DataPayloadOffset, payloadLen), (int)uncompressedSize);
        break;
      case ComprZstd: {
        using var input = new MemoryStream(
          span.Slice(off + DataPayloadOffset, payloadLen).ToArray());
        using var zstd = new FileFormat.Zstd.ZstdStream(
          input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
        using var plain = new MemoryStream();
        zstd.CopyTo(plain);
        decompressed = plain.ToArray();
        break;
      }
      default:
        unsupported.Add($"compr_{comprType}");
        return;
    }

    if (!this._dataBlocks.TryGetValue(inum, out var blocks)) {
      blocks = new SortedDictionary<uint, (byte[], ulong)>();
      this._dataBlocks[inum] = blocks;
    }
    // Highest-sqnum DATA node per (inum, block) wins. Earlier-sqnum nodes for
    // the same block stay on disk (log-structured) but are masked from reads.
    if (blocks.TryGetValue(blockIdx, out var prev) && prev.Sqnum > sqnum) return;
    blocks[blockIdx] = (decompressed, sqnum);
  }

  private static byte[] TryInflateZlib(ReadOnlySpan<byte> compressed, int expectedSize) {
    if (compressed.Length < 2) return [];
    try {
      using var input = new MemoryStream(compressed.ToArray());
      using var zls = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
      using var output = new MemoryStream(expectedSize > 0 ? expectedSize : DefaultBlockSize);
      zls.CopyTo(output);
      return output.ToArray();
    } catch {
      return [];
    }
  }

  private void ParseDentryNode(ReadOnlySpan<byte> span, int off, int nodeLen) {
    if (off + DentNameOffset > span.Length) return;
    var sqnum = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + 8, 8));
    var parent = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + DentKeyOffset, 4));
    var child = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + DentInumOffset, 4));
    var dtType = span[off + DentTypeOffset];
    var nlen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + DentNlenOffset, 2));
    if (nlen == 0 || nlen > 255 || off + DentNameOffset + nlen > span.Length) return;
    var name = Encoding.UTF8.GetString(span.Slice(off + DentNameOffset, nlen));
    if (name.Length == 0 || name == "." || name == "..") return;
    // Highest-sqnum DENT per (parent, name) wins — tombstones (child == 0)
    // included so a later Remove suppresses the earlier non-zero dentry.
    var key = (parent, name);
    if (this._dentries.TryGetValue(key, out var prev) && prev.Sqnum > sqnum) return;
    this._dentries[key] = (child, dtType, sqnum);
    _ = nodeLen;
  }

  private void BuildEntries() {
    // Live (non-tombstone) dentries keyed by child inode — for path reconstruction.
    var byChild = new Dictionary<uint, (uint Parent, string Name)>();
    foreach (var ((parent, name), entry) in this._dentries) {
      if (entry.Child == 0) continue; // tombstone
      byChild[entry.Child] = (parent, name);
    }

    foreach (var ((parent, name), entry) in this._dentries) {
      if (entry.Child == 0) continue; // tombstone — drop from listing
      var child = entry.Child;
      var isDir = entry.DtType == 4 // DT_DIR
        || (this._inodes.TryGetValue(child, out var ino) && (ino.Mode & ModeFormatMask) == ModeDirectory);
      var size = this._inodes.TryGetValue(child, out var inodeInfo) ? inodeInfo.Size : 0;
      var fullPath = BuildPath(child, byChild);
      if (fullPath.Length == 0) fullPath = name;
      this._entries.Add(new FileEntry(fullPath, child, size, isDir));
    }
  }

  private static string BuildPath(uint inode, Dictionary<uint, (uint Parent, string Name)> byChild) {
    var segments = new List<string>();
    var current = inode;
    var guard = 0;
    while (current != RootInode && current != 0 && guard++ < 256) {
      if (!byChild.TryGetValue(current, out var info)) break;
      segments.Add(info.Name);
      if (info.Parent == current) break; // self-loop guard
      current = info.Parent;
    }
    segments.Reverse();
    return string.Join('/', segments);
  }
}
