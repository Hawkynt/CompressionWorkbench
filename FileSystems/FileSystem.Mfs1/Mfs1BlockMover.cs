#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Mfs1;

/// <summary>
/// Moves a file's sectors inside an MFS-1 disk and repoints its catalog slot.
/// </summary>
/// <remarks>
/// <para>A file here is one run of sectors and its catalog slot records where
/// that run starts — ten bits of it, split between a whole byte and the low two
/// bits of the byte before. So relocating a file is the copy plus those two
/// writes.</para>
///
/// <para>The slot is found by the sector it still names rather than by the
/// file's name, so two slots sharing a name cannot send the wrong one
/// somewhere.</para>
/// </remarks>
public sealed class Mfs1BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the packed high-bits byte inside a catalog slot.</summary>
  private const int SlotPackedOffset = 6;

  /// <summary>Offset of the low start-sector byte inside a catalog slot.</summary>
  private const int SlotStartLowOffset = 7;

  private long _slotBase;
  private int _slotCount;

  /// <summary>Reads how many catalog slots this disk carries.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var sectorSize = Mfs1Reader.SectorSize;
    if (image.Length < 2L * sectorSize)
      throw new InvalidDataException("MFS-1: the image is too small to hold a catalog.");

    image.Position = sectorSize + 5;
    var entriesTimesEight = image.ReadByte();
    if (entriesTimesEight < 0 || entriesTimesEight % 8 != 0)
      throw new InvalidDataException("MFS-1: the catalog's entry count is malformed.");

    this._slotCount = entriesTimesEight / 8;
    this._slotBase = sectorSize + 8;
  }

  /// <summary>A sector. A catalog slot names a sector, not a byte.</summary>
  public int BlockSize => Mfs1Reader.SectorSize;

  /// <summary>First byte a file may occupy: past the two catalog sectors.</summary>
  public long FirstDataByte => (long)Mfs1ExtentMap.CatalogSectors * Mfs1Reader.SectorSize;

  /// <summary>
  /// Each call repoints the slot it is given and nothing else, so an owner in
  /// several runs — which this format cannot produce — would be several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full disk be rearranged at all.
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
    if (this._slotCount == 0) this.Init(image);

    var sectorSize = Mfs1Reader.SectorSize;
    if (newOffset % sectorSize != 0)
      throw new NotSupportedException(
        $"MFS-1: {newOffset} is not on a {sectorSize}-byte sector boundary, which is all a " +
        "catalog slot can name.");

    var oldSector = oldOffset / sectorSize;
    var newSector = newOffset / sectorSize;
    if (oldSector == newSector) return;
    if (newSector > 0x3FF)
      throw new NotSupportedException(
        $"MFS-1: sector {newSector} is past the 1023 a catalog slot's ten bits hold.");

    var slot = new byte[8];
    for (var i = 0; i < this._slotCount; ++i) {
      var at = this._slotBase + (long)i * 8;
      if (at + 8 > image.Length) break;

      image.Position = at;
      image.ReadExactly(slot);
      var start = ((slot[SlotPackedOffset] & 0x03) << 8) | slot[SlotStartLowOffset];
      if (start != oldSector) continue;

      var startHighBits = (byte)((uint)(newSector >> 8) & 0x03u);
      slot[SlotStartLowOffset] = (byte)((uint)newSector & 0xFFu);
      slot[SlotPackedOffset] = (byte)((slot[SlotPackedOffset] & 0xFCu) | startHighBits);
      image.Position = at + SlotPackedOffset;
      image.Write(slot.AsSpan(SlotPackedOffset, 2));
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"MFS-1: no catalog slot names sector {oldSector}, so '{fileName}' cannot be repointed.");
  }
}
