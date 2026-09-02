#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.LittleFs.LittleFsFormat;

namespace FileSystem.LittleFs;

/// <summary>
/// From-scratch (write-once) builder for a minimal but specification-accurate
/// littlefs v2 image. Produces a root metadata pair carrying the superblock and
/// the root directory entries, one metadata pair per subdirectory (linked via
/// hard-tail tags), inline structs for small files, and CTZ skip-lists for files
/// that do not fit inline. The result round-trips through <see cref="LittleFsReader"/>.
/// </summary>
/// <remarks>
/// Layout strategy: blocks 0 and 1 form the root metadata pair (both blocks carry
/// the same commit so either half validates). Subdirectory metadata pairs and file
/// data blocks are allocated from a monotonically increasing block cursor. The
/// image is sized to hold every allocated block exactly; there is no wear-levelling
/// reserve because the image is immutable.
/// </remarks>
public sealed class LittleFsWriter {
  private const uint DefaultBlockSize = 4096;

  private readonly uint _blockSize;
  private readonly DirNode _root = new(string.Empty);

  /// <summary>
  /// Initializes a new instance of <see cref="LittleFsWriter"/>.
  /// </summary>
public LittleFsWriter(uint blockSize = DefaultBlockSize) {
    if (blockSize is < 128u or > 65536u || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "block size must be a power of two in [128, 65536].");
    this._blockSize = blockSize;
  }

  /// <summary>Adds a file at <paramref name="name"/>, creating intermediate
  /// directories as needed. Forward slashes separate path components.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      throw new ArgumentException("empty file name.", nameof(name));

    var dir = this._root;
    for (var i = 0; i < parts.Length - 1; ++i)
      dir = dir.GetOrAddChild(parts[i]);

    var leaf = parts[^1];
    if (leaf.Length > NameMax)
      throw new ArgumentException($"name component exceeds {NameMax} bytes.", nameof(name));
    dir.Files[leaf] = data;
  }

  /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // ── 1. Allocate blocks ────────────────────────────────────────────────
    // The root pair always occupies blocks 0,1. Every other directory gets a
    // metadata pair; every CTZ file gets a contiguous run of data blocks.
    var allocator = new BlockAllocator(firstFree: 2);

    // Assign metadata pairs depth-first so a parent can reference each child.
    this._root.Pair = (0, 1);
    AssignDirPairs(this._root, allocator);

    // ── 2. Lay out CTZ data blocks first so the final block_count is known
    //       before any superblock is serialised (the superblock records it,
    //       and it is covered by the commit CRC, so it cannot be patched later).
    var blocks = new Dictionary<uint, byte[]>();
    this.LayOutCtzData(this._root, allocator, blocks);
    var blockCount = allocator.NextFree;

    // ── 3. Build directory commits with the final block_count baked in. ────
    this.BuildDir(this._root, blocks, blockCount);

    // ── 4. Emit the image ─────────────────────────────────────────────────
    // Block by block, not as one array: the blocks are already laid out
    // individually, so materialising the whole volume only served to cap it at
    // what a byte[] can address.
    var totalBytes = (long)blockCount * this._blockSize;
    if (output.CanSeek) {
      var basePosition = output.Position;
      output.SetLength(basePosition + totalBytes);
      foreach (var index in blocks.Keys.Order()) {
        output.Position = basePosition + (long)index * this._blockSize;
        output.Write(blocks[index], 0, blocks[index].Length);
      }
      output.Position = basePosition + totalBytes;
      output.Flush();
      return;
    }

    if (totalBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"LittleFS: a {totalBytes:N0}-byte volume exceeds the array limit; write it to a seekable stream instead.");
    var image = new byte[totalBytes];
    foreach (var (index, content) in blocks)
      content.CopyTo(image.AsSpan((int)((long)index * this._blockSize)));

    output.Write(image, 0, image.Length);
  }

  /// <summary>Allocates and fills every file's CTZ data blocks, recording the
  /// resulting (head, size) on each file so the commit pass can reference it.</summary>
  private void LayOutCtzData(DirNode node, BlockAllocator allocator, Dictionary<uint, byte[]> blocks) {
    foreach (var child in node.Children.Values)
      this.LayOutCtzData(child, allocator, blocks);

    foreach (var (fileName, data) in node.Files) {
      if (CanInline(data.Length)) continue;
      var (head, size) = this.WriteCtz(data, allocator, blocks);
      node.CtzStructs[fileName] = (head, size);
    }
  }

  private static void AssignDirPairs(DirNode node, BlockAllocator allocator) {
    foreach (var child in node.Children.Values) {
      child.Pair = allocator.AllocatePair();
      AssignDirPairs(child, allocator);
    }
  }

  /// <summary>
  /// Builds the metadata commit for <paramref name="node"/> into both halves of
  /// its block pair. Recurses into children first.
  /// </summary>
  private void BuildDir(DirNode node, Dictionary<uint, byte[]> blocks, uint blockCount) {
    foreach (var child in node.Children.Values)
      this.BuildDir(child, blocks, blockCount);

    var commit = new CommitBuilder(this._blockSize);
    uint id = 0;

    // The root pair leads with the superblock entry (id 0): a NAME tag carrying
    // the "littlefs" magic + an inline struct carrying version/geometry.
    if (node == this._root) {
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

    // Subdirectory entries: a NAME(dir) tag + a dir-struct tag pointing at the
    // child's metadata pair.
    foreach (var child in node.Children.Values) {
      commit.AddTag(TypeDir, id, Encoding.ASCII.GetBytes(child.Name));
      var pair = new byte[8];
      BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(0), child.Pair.Item1);
      BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(4), child.Pair.Item2);
      commit.AddTag(TypeDirStruct, id, pair);
      ++id;
    }

    // File entries: a NAME(reg) tag + either an inline struct (data fits in the
    // metadata block) or a CTZ struct referencing the data blocks laid out earlier.
    foreach (var (fileName, data) in node.Files) {
      commit.AddTag(TypeReg, id, Encoding.ASCII.GetBytes(fileName));
      if (CanInline(data.Length)) {
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

    var (a, b) = node.Pair;
    const uint revision = 1u;
    blocks[a] = commit.Finish(revision);
    blocks[b] = commit.Finish(revision); // mirror: identical commit, same revision
  }

  /// <summary>Inline structs are bounded by the 10-bit tag length and by leaving
  /// room for the rest of the commit in one metadata block. We keep the cap
  /// comfortably small so the whole commit always fits in one block.</summary>
  private bool CanInline(int length)
    => length <= 512 && (uint)length < this._blockSize / 4;

  /// <summary>
  /// Lays out <paramref name="data"/> as a littlefs CTZ skip-list and returns the
  /// head block index. Block i (i&gt;0) starts with ctz(i)+1 back-pointers; block 0
  /// has none. Data fills the remainder of each block.
  /// </summary>
  private (uint Head, uint Size) WriteCtz(byte[] data, BlockAllocator allocator, Dictionary<uint, byte[]> blocks) {
    var blockSize = (int)this._blockSize;
    var blockIndices = new List<uint>();

    var dataOffset = 0;
    var i = 0;
    while (dataOffset < data.Length) {
      var pointerCount = i == 0 ? 0 : (TrailingZeros((uint)i) + 1);
      var pointerBytes = pointerCount * 4;
      var dataCap = blockSize - pointerBytes;

      var block = new byte[blockSize];

      // Back-pointers: to blocks i-1, i-2, i-4, ... i-2^(ctz(i)).
      for (var p = 0; p < pointerCount; ++p) {
        var target = i - (1 << p);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(p * 4, 4), blockIndices[target]);
      }

      var take = Math.Min(dataCap, data.Length - dataOffset);
      data.AsSpan(dataOffset, take).CopyTo(block.AsSpan(pointerBytes));
      dataOffset += take;

      var index = allocator.Allocate();
      blocks[index] = block;
      blockIndices.Add(index);
      ++i;
    }

    // littlefs records the head as the LAST block of the skip-list (highest index
    // in file order) together with the total size; readers walk backwards.
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
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
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
