using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileSystem.CramFs;

/// <summary>
/// Writes a CramFS (Compressed ROM Filesystem) image.
/// Entries are collected via <see cref="AddFile"/>, <see cref="AddDirectory"/>, and
/// <see cref="AddSymlink"/>, and the entire image is serialised on <see cref="Dispose"/>.
/// </summary>
public sealed class CramFsWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PendingEntry> _pending = [];
  private bool _disposed;

  private const ushort DirMode = 0x41FF;     // S_IFDIR | 0777
  private const ushort FileMode = 0x81A4;    // S_IFREG | 0644
  private const ushort SymlinkMode = 0xA1FF; // S_IFLNK | 0777

  /// <summary>
  /// Initialises a new <see cref="CramFsWriter"/> that will write to the given stream.
  /// </summary>
  /// <param name="stream">The destination stream.</param>
  /// <param name="leaveOpen">If <c>true</c>, the stream is not closed on dispose.</param>
  public CramFsWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a regular file with the given path and content.</summary>
  /// <param name="path">Forward-slash-separated path (e.g. "dir/file.txt").</param>
  /// <param name="data">The file content.</param>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._pending.Add(new PendingEntry(NormalisePath(path), EntryKind.File, data, null));
  }

  /// <summary>Adds an explicit directory entry.</summary>
  /// <param name="path">Forward-slash-separated directory path.</param>
  public void AddDirectory(string path) {
    ArgumentNullException.ThrowIfNull(path);
    this._pending.Add(new PendingEntry(NormalisePath(path), EntryKind.Directory, null, null));
  }

  /// <summary>Adds a symbolic link.</summary>
  /// <param name="path">Forward-slash-separated path for the link.</param>
  /// <param name="target">The symlink target string.</param>
  public void AddSymlink(string path, string target) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(target);
    this._pending.Add(new PendingEntry(NormalisePath(path), EntryKind.Symlink, Encoding.UTF8.GetBytes(target), null));
  }

  /// <summary>Serialises the entire CramFS image to the output stream.</summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;

    this.WriteImage();

    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  // ── Internal types ─────────────────────────────────────────────────────────

  private enum EntryKind { File, Directory, Symlink }

  private sealed record PendingEntry(string Path, EntryKind Kind, byte[]? Data, string? Target);

  private sealed class TreeNode {
    public string Name { get; init; } = "";
    public EntryKind Kind { get; init; } = EntryKind.Directory;
    public byte[]? Data { get; init; }
    public List<TreeNode> Children { get; } = [];
  }

  // ── Image construction ─────────────────────────────────────────────────────

  private void WriteImage() {
    // 1. Build the directory tree.
    var root = this.BuildTree();

    // 2. Count total files (regular files + symlinks) for the superblock.
    var fileCount = 0;
    var totalBlocks = 0;
    CountFilesAndBlocks(root, ref fileCount, ref totalBlocks);

    // 3. Serialise into a MemoryStream so we can compute CRC over the whole image.
    using var ms = new MemoryStream();

    // Reserve space for superblock (76 bytes). We will patch it later.
    ms.Write(new byte[CramFsConstants.SuperblockSize]);

    // 4. Lay out all directory and file data.
    //    For each directory: write child inodes + names sequentially.
    //    For each file/symlink: write block pointer table + compressed blocks.
    //    We need to know the byte offset where each directory's children are written,
    //    and the byte offset where each file's block pointers start.

    // Phase A: Assign data offsets for files/symlinks by compressing them.
    //          Store the compressed result so we only compress once.
    var fileDataMap = new Dictionary<TreeNode, (byte[] blob, int blockCount)>();
    CompressAllFiles(root, fileDataMap);

    // Phase B: Layout. We write directories depth-first.
    //          Each directory's "data" is the list of child inodes+names.
    //          Each file's "data" is block pointers + compressed blocks.
    //
    // Strategy: two-pass.
    //   Pass 1: compute sizes of everything so we can assign offsets.
    //   Pass 2: write bytes.
    //
    // Simpler approach: write file data first (after superblock), record offsets,
    // then write directory data, record offsets, then patch root inode.
    // But directories reference child inodes which embed offsets to their data...
    // So we need all offsets known before writing any inodes.
    //
    // Approach: serialise bottom-up.
    //   1. Write all file/symlink blobs, record their offsets.
    //   2. Write leaf directories (no subdirs), record offsets.
    //   3. Walk up, writing parent directories whose children now have known offsets.
    //
    // Simplest correct approach: recursive, writing files first, then directories
    // from leaves upward. But since directory data contains child inodes and children
    // may be other directories whose data hasn't been placed yet... we need offsets
    // before we can write the parent directory.
    //
    // Two-pass approach:
    //   Pass 1: Write all file/symlink blobs to ms. Record offset for each.
    //   Pass 2: Write directories bottom-up. For each dir, serialise child inodes
    //           (using known offsets for files and already-placed subdirs) + names.

    // Pass 1: Write file/symlink blobs.
    var fileOffsets = new Dictionary<TreeNode, int>();
    WriteFileBlobs(ms, root, fileDataMap, fileOffsets);

    // Pass 2: Write directory data bottom-up.
    var dirOffsets = new Dictionary<TreeNode, (int offset, int size)>();
    WriteDirData(ms, root, fileDataMap, fileOffsets, dirOffsets);

    // 5. Build root inode and patch superblock.
    var imageSize = (int)ms.Length;

    // Root directory info.
    var (rootDirOffset, rootDirSize) = dirOffsets[root];
    var rootInode = MakeInode(DirMode, 0, 0, rootDirSize, 0, rootDirOffset);

    // Patch superblock.
    var image = ms.ToArray();
    PatchSuperblock(image, (uint)imageSize, (uint)totalBlocks, (uint)fileCount, rootInode);

    // 6. Compute CRC-32 over entire image (with CRC field zeroed) and patch.
    // CRC field is at offset 32, 4 bytes — already zero from PatchSuperblock.
    var crc = new Crc32();
    crc.Update(image);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), crc.Value);

    this._stream.Write(image);
  }

  // ── Tree building ──────────────────────────────────────────────────────────

  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", Kind = EntryKind.Directory };

    // Ensure all implicit parent directories exist.
    foreach (var entry in this._pending) {
      EnsureParentDirs(root, entry.Path);
    }

    // Insert actual entries.
    foreach (var entry in this._pending) {
      var parent = NavigateToParent(root, entry.Path);
      var name = GetFileName(entry.Path);

      if (entry.Kind == EntryKind.Directory) {
        // May already exist from implicit creation.
        if (!parent.Children.Any(c => c.Name == name && c.Kind == EntryKind.Directory)) {
          parent.Children.Add(new TreeNode { Name = name, Kind = EntryKind.Directory });
        }
      } else {
        parent.Children.Add(new TreeNode {
          Name = name,
          Kind = entry.Kind,
          Data = entry.Data,
        });
      }
    }

    // Sort children in each directory (FlagSortedDirs).
    SortTree(root);
    return root;
  }

  private static void SortTree(TreeNode node) {
    node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
    foreach (var child in node.Children) {
      if (child.Kind == EntryKind.Directory)
        SortTree(child);
    }
  }

  private static void EnsureParentDirs(TreeNode root, string path) {
    var parts = path.Split('/');
    var current = root;
    // All parts except the last are directory components.
    for (var i = 0; i < parts.Length - 1; i++) {
      var existing = current.Children.FirstOrDefault(
        c => c.Name == parts[i] && c.Kind == EntryKind.Directory);
      if (existing == null) {
        existing = new TreeNode { Name = parts[i], Kind = EntryKind.Directory };
        current.Children.Add(existing);
      }
      current = existing;
    }
  }

  private static TreeNode NavigateToParent(TreeNode root, string path) {
    var parts = path.Split('/');
    var current = root;
    for (var i = 0; i < parts.Length - 1; i++) {
      current = current.Children.First(
        c => c.Name == parts[i] && c.Kind == EntryKind.Directory);
    }
    return current;
  }

  private static string GetFileName(string path) {
    var idx = path.LastIndexOf('/');
    return idx < 0 ? path : path[(idx + 1)..];
  }

  private static string NormalisePath(string path) {
    // Strip leading slashes and collapse.
    return path.TrimStart('/').Replace('\\', '/');
  }

  // ── Counting ───────────────────────────────────────────────────────────────

  private static void CountFilesAndBlocks(TreeNode node, ref int fileCount, ref int totalBlocks) {
    foreach (var child in node.Children) {
      if (child.Kind == EntryKind.Directory) {
        CountFilesAndBlocks(child, ref fileCount, ref totalBlocks);
      } else {
        fileCount++;
        var size = child.Data?.Length ?? 0;
        if (size > 0)
          totalBlocks += (size + CramFsConstants.PageSize - 1) / CramFsConstants.PageSize;
      }
    }
  }

  // ── File compression ───────────────────────────────────────────────────────

  private static void CompressAllFiles(TreeNode node, Dictionary<TreeNode, (byte[] blob, int blockCount)> map) {
    foreach (var child in node.Children) {
      if (child.Kind == EntryKind.Directory) {
        CompressAllFiles(child, map);
      } else {
        var data = child.Data ?? [];
        if (data.Length == 0) {
          map[child] = ([], 0);
          continue;
        }

        var blocks = (data.Length + CramFsConstants.PageSize - 1) / CramFsConstants.PageSize;
        // Build block pointer table + compressed blocks into a single blob.
        using var blobStream = new MemoryStream();

        // Reserve space for block pointer table.
        blobStream.Write(new byte[blocks * 4]);

        var endOffsets = new int[blocks];

        for (var i = 0; i < blocks; i++) {
          var offset = i * CramFsConstants.PageSize;
          var len = Math.Min(CramFsConstants.PageSize, data.Length - offset);
          var page = data.AsSpan(offset, len).ToArray();

          var zlibBlock = CompressZlib(page);
          blobStream.Write(zlibBlock);
          // endOffset is relative to the start of this blob in the image,
          // but the reader expects absolute image offsets. We will fix these up
          // when we know the blob's position in the image.
          endOffsets[i] = (int)blobStream.Length;
        }

        // We store endOffsets relative to blob start for now; they'll be
        // adjusted when writing to the image.
        var blob = blobStream.ToArray();

        // Write the (relative) end offsets into the pointer table region.
        for (var i = 0; i < blocks; i++) {
          BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(i * 4), (uint)endOffsets[i]);
        }

        map[child] = (blob, blocks);
      }
    }
  }

  private static byte[] CompressZlib(byte[] data) {
    // zlib frame: 2-byte header + deflate + 4-byte Adler-32 (big-endian).
    var deflated = DeflateCompressor.Compress(data);

    var result = new byte[2 + deflated.Length + 4];
    // CMF = 0x78 (deflate, window size 7 = 32K)
    // FLG = 0x9C (check bits so that CMF*256+FLG is divisible by 31, level 2)
    result[0] = 0x78;
    result[1] = 0x9C;
    deflated.CopyTo(result.AsSpan(2));

    var adler = new Adler32();
    adler.Update(data);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(2 + deflated.Length), adler.Value);

    return result;
  }

  // ── Writing file blobs ─────────────────────────────────────────────────────

  private static void WriteFileBlobs(
      MemoryStream ms,
      TreeNode node,
      Dictionary<TreeNode, (byte[] blob, int blockCount)> fileDataMap,
      Dictionary<TreeNode, int> fileOffsets) {
    foreach (var child in node.Children) {
      if (child.Kind == EntryKind.Directory) {
        WriteFileBlobs(ms, child, fileDataMap, fileOffsets);
      } else {
        var (blob, _) = fileDataMap[child];
        if (blob.Length == 0) {
          fileOffsets[child] = 0;
          continue;
        }

        // CramFS stores offsets in units of 4 bytes, so data must be 4-byte aligned.
        Pad4(ms);
        var blobStart = (int)ms.Position;
        fileOffsets[child] = blobStart;

        // Fix up the block pointer end-offsets: they are currently relative
        // to blob start, but the reader expects absolute image offsets.
        var blockCount = fileDataMap[child].blockCount;
        for (var i = 0; i < blockCount; i++) {
          var relEnd = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(i * 4));
          BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(i * 4), (uint)(blobStart + relEnd));
        }

        ms.Write(blob);
      }
    }
  }

  // ── Writing directory data ─────────────────────────────────────────────────

  private static void WriteDirData(
      MemoryStream ms,
      TreeNode node,
      Dictionary<TreeNode, (byte[] blob, int blockCount)> fileDataMap,
      Dictionary<TreeNode, int> fileOffsets,
      Dictionary<TreeNode, (int offset, int size)> dirOffsets) {
    // Recurse into subdirectories first so their offsets are known.
    foreach (var child in node.Children) {
      if (child.Kind == EntryKind.Directory)
        WriteDirData(ms, child, fileDataMap, fileOffsets, dirOffsets);
    }

    // Now serialise this directory's children.
    if (node.Children.Count == 0) {
      dirOffsets[node] = (0, 0);
      return;
    }

    // CramFS stores offsets in units of 4 bytes, so data must be 4-byte aligned.
    Pad4(ms);
    var dirStart = (int)ms.Position;

    foreach (var child in node.Children) {
      // Determine inode fields.
      ushort mode;
      int size;
      int dataOffset;

      if (child.Kind == EntryKind.Directory) {
        mode = DirMode;
        var (off, sz) = dirOffsets[child];
        size = sz;
        dataOffset = off;
      } else if (child.Kind == EntryKind.Symlink) {
        mode = SymlinkMode;
        size = child.Data?.Length ?? 0;
        dataOffset = fileOffsets[child];
      } else {
        mode = FileMode;
        size = child.Data?.Length ?? 0;
        dataOffset = fileOffsets[child];
      }

      // Name bytes, null-padded to multiple of 4.
      var nameBytes = Encoding.UTF8.GetBytes(child.Name);
      var paddedLen = Align4(nameBytes.Length + 1); // +1 for null terminator, then align
      var paddedName = new byte[paddedLen]; // zero-filled
      nameBytes.CopyTo(paddedName, 0);

      var nameLenField = paddedLen / 4;

      var inode = MakeInode(mode, 0, 0, size, nameLenField, dataOffset);
      ms.Write(inode);
      ms.Write(paddedName);
    }

    var dirSize = (int)ms.Position - dirStart;
    dirOffsets[node] = (dirStart, dirSize);
  }

  // ── Inode encoding ─────────────────────────────────────────────────────────

  private static byte[] MakeInode(ushort mode, ushort uid, byte gid, int size, int namelen, int dataOffset) {
    // word 0: mode[15:0] | uid[31:16]
    var w0 = (uint)mode | ((uint)uid << 16);

    // word 1: size[23:0] | gid[31:24]
    var w1 = ((uint)size & 0x00FFFFFF) | ((uint)gid << 24);

    // word 2: namelen[5:0] | offset[31:6]
    //   offset field = dataOffset / 4 (stored in bits 6-31)
    //   namelen in bits 0-5
    var offsetField = (uint)(dataOffset / 4);
    var w2 = ((uint)namelen & 0x3F) | (offsetField << 6);

    var buf = new byte[CramFsConstants.InodeSize];
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), w0);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), w1);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), w2);
    return buf;
  }

  // ── Superblock ─────────────────────────────────────────────────────────────

  private static void PatchSuperblock(byte[] image, uint totalSize, uint blockCount, uint fileCount, byte[] rootInode) {
    var span = image.AsSpan();

    // [0..4] Magic
    BinaryPrimitives.WriteUInt32LittleEndian(span[0..], CramFsConstants.MagicLE);
    // [4..8] Size
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], totalSize);
    // [8..12] Flags: FsidVersion2 | SortedDirs
    BinaryPrimitives.WriteUInt32LittleEndian(span[8..], CramFsConstants.FlagFsidVersion2 | CramFsConstants.FlagSortedDirs);
    // [12..16] Future
    BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 0);
    // [16..32] Signature
    Encoding.ASCII.GetBytes(CramFsConstants.Signature).CopyTo(span[16..]);
    // [32..36] CRC — zeroed now, patched after full-image CRC is computed.
    BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 0);
    // [36..40] Edition
    BinaryPrimitives.WriteUInt32LittleEndian(span[36..], 0);
    // [40..44] Number of blocks
    BinaryPrimitives.WriteUInt32LittleEndian(span[40..], blockCount);
    // [44..48] Number of files
    BinaryPrimitives.WriteUInt32LittleEndian(span[44..], fileCount);
    // [48..52] Name length
    BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 0);
    // [52..56] Reserved
    BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 0);
    // [56..60] Reserved
    BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0);
    // [60..72] Root inode (12 bytes)
    rootInode.CopyTo(span[CramFsConstants.RootInodeOffset..]);
    // [72..76] Reserved
    BinaryPrimitives.WriteUInt32LittleEndian(span[72..], 0);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  private static int Align4(int value) => (value + 3) & ~3;

  private static void Pad4(MemoryStream ms) {
    var pad = (4 - (int)(ms.Position % 4)) % 4;
    for (var i = 0; i < pad; i++) ms.WriteByte(0);
  }
}
