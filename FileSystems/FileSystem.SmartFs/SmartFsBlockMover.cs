#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.SmartFs;

/// <summary>
/// Moves a SmartFS sector to another place in the volume and rewrites whatever
/// named it.
/// </summary>
/// <remarks>
/// <para>A file is a chain: its directory entry names the first sector, and
/// each sector's chain header names the one after it. So a sector may sit
/// anywhere, and moving one is the copy plus the single field that pointed at
/// it — plus the logical number in the sector's own header, which this keeps
/// equal to the physical position the way a freshly formatted volume does.</para>
///
/// <para>Sectors the volume no longer uses are left erased once the pass is
/// over, which is what a flash sector that holds nothing looks like.</para>
/// </remarks>
public sealed class SmartFsBlockMover : IFilesystemBlockMover {

  private SmartFsExtentMap.Volume? _volume;

  /// <summary>Where the field naming each sector lives, as the pass moves them about.</summary>
  private readonly Dictionary<int, long> _pointedAtFrom = [];

  /// <summary>Reads the volume once and notes which field names each sector.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._volume = SmartFsExtentMap.Read(image);
    this._pointedAtFrom.Clear();
    if (this._volume == null)
      throw new InvalidDataException("SmartFS: the volume does not carry a format sector this reads.");

    foreach (var (sector, at) in this._volume.PointedAtFrom)
      this._pointedAtFrom[sector] = at;
  }

  /// <summary>A sector, which is the unit everything here is counted in.</summary>
  public int BlockSize => this._volume?.SectorSize ?? 0;

  /// <summary>First byte a file's sector may occupy: past the format sector and the root.</summary>
  public long FirstDataByte => (long)SmartFsLayout.FirstDataSector * (this._volume?.SectorSize ?? 0);

  /// <summary>
  /// Each call rewrites the fields naming the sectors it is given, so a file
  /// scattered over the volume is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A sector may be held outside the volume while the rest of the layout
  /// moves, which is what lets a full volume be rearranged at all.
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

    var sectorSize = this._volume!.SectorSize;
    if (newOffset % sectorSize != 0)
      throw new NotSupportedException(
        $"SmartFS: {newOffset} is not on a {sectorSize}-byte sector boundary, which is all a chain " +
        "field can name.");

    var from = (int)(oldOffset / sectorSize);
    var to = (int)(newOffset / sectorSize);
    if (from == to) return;

    var count = (int)((length + sectorSize - 1) / sectorSize);
    if (to + count > this._volume.TotalSectors)
      throw new NotSupportedException(
        $"SmartFS: sector {to + count - 1} is past the {this._volume.TotalSectors} the volume holds.");

    // Work out every rewrite the whole run needs before making any of them: a
    // run's own sectors point at each other, and a field patched early would
    // otherwise be read back as if it had always said that.
    var rewrites = new List<(long At, ushort Value)>();
    for (var k = 0; k < count; ++k) {
      if (!this._pointedAtFrom.TryGetValue(from + k, out var site))
        throw new InvalidOperationException(
          $"SmartFS: nothing names sector {from + k}, so '{fileName}' cannot be repointed.");

      rewrites.Add((site, (ushort)(to + k)));
    }

    foreach (var (site, value) in rewrites) {
      // A field inside the run travels with it.
      var moved = site >= (long)from * sectorSize && site < (long)(from + count) * sectorSize
        ? site - (long)from * sectorSize + (long)to * sectorSize
        : site;
      WriteUInt16(image, moved, value);
    }

    // The sector's own header says which logical sector it holds, and a volume
    // that has never been wear-levelled keeps that equal to where it sits.
    for (var k = 0; k < count; ++k)
      WriteUInt16(image, (long)(to + k) * sectorSize, (ushort)(to + k));

    // Re-key: the fields that lived inside the run are now at its new home, and
    // the sectors they name have moved with it.
    var moves = new Dictionary<int, long>();
    foreach (var (sector, site) in this._pointedAtFrom) {
      var newSite = site >= (long)from * sectorSize && site < (long)(from + count) * sectorSize
        ? site - (long)from * sectorSize + (long)to * sectorSize
        : site;
      var newSector = sector >= from && sector < from + count ? sector - from + to : sector;
      moves[newSector] = newSite;
    }

    this._pointedAtFrom.Clear();
    foreach (var (sector, site) in moves) this._pointedAtFrom[sector] = site;
    image.Flush();
  }

  /// <summary>
  /// Erases every sector the volume no longer uses.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. A sector left with the bytes of
  /// whatever used to be there still says it holds a logical sector, and two
  /// sectors claiming one logical number is what a driver reads as a volume
  /// that needs recovering. Erased is all-ones, which is what unwritten flash
  /// reads as.
  /// </remarks>
  public void SettleFreeSectors(Stream image, IEnumerable<(long Offset, long Length)> live) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(live);
    if (this._volume == null) this.Init(image);

    var sectorSize = this._volume!.SectorSize;
    var used = new bool[this._volume.TotalSectors];
    foreach (var (offset, length) in live) {
      if (length <= 0) continue;
      var first = offset / sectorSize;
      var last = (offset + length + sectorSize - 1) / sectorSize;
      for (var sector = first; sector < last && sector < used.Length; ++sector)
        if (sector >= 0) used[sector] = true;
    }

    var erased = new byte[sectorSize];
    Array.Fill(erased, (byte)0xFF);
    for (var sector = SmartFsLayout.FirstDataSector; sector < used.Length; ++sector) {
      if (used[sector]) continue;
      image.Position = (long)sector * sectorSize;
      image.Write(erased, 0, sectorSize);
    }

    image.Flush();
  }

  private static void WriteUInt16(Stream image, long at, ushort value) {
    Span<byte> field = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(field, value);
    image.Position = at;
    image.Write(field);
  }
}
