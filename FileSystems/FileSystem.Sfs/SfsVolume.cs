#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Sfs;

/// <summary>
/// Walks an SFS volume to its files and says which blocks each one owns.
/// </summary>
/// <remarks>
/// <para>The root block names the root directory's object container and the
/// tree of extents. A directory entry names one extent key; that key finds an
/// extent in the tree, which says how many blocks it covers and which key comes
/// after it. Following that chain gives a file's blocks in the order its bytes
/// run — and it is the only record of them, which is what makes the chain the
/// thing a layout pass rewrites.</para>
///
/// <para>Only what this project writes is walked: one object container, one
/// tree leaf, a flat root. Hash tables, soft links, sub-directories and
/// multi-level trees are refused rather than half-read.</para>
/// </remarks>
public sealed class SfsVolume {

  /// <summary>A run of blocks, and where the entry claiming it sits.</summary>
  /// <param name="Block">The first block, which is also the extent's key.</param>
  /// <param name="Count">How many blocks it covers.</param>
  /// <param name="NodeOffset">Where the extent's own record is in the image.</param>
  public readonly record struct Extent(long Block, long Count, long NodeOffset);

  /// <summary>A file in the root directory.</summary>
  /// <param name="Name">Its name.</param>
  /// <param name="Size">How many bytes it holds.</param>
  /// <param name="EntryOffset">Where its directory entry is in the image.</param>
  /// <param name="Extents">The runs it owns, in the order its bytes run.</param>
  public sealed record VolumeFile(string Name, long Size, long EntryOffset, IReadOnlyList<Extent> Extents);

  private readonly byte[] _image;

  /// <summary>
  /// Gets a value indicating whether valid.
  /// </summary>
public bool Valid { get; private set; }
  /// <summary>
  /// Gets or sets the status.
  /// </summary>
public string Status { get; private set; } = "unparsed";
  /// <summary>
  /// Gets or sets the block size.
  /// </summary>
public int BlockSize { get; private set; } = 512;
  /// <summary>
  /// Gets or sets the total blocks.
  /// </summary>
public long TotalBlocks { get; private set; }

  /// <summary>Blocks the volume's own structures occupy.</summary>
  public IReadOnlyList<long> ReservedBlocks => this._reserved;
  private readonly List<long> _reserved = [];

  /// <summary>Where the tree of extents lives.</summary>
  public long ExtentTreeBlock { get; private set; }

  /// <summary>
  /// Gets the files.
  /// </summary>
public IReadOnlyList<VolumeFile> Files => this._files;
  private readonly List<VolumeFile> _files = [];

  /// <summary>
  /// Gets the image length.
  /// </summary>
public long ImageLength => this._image.LongLength;

  /// <summary>
  /// Initializes a new instance of <see cref="SfsVolume"/>.
  /// </summary>
public SfsVolume(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    using var ms = new MemoryStream();
    image.Position = 0;
    image.CopyTo(ms);
    this._image = ms.ToArray();

    try {
      this.Walk();
    } catch (Exception e) {
      this.Status = $"walk failed: {e.GetType().Name}";
    }
  }

  private void Walk() {
    if (this._image.Length < 512) { this.Status = "too small for a root block"; return; }
    if (!this._image.AsSpan(0, 4).SequenceEqual(SfsLayout.RootId)) { this.Status = "no SFS root block"; return; }

    var blockSize = (int)this.U32(SfsLayout.RbBlockSize);
    if (blockSize is < 512 or > 32768 || (blockSize & (blockSize - 1)) != 0) {
      this.Status = $"implausible block size {blockSize}";
      return;
    }
    this.BlockSize = blockSize;
    this.TotalBlocks = this.U32(SfsLayout.RbTotalBlocks);

    if (!SfsLayout.ChecksumHolds(this._image.AsSpan(0, blockSize))) {
      this.Status = "the root block's checksum does not hold";
      return;
    }

    var bitmapBase = this.U32(SfsLayout.RbBitmapBase);
    var adminBlock = this.U32(SfsLayout.RbAdminSpaceContainer);
    var objectBlock = this.U32(SfsLayout.RbRootObjectContainer);
    var extentBlock = this.U32(SfsLayout.RbExtentBNodeRoot);
    var nodeBlock = this.U32(SfsLayout.RbObjectNodeRoot);
    this.ExtentTreeBlock = extentBlock;

    if (!this.IsBlock(objectBlock, SfsLayout.ObjectContainerId)) {
      this.Status = "the root object container is not one";
      return;
    }

    if (!this.IsBlock(extentBlock, SfsLayout.BNodeContainerId)) {
      this.Status = "the extent tree root is not one";
      return;
    }

    var extents = this.ReadExtentLeaf(extentBlock);
    if (extents == null) return;

    // Everything the volume needs to describe itself, and the copy of the root
    // block it keeps at the far end.
    this._reserved.Add(0);
    for (var b = bitmapBase; b < objectBlock; ++b) this._reserved.Add(b);
    this._reserved.Add(objectBlock);
    this._reserved.Add(extentBlock);
    this._reserved.Add(nodeBlock);
    this._reserved.Add(adminBlock);
    if (this.TotalBlocks > 0) this._reserved.Add(this.TotalBlocks - 1);

    var container = objectBlock * blockSize;
    var cursor = SfsLayout.OcObjects;
    while (cursor + SfsLayout.ObName < blockSize) {
      var entry = container + cursor;
      if (this._image[entry + SfsLayout.ObName] == 0) break;

      var length = SfsWriter.EntryBytes(this._image.AsSpan((int)container, blockSize), cursor);
      var bits = this._image[entry + SfsLayout.ObBits];

      if ((bits & SfsLayout.OTypeDir) == 0) {
        var nameEnd = (int)entry + SfsLayout.ObName;
        while (this._image[nameEnd] != 0) ++nameEnd;
        var name = Encoding.ASCII.GetString(
          this._image, (int)entry + SfsLayout.ObName, nameEnd - ((int)entry + SfsLayout.ObName));

        var size = this.U32(entry + SfsLayout.ObSize);
        var chain = this.Chain(extents, this.U32(entry + SfsLayout.ObData));
        if (chain == null) { this.Status = $"the extent chain of '{name}' does not close"; return; }

        this._files.Add(new VolumeFile(name, size, entry, chain));
      }

      cursor += length;
    }

    this.Valid = true;
    this.Status = "ok";
  }

  /// <summary>Returns a file's bytes.</summary>
  public byte[] Read(VolumeFile file) {
    ArgumentNullException.ThrowIfNull(file);

    var data = new byte[file.Size];
    var written = 0L;
    foreach (var extent in file.Extents)
      for (var i = 0L; i < extent.Count && written < file.Size; ++i) {
        var from = (extent.Block + i) * this.BlockSize;
        var take = (int)Math.Min(this.BlockSize, file.Size - written);
        if (from < 0 || from + take > this._image.LongLength) return data;
        Array.Copy(this._image, from, data, written, take);
        written += take;
      }

    return data;
  }

  /// <summary>Reads the tree's one leaf, keyed by the block each extent starts at.</summary>
  private Dictionary<uint, Extent>? ReadExtentLeaf(long block) {
    var at = block * this.BlockSize;
    if (this._image[at + SfsLayout.BtcIsLeaf] == 0) {
      this.Status = "the extent tree has more than one level, which this does not read";
      return null;
    }

    var stride = this._image[at + SfsLayout.BtcNodeSize];
    if (stride != SfsLayout.ExtentNodeBytes) {
      this.Status = $"an extent of {stride} bytes is not one this reads";
      return null;
    }

    var count = BinaryPrimitives.ReadUInt16BigEndian(this._image.AsSpan((int)(at + SfsLayout.BtcNodeCount)));
    var extents = new Dictionary<uint, Extent>();
    for (var i = 0; i < count; ++i) {
      var node = at + SfsLayout.BtcNodes + i * stride;
      if (node + stride > this._image.LongLength) break;

      var key = this.U32(node + SfsLayout.ExKey);
      var blocks = BinaryPrimitives.ReadUInt16BigEndian(this._image.AsSpan((int)(node + SfsLayout.ExBlocks)));
      extents[key] = new Extent(key, blocks, node);
    }

    return extents;
  }

  /// <summary>Follows a file's chain of extents from the key its entry names.</summary>
  private List<Extent>? Chain(Dictionary<uint, Extent> extents, uint firstKey) {
    var chain = new List<Extent>();
    var key = firstKey;
    var guard = extents.Count + 1;

    while (key != 0 && guard-- > 0) {
      if (!extents.TryGetValue(key, out var extent)) return null;
      chain.Add(extent);
      key = this.U32(extent.NodeOffset + SfsLayout.ExNext);
    }

    return guard < 0 ? null : chain;
  }

  private bool IsBlock(long block, uint id) {
    var at = block * this.BlockSize;
    if (block <= 0 || at + this.BlockSize > this._image.LongLength) return false;
    if (this.U32(at) != id) return false;
    return SfsLayout.ChecksumHolds(this._image.AsSpan((int)at, this.BlockSize));
  }

  private uint U32(long at) => BinaryPrimitives.ReadUInt32BigEndian(this._image.AsSpan((int)at));
}
