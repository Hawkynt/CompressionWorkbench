#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Sfs;

/// <summary>
/// Moves a file's blocks inside an SFS volume and rewrites the tree of extents
/// that claimed them.
/// </summary>
/// <remarks>
/// <para>An extent's key <em>is</em> the block it starts at, so moving one
/// changes its name as well as its place. Everything that referred to it by
/// that name has to follow: the directory entry naming a file's first extent,
/// and the links from the extents either side of it in the chain. The leaf
/// holding them is kept sorted by key, so it is rewritten whole.</para>
///
/// <para>All of which happens once, after the pass. One run's old key is
/// routinely another's new one, and a tree rewritten halfway through would name
/// two extents the same thing.</para>
/// </remarks>
public sealed class SfsBlockMover : IFilesystemBlockMover {

  /// <summary>One extent of one file, and where it now is.</summary>
  private sealed class Slot {
    public required string FileName { get; init; }
    /// <summary>Where the file's directory entry sits.</summary>
    public required long EntryOffset { get; init; }
    /// <summary>Which link of the file's chain this is.</summary>
    public required int Index { get; init; }
    public long Block { get; set; }
    public required long Count { get; init; }
  }

  private readonly List<Slot> _slots = [];
  private SfsVolume? _volume;

  /// <summary>Reads the volume once and notes which extent claims each run.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    this._volume = new SfsVolume(image);
    if (!this._volume.Valid)
      throw new InvalidDataException($"SFS: {this._volume.Status}.");

    this._slots.Clear();
    foreach (var file in this._volume.Files)
      for (var i = 0; i < file.Extents.Count; ++i)
        this._slots.Add(new Slot {
          FileName = file.Name,
          EntryOffset = file.EntryOffset,
          Index = i,
          Block = file.Extents[i].Block,
          Count = file.Extents[i].Count,
        });
  }

    /// <summary>
  /// Gets the block size.
  /// </summary>
public int BlockSize => this._volume?.BlockSize ?? 512;

  /// <summary>First byte a file may occupy: past the structures at the front.</summary>
  public long FirstDataByte => this._volume == null ? 0 : SfsExtentMap.FirstDataByte(this._volume);

  /// <summary>Each call notes one run; the tree is written once the pass is over.</summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full one be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._volume == null) this.Init(image);
    if (oldOffset == newOffset) return;

    var bs = this.BlockSize;
    if (newOffset % bs != 0)
      throw new NotSupportedException(
        $"SFS: an extent is keyed by a block number, so it cannot start at byte {newOffset}.");

    if (newOffset < this.FirstDataByte)
      throw new NotSupportedException(
        "SFS: a file cannot start before the volume's own structures end — the root block, the " +
        "bitmap, the admin space, the node table, the extent tree and the root directory live there.");

    if (newOffset + length > (this._volume!.TotalBlocks - 1) * bs)
      throw new NotSupportedException(
        "SFS: a file cannot reach the copy of the root block the volume keeps at its far end.");

    // A run is found by who owns it, not by where it is: a held run keeps the
    // block it was lifted from until it is put down again, and something else
    // has very likely taken that block meanwhile.
    var oldBlock = oldOffset / bs;
    var blocks = length / bs;
    var slot = this._slots.FirstOrDefault(
                 x => x.Block == oldBlock && x.Count == blocks && x.FileName == fileName)
               ?? this._slots.FirstOrDefault(x => x.Block == oldBlock && x.Count == blocks)
      ?? throw new InvalidOperationException(
        $"SFS: no extent of '{fileName}' starts at block {oldBlock}, so it cannot be repointed.");

    slot.Block = newOffset / bs;
  }

  /// <summary>
  /// Writes the tree of extents and the directory entries that name their first
  /// links, then stamps both blocks' checksums.
  /// </summary>
  public void Settle(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._volume == null) return;

    var bs = this.BlockSize;
    var tree = new byte[bs];
    image.Position = this._volume.ExtentTreeBlock * bs;
    image.ReadExactly(tree);

    var ordered = this._slots.OrderBy(x => x.Block).ToList();
    if (SfsLayout.BtcNodes + ordered.Count * SfsLayout.ExtentNodeBytes > bs)
      throw new NotSupportedException(
        "SFS: the extents no longer fit one tree leaf, and this writes only one.");

    // A chain's links are keys, so each extent has to learn its neighbours'
    // new names before any of them is written down.
    var byChain = this._slots
      .GroupBy(x => x.EntryOffset)
      .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Index).ToList());

    var neighbours = new Dictionary<Slot, (long Prev, long Next)>();
    foreach (var chain in byChain.Values)
      for (var i = 0; i < chain.Count; ++i)
        neighbours[chain[i]] = (
          i > 0 ? chain[i - 1].Block : 0,
          i + 1 < chain.Count ? chain[i + 1].Block : 0);

    tree.AsSpan(SfsLayout.BtcNodes).Clear();
    BinaryPrimitives.WriteUInt16BigEndian(
      tree.AsSpan(SfsLayout.BtcNodeCount), (ushort)ordered.Count);
    tree[SfsLayout.BtcIsLeaf] = 1;
    tree[SfsLayout.BtcNodeSize] = SfsLayout.ExtentNodeBytes;

    for (var i = 0; i < ordered.Count; ++i) {
      var node = tree.AsSpan(SfsLayout.BtcNodes + i * SfsLayout.ExtentNodeBytes);
      var (prev, next) = neighbours[ordered[i]];
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExKey..], (uint)ordered[i].Block);
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExNext..], (uint)next);
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExPrev..], (uint)prev);
      BinaryPrimitives.WriteUInt16BigEndian(node[SfsLayout.ExBlocks..], (ushort)ordered[i].Count);
    }

    SfsLayout.SetChecksum(tree);
    image.Position = this._volume.ExtentTreeBlock * bs;
    image.Write(tree);

    // Each directory entry names the first link of its own chain.
    var containerBlock = this._slots.Count == 0
      ? -1
      : this._slots[0].EntryOffset / bs;
    if (containerBlock >= 0) {
      var container = new byte[bs];
      image.Position = containerBlock * bs;
      image.ReadExactly(container);

      foreach (var chain in byChain.Values) {
        var at = (int)(chain[0].EntryOffset - containerBlock * bs);
        BinaryPrimitives.WriteUInt32BigEndian(
          container.AsSpan(at + SfsLayout.ObData), (uint)chain[0].Block);
      }

      SfsLayout.SetChecksum(container);
      image.Position = containerBlock * bs;
      image.Write(container);
    }

    image.Flush();
  }
}
