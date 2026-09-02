#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Hammer;

/// <summary>
/// Moves a file's data records inside a HAMMER volume and repoints the B-tree
/// elements that name them.
/// </summary>
/// <remarks>
/// <para>A HAMMER file's bytes live in data records, and each record's B-tree
/// leaf element carries the zone offset its bytes start at. Moving a record is
/// the copy, that eight-byte field, and the checksum over the node the element
/// lives in — which covers the element, so leaving it stale would describe a
/// node that no longer exists.</para>
///
/// <para>The element is found by the offset it still names rather than by the
/// file's path, so a file with several records can be moved one record at a
/// time.</para>
///
/// <para>The freemap is not touched. It accounts per eight-megabyte big-block —
/// which zone owns each and how far into it the allocator has appended — so a
/// record moved inside the area the freemap already accounts for leaves it
/// telling the truth. A destination outside that area is refused rather than
/// silently leaving the freemap describing a volume that no longer exists.</para>
/// </remarks>
public sealed class HammerBlockMover : IFilesystemBlockMover {

  /// <summary>Where each run started, and the B-tree element that names it.</summary>
  private readonly Dictionary<long, (long ElementOffset, long NodeOffset)> _elementOf = [];

  /// <summary>Offset of the data offset inside a B-tree leaf element.</summary>
  private const int ElementDataOffset = 48;

  /// <summary>Bytes of one B-tree node, header and elements together.</summary>
  private const int NodeOndiskSize = 64 + 63 * 64;

  /// <summary>Volume format version the checksums are computed for.</summary>
  private const uint VolVersion = 7;

  /// <summary>Mask of the short offset inside a zone offset.</summary>
  private const ulong OffShortMask = 0x000FFFFFFFFFFFFFUL;

  private long _volumeBufferStart;
  private long _firstDataByte;
  private long _allocatedEnd;

  /// <summary>Reads where the buffer area starts and how far the records reach.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var reader = HammerReader.Open(image);
    if (!reader.Valid)
      throw new InvalidDataException("HAMMER: the volume header does not parse.");

    this._volumeBufferStart = reader.VolumeBufferStart;

    // Which element names which run is settled here, before anything moves, and
    // keyed by where the run started. Searching the live volume for the element
    // that still points at an address finds whatever has since been laid down
    // there instead, and repoints that other file: two files of one length come
    // back holding each other's bytes, right length, nothing raised.
    this._elementOf.Clear();
    var first = long.MaxValue;
    foreach (var extent in reader.EnumerateDataExtents()) {
      if (extent.Length > 0) first = Math.Min(first, extent.Offset);
      this._elementOf[extent.Offset] = (extent.ElementOffset, extent.NodeOffset);
    }
    this._firstDataByte = first == long.MaxValue ? this._volumeBufferStart : first;

    // How far the freemap accounts for the volume: each big-block it hands to a
    // zone is accounted for up to that zone's append point, and a record may be
    // put anywhere inside that. Past it the freemap would have to be changed
    // too, which this does not do — so a destination there is refused rather
    // than left describing a volume that no longer exists.
    image.Position = 0;
    var end = this._volumeBufferStart;
    foreach (var extent in HammerExtentMap.Enumerate(image))
      end = Math.Max(end, extent.Offset + extent.Length);
    this._allocatedEnd = end;
  }

  /// <summary>
  /// Sixty-four bytes. A record's zone offset is byte-exact, but keeping the
  /// destinations on the element grid keeps a record from straddling the
  /// sixteen-byte structures the format aligns on.
  /// </summary>
  public int BlockSize => 64;

  /// <summary>First byte a record may occupy: past the volume header and reserves.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the run it is given and nothing else, so an owner
  /// scattered over several runs is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._volumeBufferStart == 0) this.Init(image);
    if (oldOffset == newOffset) return;

    if (newOffset < this._volumeBufferStart)
      throw new NotSupportedException(
        $"HAMMER: {newOffset} is ahead of the buffer area, which is where records live.");
    if (newOffset + length > this._allocatedEnd)
      throw new NotSupportedException(
        "HAMMER: the destination is past what the freemap accounts for, and the freemap is not " +
        "rewritten here.");

    if (this._elementOf.Count == 0) this.Init(image);
    long elementOffset = -1;
    long nodeOffset = -1;
    if (this._elementOf.TryGetValue(oldOffset, out var record)) {
      elementOffset = record.ElementOffset;
      nodeOffset = record.NodeOffset;
    }

    if (elementOffset < 0)
      throw new InvalidOperationException(
        $"HAMMER: no B-tree element names {oldOffset}, so '{fileName}' cannot be repointed.");

    // Keep the zone the record already lives in; only the short offset — the
    // device offset relative to the buffer area — changes.
    Span<byte> field = stackalloc byte[8];
    image.Position = elementOffset + ElementDataOffset;
    image.ReadExactly(field);
    var zoneBits = BinaryPrimitives.ReadUInt64LittleEndian(field) & ~OffShortMask;
    var shortOffset = (ulong)(newOffset - this._volumeBufferStart);
    if ((shortOffset & ~OffShortMask) != 0)
      throw new NotSupportedException(
        $"HAMMER: {newOffset} is past what a zone offset's short field holds.");

    BinaryPrimitives.WriteUInt64LittleEndian(field, zoneBits | shortOffset);
    image.Position = elementOffset + ElementDataOffset;
    image.Write(field);

    RestampNode(image, nodeOffset);
    image.Flush();
  }

  /// <summary>
  /// Recomputes the checksum over a B-tree node. It covers everything past the
  /// checksum itself, so rewriting an element inside the node invalidates it.
  /// </summary>
  private static void RestampNode(Stream image, long nodeOffset) {
    if (nodeOffset < 0 || nodeOffset + NodeOndiskSize > image.Length) return;

    var node = new byte[NodeOndiskSize];
    image.Position = nodeOffset;
    image.ReadExactly(node);

    var crc = HammerCrc.DataCrc(VolVersion, node.AsSpan(4, NodeOndiskSize - 4));
    Span<byte> stamp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(stamp, crc);
    image.Position = nodeOffset;
    image.Write(stamp);
  }
}
