#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Moves a file's bytes inside a bcachefs volume and rewrites the extent keys that
/// name them.
/// </summary>
/// <remarks>
/// <para>Where a run of a file's bytes sits is one word in one key in the extents
/// b-tree: the pointer, whose middle forty-four bits are a sector. Moving the run
/// is the copy plus that word — nothing else on the volume records the position,
/// because in bcachefs nothing else can.</para>
///
/// <para>The node the keys live in carries a checksum over everything it holds, so
/// the whole node is re-stamped once the pass is over rather than after each move.
/// Doing it per move would be correct and would rewrite the same sector once for
/// every extent on the volume.</para>
/// </remarks>
public sealed class BcacheFsBlockMover : IFilesystemBlockMover {

  /// <summary>One extent key's pointer: where it points, and where that word is.</summary>
  /// <remarks>
  /// Two sectors are kept, not one. The pass is told where a run started, even for
  /// a run it lifted out of the volume and put back later, so that is what a
  /// pointer answers to; where the run is now is the answer, and matching on it
  /// would let a run that has landed on another's old address claim that other's
  /// pointer.
  /// </remarks>
  private sealed class Slot {
    internal required long FieldOffset { get; init; }
    internal required long OriginalSector { get; init; }
    internal required long Sector { get; set; }
    internal required int Sectors { get; init; }
  }

  private readonly List<Slot> _slots = [];
  private long _extentsNodeOffset;
  private int _extentsNodeSectors;

  /// <summary>Reads the extents b-tree so its pointers can be found again.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._slots.Clear();
    this._extentsNodeOffset = 0;

    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid || !volume.Roots.TryGetValue(BtreeExtents, out var rootSector)) return;

    this._extentsNodeOffset = rootSector * SectorSize;
    this._extentsNodeSectors = volume.BucketSectorCount;

    var node = new byte[volume.BucketSectorCount * SectorSize];
    image.Position = this._extentsNodeOffset;
    image.ReadExactly(node);

    foreach (var (fieldOffset, sector, sectors) in EnumeratePointers(node))
      this._slots.Add(new Slot {
        FieldOffset = this._extentsNodeOffset + fieldOffset,
        OriginalSector = sector, Sector = sector, Sectors = sectors,
      });
  }

  /// <summary>Every extent pointer in a node: where its word is, and what it says.</summary>
  private static IEnumerable<(int FieldOffset, long Sector, int Sectors)> EnumeratePointers(byte[] node) {
    var offset = BcacheFsNodeBuilder.KeysOffset;
    var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));
    var end = offset + words * 8;
    if (end > node.Length) yield break;

    while (offset + 8 <= end) {
      var keyWords = node[offset];
      if (keyWords == 0) yield break;

      var bytes = keyWords * 8;
      if (offset + bytes > end) yield break;

      // Only keys written unpacked are moved: those are the ones this project
      // writes, and a volume it did not write is not one it rearranges.
      if ((node[offset + 1] & 0x7F) == KeyFormatCurrent && node[offset + 2] == KeyExtent) {
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(offset + 16));
        for (var value = offset + BkeyBytes; value + 8 <= offset + bytes; value += 8) {
          var word = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(value));
          if (!IsPointer(word)) continue;
          yield return (value, PointerSector(word), size);
          break;
        }
      }

      offset += bytes;
    }
  }

  /// <inheritdoc />
  public int AllocationBlockSize => BucketBytes;

  /// <summary>
  /// The unit a layout may place a run at: a whole bucket.
  /// </summary>
  /// <remarks>
  /// A pointer names a sector, so finer placement is expressible — and refused. An
  /// extent may not straddle a bucket boundary, because a bucket is what bcachefs
  /// allocates and accounts in; a run laid down across one is read as an invalid
  /// key and the file it belongs to comes back as a hole. Quantising the layout to
  /// buckets is what keeps every run inside one.
  /// </remarks>
  public int BlockSize => BucketBytes;

  /// <summary>
  /// The first byte a file's bytes may occupy.
  /// </summary>
  /// <remarks>
  /// It is where the volume's own structures end, not where the first file
  /// currently starts. Taking the second would mean a volume whose files had been
  /// pushed to the tail could never be brought back to the front: the layout would
  /// be told the front was occupied by something it must not touch.
  /// </remarks>
  public long FirstDataByte => MetadataEndBytes;

  /// <inheritdoc />
  public bool RepointsRunsIndependently => true;

  /// <inheritdoc />
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long sourceOffset, long destinationOffset, long length,
      bool zeroSource = false) {
    ArgumentNullException.ThrowIfNull(image);
    if (sourceOffset == destinationOffset || length <= 0) return;

    var buffer = new byte[Math.Min(length, BucketBytes)];
    var moved = 0L;
    while (moved < length) {
      var chunk = (int)Math.Min(buffer.Length, length - moved);
      image.Position = sourceOffset + moved;
      image.ReadExactly(buffer, 0, chunk);
      image.Position = destinationOffset + moved;
      image.Write(buffer, 0, chunk);
      moved += chunk;
    }

    if (!zeroSource) return;

    Array.Clear(buffer);
    var cleared = 0L;
    while (cleared < length) {
      var chunk = (int)Math.Min(buffer.Length, length - cleared);
      image.Position = sourceOffset + cleared;
      image.Write(buffer, 0, chunk);
      cleared += chunk;
    }
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName,
      long sourceOffset, long destinationOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    _ = fileName;   // a pointer is found by where it points, not by whose bytes they are
    if (sourceOffset == destinationOffset) return;

    var sourceSector = sourceOffset / SectorSize;
    var sectors = (int)((length + SectorSize - 1) / SectorSize);

    // Where the run began is what the pass names it by, and no two runs began in
    // the same place. Where it happens to be now is not usable as a name: another
    // run may have been laid down there in the meantime.
    var slot = this._slots.FirstOrDefault(s => s.OriginalSector == sourceSector && s.Sectors == sectors)
      ?? this._slots.FirstOrDefault(s => s.OriginalSector == sourceSector)
      ?? this._slots.FirstOrDefault(s => s.Sector == sourceSector && s.Sectors == sectors);
    if (slot == null) return;

    slot.Sector = destinationOffset / SectorSize;
  }

  /// <summary>
  /// Writes every pointer back and re-stamps the node that holds them.
  /// </summary>
  /// <remarks>
  /// A b-tree node's checksum covers all the keys it holds, so this is done once
  /// the whole pass is over: until then the node on disk and the pointers in hand
  /// disagree, and stamping it early would only be undone by the next move.
  /// </remarks>
  public void Settle(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._extentsNodeOffset == 0 || this._slots.Count == 0) return;

    var node = new byte[this._extentsNodeSectors * SectorSize];
    image.Position = this._extentsNodeOffset;
    image.ReadExactly(node);

    foreach (var slot in this._slots) {
      var at = (int)(slot.FieldOffset - this._extentsNodeOffset);
      var word = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(at));
      var device = (byte)((word >> 48) & 0xFF);
      var generation = (byte)((word >> 56) & 0xFF);
      BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(at),
        ExtentPointer(slot.Sector, device, generation));
    }

    var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));
    var end = BcacheFsNodeBuilder.KeysOffset + words * 8;
    var checksum = MetadataChecksum(node.AsSpan(16, end - 16));
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(0), checksum);
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(8), 0);

    image.Position = this._extentsNodeOffset;
    image.Write(node, 0, (end + SectorSize - 1) / SectorSize * SectorSize);
    image.Flush();
  }
}
