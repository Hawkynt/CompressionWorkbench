#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Adfs;

/// <summary>
/// Moves a file's sectors inside an old-map ADFS disc and rewrites the
/// directory entry that named them.
/// </summary>
/// <remarks>
/// <para>An old-map file is one contiguous run, and its directory entry holds
/// the sector it starts at in three bytes. A move is therefore the copy plus
/// those three bytes and the directory's check byte.</para>
///
/// <para>The free-space map is not written per move. It is a list of regions
/// rather than a bit per sector, and one run's old home is routinely where
/// another has just landed, so it is written once from the finished layout —
/// which is also the only way to keep the list inside the 82 regions the old
/// map holds.</para>
/// </remarks>
public sealed class AdfsBlockMover : IFilesystemBlockMover {

  private const int DirectoryCheckByteOffset = 0x4FD;
  private const int FreeMapCheckByteOffset = 0xFF;

  private long _imageLength;

  /// <summary>Set when the disc carries the new map, in which case it says where everything is.</summary>
  private AdfsNewMap.Layout? _newMap;

  /// <summary>Where each fragment has got to, as the pass moves them about.</summary>
  private readonly List<(uint Id, int FirstSector, int Sectors)> _fragments = [];

  /// <summary>Which fragment each file is, which is what the directory says.</summary>
  private readonly Dictionary<string, uint> _fragmentOfName = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Reads the map, which on a new-map disc is where the fragments are.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._imageLength = image.Length;
    this._fragments.Clear();

    this._fragmentOfName.Clear();
    this._newMap = AdfsExtentMap.IsOldMap(image) ? null : AdfsNewMap.TryRead(image);
    if (this._newMap == null) return;

    // Free fragments are left out: they are not runs that move, and the map is
    // written back with free space worked out from what the others leave over.
    this._fragments.AddRange(this._newMap.Fragments.Where(f => f.Id != 0));
    foreach (var (id, name) in AdfsExtentMap.NewMapNames(image, this._newMap))
      this._fragmentOfName[name] = id;
  }

  /// <summary>A sector, which is what the map addresses.</summary>
  public int BlockSize => this._newMap?.SectorSize ?? AdfsExtentMap.SectorSize;

  /// <summary>
  /// First byte a file may occupy. On an old-map disc that is past the free map
  /// and the root directory; on a new-map disc it is past the zone the map
  /// itself lives in.
  /// </summary>
  public long FirstDataByte {
    get {
      if (this._newMap == null)
        return (long)AdfsExtentMap.FirstDataSector * AdfsExtentMap.SectorSize;

      var map = this._newMap.Fragments.FirstOrDefault(f => f.Id == AdfsNewMap.MapFragment);
      return (long)(map.FirstSector + map.Sectors) * this._newMap.SectorSize;
    }
  }

  /// <summary>
  /// Each call rewrites the entry naming the run it is given. An old-map file
  /// is never in more than one piece, so one call is the whole of it.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the disc while the rest of the layout moves,
  /// which is what lets a full disc be rearranged at all.
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
    if (this._imageLength == 0) this.Init(image);

    if (newOffset % this.BlockSize != 0)
      throw new NotSupportedException(
        $"ADFS: {newOffset} is not on a {this.BlockSize}-byte sector boundary, which is all the " +
        "map can name.");

    var oldSector = (uint)(oldOffset / AdfsExtentMap.SectorSize);
    var newSector = (uint)(newOffset / AdfsExtentMap.SectorSize);
    if (oldSector == newSector) return;
    if (this._newMap != null) {
      this.MoveFragment(newOffset, fileName);
      return;
    }

    if (newSector > 0xFFFFFF)
      throw new NotSupportedException(
        $"ADFS: sector {newSector} is past the three bytes a directory entry holds.");

    var directory = AdfsModifier.ReadDirectory(image);
    var entry = -1;
    foreach (var (_, start, _, at) in AdfsExtentMap.Files(image))
      if (start == oldSector) { entry = at; break; }

    if (entry < 0)
      throw new InvalidOperationException(
        $"ADFS: no directory entry starts at sector {oldSector}, so '{fileName}' cannot be repointed.");

    AdfsModifier.WriteUInt24LittleEndian(directory.AsSpan(entry + 0x16, 3), newSector);
    RecomputeDirectoryCheckByte(directory);
    AdfsModifier.WriteDirectory(image, directory);
    image.Flush();
  }

  /// <summary>
  /// Notes a fragment's new home. Nothing is written until the pass is over:
  /// while runs are still moving, two fragments can hold the same sectors
  /// between them, and a bitmap written from that would describe an overlap.
  /// </summary>
  private void MoveFragment(long newOffset, string fileName) {
    var to = (int)(newOffset / this._newMap!.SectorSize);

    // By name rather than by where it currently sits: a run can be held out of
    // the disc entirely while another takes the sectors it came from, and two
    // fragments claiming one sector is exactly what that would look like.
    if (!this._fragmentOfName.TryGetValue(fileName, out var id))
      throw new InvalidOperationException(
        $"ADFS: the directory names no fragment for '{fileName}', so it cannot be repointed.");

    for (var i = 0; i < this._fragments.Count; ++i) {
      if (this._fragments[i].Id != id) continue;
      this._fragments[i] = this._fragments[i] with { FirstSector = to };
      return;
    }

    throw new InvalidOperationException(
      $"ADFS: the map holds no fragment {id}, so '{fileName}' cannot be repointed.");
  }

  /// <summary>
  /// Writes the free-space map from the runs the disc actually holds.
  /// </summary>
  /// <remarks>
  /// Called once a layout pass has finished. The old map lists free regions,
  /// not sectors, so it cannot be edited a run at a time without either
  /// describing space twice or splintering into more regions than the two
  /// sectors hold. From the finished layout the answer is simply what is left
  /// over.
  /// </remarks>
  public void SettleFreeMap(Stream image, IEnumerable<(long Offset, long Length)> live) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(live);
    if (this._imageLength == 0) this.Init(image);

    // A new-map disc keeps free space in the same bitmap as everything else,
    // so writing the fragments back is what frees what they no longer cover.
    if (this._newMap != null) {
      AdfsNewMap.Write(image, this._newMap, this._fragments);
      return;
    }

    var totalSectors = (uint)(this._imageLength / AdfsExtentMap.SectorSize);
    var taken = new bool[totalSectors];
    for (var sector = 0u; sector < AdfsExtentMap.FirstDataSector && sector < totalSectors; ++sector)
      taken[sector] = true;

    foreach (var (offset, length) in live) {
      if (length <= 0) continue;
      var first = offset / AdfsExtentMap.SectorSize;
      var last = (offset + length + AdfsExtentMap.SectorSize - 1) / AdfsExtentMap.SectorSize;
      for (var sector = first; sector < last && sector < totalSectors; ++sector)
        if (sector >= 0) taken[sector] = true;
    }

    var free = new List<AdfsModifier.FreeRegion>();
    for (var sector = (uint)AdfsExtentMap.FirstDataSector; sector < totalSectors; ++sector) {
      if (taken[sector]) continue;
      var start = sector;
      while (sector < totalSectors && !taken[sector]) ++sector;
      free.Add(new AdfsModifier.FreeRegion(start, sector - start));
    }

    var freeMap0 = AdfsModifier.ReadSector(image, 0);
    var freeMap1 = AdfsModifier.ReadSector(image, 1);
    AdfsModifier.EncodeFsm(freeMap0, freeMap1, free);
    freeMap0[FreeMapCheckByteOffset] = AdfsModifier.ComputeOldMapCheckByte(freeMap0);
    freeMap1[FreeMapCheckByteOffset] = AdfsModifier.ComputeOldMapCheckByte(freeMap1);
    AdfsModifier.WriteSector(image, 0, freeMap0);
    AdfsModifier.WriteSector(image, 1, freeMap1);
    image.Flush();
  }

  /// <summary>The directory carries a check byte over everything before it.</summary>
  private static void RecomputeDirectoryCheckByte(byte[] directory) {
    byte check = 0;
    for (var i = 0; i < DirectoryCheckByteOffset; ++i) check ^= directory[i];
    directory[DirectoryCheckByteOffset] = check;
  }
}
