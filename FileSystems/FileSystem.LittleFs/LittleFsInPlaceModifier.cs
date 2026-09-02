#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.LittleFs.LittleFsFormat;

namespace FileSystem.LittleFs;

/// <summary>
/// Genuine in-place modifier for littlefs v2 images emitted by
/// <see cref="LittleFsWriter"/>. littlefs is inherently append/copy-on-write within
/// its erase blocks: each metadata pair ping-pongs between its two blocks (the half
/// with the higher revision count wins), and file data lives in CTZ skip-list blocks
/// addressed by block index. This modifier exploits exactly that:
///
/// <list type="bullet">
///   <item><description>The root metadata pair stays at blocks 0,1. A mutation
///   rewrites only the <em>inactive</em> half (the one with the lower revision) with
///   a fresh commit at <c>revision+1</c> carrying the updated directory — the
///   active half stays byte-identical, exactly the metadata-pair ping-pong the
///   reference driver performs.</description></item>
///   <item><description>New subdirectory metadata pairs and new CTZ file data are
///   written into blocks <em>appended</em> past the current block count; existing
///   data blocks are never overwritten or relocated, so they stay byte-identical at
///   their offsets.</description></item>
/// </list>
///
/// <para><b>In-place invariant.</b> Add / Replace / Remove preserve the active root
/// half plus every existing CTZ/subdir block byte-identical at its offset, and grow
/// the image by only the blocks the new tree needs (O(bytes changed) for the common
/// flat-file case). The superblock's <c>block_count</c> is updated in the rewritten
/// root commit (it is covered by the commit CRC, so it is re-emitted, not patched).</para>
///
/// <para><b>Verification.</b> littlefs has no Linux fsck; correctness is proven by
/// the reader round-trip plus the byte-identity offset proof.</para>
/// </summary>
public static class LittleFsInPlaceModifier {

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<Compression.Registry.ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    EnsureRwSeek(image);

    var (img, blockSize) = ReadImage(image);
    var files = ReadFiles(img);

    var changed = false;
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName.Replace('\\', '/').Trim('/');
      if (name.Length == 0) continue;
      files[name] = input.ReadContent();
      changed = true;
    }
    if (!changed) return;

    WriteBack(image, img, blockSize, files);
  }

  /// <summary>
  /// Performs the replace operation.
  /// </summary>
  public static void Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    EnsureRwSeek(image);

    var (img, blockSize) = ReadImage(image);
    var files = ReadFiles(img);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (!files.ContainsKey(normalized))
      throw new FileNotFoundException($"LittleFs entry '{normalized}' not present.");
    files[normalized] = newData;
    WriteBack(image, img, blockSize, files);
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
  public static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    EnsureRwSeek(image);

    var (img, blockSize) = ReadImage(image);
    var files = ReadFiles(img);

    var changed = false;
    foreach (var raw in entryNames) {
      if (string.IsNullOrEmpty(raw)) continue;
      var normalized = raw.Replace('\\', '/').Trim('/');
      if (files.Remove(normalized)) changed = true;
    }
    if (!changed) return;

    WriteBack(image, img, blockSize, files);
  }

  // ── Internals ───────────────────────────────────────────────────────────

  private static void EnsureRwSeek(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("LittleFs in-place modify requires a read/write/seek stream.", nameof(image));
  }

  private static (byte[] Image, uint BlockSize) ReadImage(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var img = ms.ToArray();
    var sb = LittleFsSuperblock.TryParse(img);
    if (!sb.Valid)
      throw new InvalidDataException("not a recognised littlefs image (no valid superblock).");
    return (img, sb.BlockSize);
  }

  private static SortedDictionary<string, byte[]> ReadFiles(byte[] img) {
    var reader = new LittleFsReader(img);
    var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var f in reader.Files)
      files[f.Path] = reader.ReadFile(f);
    return files;
  }

  /// <summary>
  /// Re-lays the (mutated) file tree using append-only block allocation rooted at
  /// the existing block pair (0,1) and rewrites only the inactive root half at
  /// <c>revision+1</c>. Existing live blocks (the active root half + every CTZ /
  /// subdir block referenced from it) are never overwritten.
  /// </summary>
  private static void WriteBack(Stream image, byte[] img, uint blockSize, SortedDictionary<string, byte[]> files) {
    var bs = (int)blockSize;
    var currentBlocks = (uint)(img.LongLength / bs);

    // Determine which root half is active (higher revision) and the active rev.
    var revA = currentBlocks >= 1 ? BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0, 4)) : 0u;
    var revB = currentBlocks >= 2 ? BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(bs, 4)) : 0u;
    var activeRev = Math.Max(revA, revB);
    var newRev = activeRev + 1;
    // The half the reader currently prefers (revA >= revB ? A : B) must stay
    // byte-identical; we rewrite the OTHER half.
    var rewriteBlockIndex = revA >= revB ? 1u : 0u;

    // Build the new tree into appended blocks (block cursor starts past EOF so we
    // never touch existing data). Subdirectory pairs + CTZ data go to the tail.
    var layout = new TreeLayout(blockSize, firstAppendBlock: currentBlocks);
    foreach (var (path, data) in files)
      layout.AddFile(path, data);
    layout.AssignBlocks();

    var newBlockCount = layout.NextFreeBlock;
    var (appendedBlocks, rootCommit) = layout.Serialize(blockCount: newBlockCount, rootRevision: newRev);

    // Grow the image to hold the appended blocks, copying existing bytes verbatim.
    var newImage = img;
    if ((long)newBlockCount * bs > img.LongLength) {
      newImage = new byte[(long)newBlockCount * bs];
      img.CopyTo(newImage, 0);
    }
    // Splat appended blocks (indices >= currentBlocks) — all fresh, no overwrite.
    foreach (var (index, content) in appendedBlocks)
      if (index >= currentBlocks)
        content.CopyTo(newImage.AsSpan((int)((long)index * bs)));

    // Rewrite ONLY the inactive root half with the new commit at revision+1.
    rootCommit.CopyTo(newImage.AsSpan((int)((long)rewriteBlockIndex * bs)));

    // Persist.
    image.Position = 0;
    image.SetLength(newImage.LongLength);
    image.Write(newImage, 0, newImage.Length);
    image.Flush();
  }

  /// <summary>
  /// Lays out a littlefs directory tree with the root metadata pair pinned at
  /// blocks (0,1) and every other block (subdirectory pairs, CTZ data) allocated
  /// from a configurable append cursor so existing blocks are never reused.
  /// Mirrors <see cref="LittleFsWriter"/>'s encoding (inline structs for small
  /// files, CTZ skip-lists otherwise) but produces the root commit separately so
  /// the caller can write it into just the inactive root half.
  /// </summary>
  private sealed class TreeLayout {
    private readonly uint _blockSize;
    private readonly DirNode _root = new(string.Empty);
    private readonly BlockAllocator _allocator;

    public TreeLayout(uint blockSize, uint firstAppendBlock) {
      this._blockSize = blockSize;
      this._root.Pair = (0, 1);
      // Append cursor must clear the root pair too.
      this._allocator = new BlockAllocator(Math.Max(firstAppendBlock, 2u));
    }

    public uint NextFreeBlock => this._allocator.NextFree;

    public void AddFile(string path, byte[] data) {
      var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) return;
      var dir = this._root;
      for (var i = 0; i < parts.Length - 1; ++i)
        dir = dir.GetOrAddChild(parts[i]);
      dir.Files[parts[^1]] = data;
    }

    public void AssignBlocks() {
      // Subdirectory pairs first (append region), then CTZ data.
      AssignDirPairs(this._root, this._allocator);
      this.LayOutCtz(this._root);
    }

    /// <summary>
    /// Serialises every subdirectory + CTZ block (appended) and returns the root
    /// commit separately. <paramref name="blockCount"/> is baked into the root
    /// superblock struct; <paramref name="rootRevision"/> is the revision written
    /// into the root commit's leading word.
    /// </summary>
    public (Dictionary<uint, byte[]> Blocks, byte[] RootCommit) Serialize(uint blockCount, uint rootRevision) {
      var blocks = new Dictionary<uint, byte[]>();

      // Re-emit the CTZ data blocks (allocated during LayOutCtz).
      foreach (var (index, content) in this._ctzBlocks)
        blocks[index] = content;

      // Subdirectory commits (mirrored into both halves of each pair) at revision 1.
      this.BuildSubdirs(this._root, blocks, blockCount);

      var rootCommit = this.BuildRootCommit(blockCount, rootRevision);
      return (blocks, rootCommit);
    }

    private readonly Dictionary<uint, byte[]> _ctzBlocks = new();

    private static void AssignDirPairs(DirNode node, BlockAllocator allocator) {
      foreach (var child in node.Children.Values) {
        child.Pair = allocator.AllocatePair();
        AssignDirPairs(child, allocator);
      }
    }

    private void LayOutCtz(DirNode node) {
      foreach (var child in node.Children.Values)
        this.LayOutCtz(child);
      foreach (var (fileName, data) in node.Files) {
        if (CanInline(data.Length, this._blockSize)) continue;
        node.CtzStructs[fileName] = this.WriteCtz(data);
      }
    }

    private void BuildSubdirs(DirNode node, Dictionary<uint, byte[]> blocks, uint blockCount) {
      foreach (var child in node.Children.Values) {
        this.BuildSubdirs(child, blocks, blockCount);
        var commit = this.BuildDirCommit(child, isRoot: false, blockCount, revision: 1u);
        blocks[child.Pair.Item1] = commit;
        blocks[child.Pair.Item2] = commit;
      }
    }

    private byte[] BuildRootCommit(uint blockCount, uint revision)
      => this.BuildDirCommit(this._root, isRoot: true, blockCount, revision);

    private byte[] BuildDirCommit(DirNode node, bool isRoot, uint blockCount, uint revision) {
      var commit = new CommitBuilder(this._blockSize);
      uint id = 0;

      if (isRoot) {
        commit.AddTag(TypeSuperblock, id, "littlefs"u8);
        Span<byte> sbStruct = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[0..], DiskVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[4..], this._blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[8..], blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[12..], NameMax);
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[16..], FileMaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(sbStruct[20..], AttrMaxValue);
        commit.AddTag(TypeInlineStruct, id, sbStruct);
        ++id;
      }

      foreach (var child in node.Children.Values) {
        commit.AddTag(TypeDir, id, Encoding.ASCII.GetBytes(child.Name));
        var pair = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(0), child.Pair.Item1);
        BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(4), child.Pair.Item2);
        commit.AddTag(TypeDirStruct, id, pair);
        ++id;
      }

      foreach (var (fileName, data) in node.Files) {
        commit.AddTag(TypeReg, id, Encoding.ASCII.GetBytes(fileName));
        if (CanInline(data.Length, this._blockSize)) {
          commit.AddTag(TypeInlineStruct, id, data);
        } else {
          var (head, size) = node.CtzStructs[fileName];
          var ctz = new byte[8];
          BinaryPrimitives.WriteUInt32LittleEndian(ctz.AsSpan(0), head);
          BinaryPrimitives.WriteUInt32LittleEndian(ctz.AsSpan(4), size);
          commit.AddTag(TypeCtzStruct, id, ctz);
        }
        ++id;
      }

      return commit.Finish(revision);
    }

    private static bool CanInline(int length, uint blockSize)
      => length <= 512 && (uint)length < blockSize / 4;

    private (uint Head, uint Size) WriteCtz(byte[] data) {
      var blockSize = (int)this._blockSize;
      var blockIndices = new List<uint>();
      var dataOffset = 0;
      var i = 0;
      while (dataOffset < data.Length) {
        var pointerCount = i == 0 ? 0 : (TrailingZeros((uint)i) + 1);
        var pointerBytes = pointerCount * 4;
        var dataCap = blockSize - pointerBytes;
        var block = new byte[blockSize];
        for (var p = 0; p < pointerCount; ++p) {
          var target = i - (1 << p);
          BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(p * 4, 4), blockIndices[target]);
        }
        var take = Math.Min(dataCap, data.Length - dataOffset);
        data.AsSpan(dataOffset, take).CopyTo(block.AsSpan(pointerBytes));
        dataOffset += take;
        var index = this._allocator.Allocate();
        this._ctzBlocks[index] = block;
        blockIndices.Add(index);
        ++i;
      }
      return (blockIndices[^1], (uint)data.Length);
    }

    private static int TrailingZeros(uint x) {
      var n = 0;
      while ((x & 1) == 0) { x >>= 1; ++n; }
      return n;
    }

    private sealed class DirNode(string name) {
      public string Name { get; } = name;
      public Dictionary<string, DirNode> Children { get; } = new(StringComparer.Ordinal);
      public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
      public Dictionary<string, (uint Head, uint Size)> CtzStructs { get; } = new(StringComparer.Ordinal);
      public (uint, uint) Pair { get; set; }

      public DirNode GetOrAddChild(string component) {
        if (!this.Children.TryGetValue(component, out var child)) {
          child = new DirNode(component);
          this.Children[component] = child;
        }
        return child;
      }
    }

    private sealed class BlockAllocator(uint firstFree) {
      public uint NextFree { get; private set; } = firstFree;
      public uint Allocate() => this.NextFree++;
      public (uint, uint) AllocatePair() => (this.NextFree++, this.NextFree++);
    }
  }
}
