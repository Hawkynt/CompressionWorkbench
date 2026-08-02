#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.MinixV1;

/// <summary>
/// Moves a file's zones inside a Minix V1 volume, repoints the pointers that
/// name them, and moves the allocation with them.
/// </summary>
/// <remarks>
/// <para>A Minix file's bytes are addressed one zone at a time by two-byte
/// pointers — seven in the inode, then as many as the indirect blocks below it
/// hold. Moving a run of them is the copy, the pointers that named it, and the
/// bit per zone that says whether it is taken. Without the bitmap half, the
/// next file added would be allocated straight on top of one that had
/// moved.</para>
///
/// <para>A run is only ever reported when its zones and its pointers are both
/// consecutive, which is what lets the whole run be repointed by counting
/// forward from the first.</para>
/// </remarks>
public sealed class MinixV1BlockMover : IFilesystemBlockMover {

  private long _bitmapOffset;
  private long _firstDataZone;
  private long _firstDataByte;
  private long _volumeZones;

  /// <summary>Reads the geometry and where file data may start.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var reader = new MinixV1Reader(image);
    this._bitmapOffset = reader.ZoneBitmapOffset;
    this._firstDataByte = reader.FirstDataZoneOffset;
    this._firstDataZone = this._firstDataByte / MinixV1Reader.ZoneSize;
    this._volumeZones = image.Length / MinixV1Reader.ZoneSize;
  }

  /// <summary>A zone, which is a block at the zone size this writer uses.</summary>
  public int BlockSize => MinixV1Reader.ZoneSize;

  /// <summary>First byte a file may occupy: past the bitmaps and the inode table.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
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
    if (this._firstDataByte == 0) this.Init(image);

    var zoneSize = MinixV1Reader.ZoneSize;
    if (newOffset % zoneSize != 0)
      throw new NotSupportedException(
        $"Minix: {newOffset} is not on a {zoneSize}-byte zone boundary, which is all a zone " +
        "pointer can name.");

    var oldZone = oldOffset / zoneSize;
    var newZone = newOffset / zoneSize;
    if (oldZone == newZone) return;
    if (newZone > ushort.MaxValue)
      throw new NotSupportedException(
        $"Minix: zone {newZone} is past the 65535 a sixteen-bit pointer holds.");

    var zones = (int)((length + zoneSize - 1) / zoneSize);
    var pointerOffset = this.FindPointerNaming(image, oldOffset, zones);
    if (pointerOffset < 0)
      throw new InvalidOperationException(
        $"Minix: no pointer run names zone {oldZone}, so '{fileName}' cannot be repointed.");

    Span<byte> pointer = stackalloc byte[2];
    for (var i = 0; i < zones; ++i) {
      BinaryPrimitives.WriteUInt16LittleEndian(pointer, (ushort)(newZone + i));
      image.Position = pointerOffset + (long)i * 2;
      image.Write(pointer);
    }

    // The bitmap says which zones are taken; leaving it behind would let the
    // next file added to the volume be allocated straight on top of this one.
    for (var i = 0; i < zones; ++i) {
      this.SetBit(image, oldZone + i, taken: false);
      this.SetBit(image, newZone + i, taken: true);
    }
    image.Flush();
  }

  /// <summary>
  /// The byte offset of the first pointer of the run that starts at
  /// <paramref name="offset" /> and covers <paramref name="zones" />, or -1.
  /// </summary>
  private long FindPointerNaming(Stream image, long offset, int zones) {
    image.Position = 0;
    using var reader = new MinixV1Reader(image);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      foreach (var (runOffset, runLength, pointerOffset) in reader.EnumerateDataExtents(entry)) {
        if (runOffset != offset) continue;
        if (runLength < (long)zones * MinixV1Reader.ZoneSize) continue;
        return pointerOffset;
      }
    }
    return -1;
  }

  /// <summary>
  /// Flips a zone's allocation bit. Minix numbers the bitmap from the first
  /// data zone with bit zero reserved, so a zone's bit is one past its distance
  /// from that first zone.
  /// </summary>
  private void SetBit(Stream image, long zone, bool taken) {
    if (zone < this._firstDataZone || zone >= this._volumeZones) return;

    var bit = zone - this._firstDataZone + 1;
    var at = this._bitmapOffset + bit / 8;
    if (at < 0 || at >= image.Length) return;

    image.Position = at;
    var current = image.ReadByte();
    if (current < 0) return;

    var mask = 1 << (int)(bit % 8);
    var updated = taken ? current | mask : current & ~mask;
    if (updated == current) return;

    image.Position = at;
    image.WriteByte((byte)updated);
  }
}
