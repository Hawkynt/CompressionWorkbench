#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Lif;

/// <summary>
/// In-place HP LIF block mover. Moves sector-aligned extents within a LIF image
/// and patches the directory entry's start-sector field so the file remains
/// reachable at its new location.
///
/// <para>LIF files are stored contiguously. Each 32-byte directory entry records
/// the name (10 bytes), file type (u16 BE at +10), start sector (u32 BE at +12),
/// and sector count (u32 BE at +16). Updating the start-sector field is
/// sufficient to redirect the file.</para>
/// </summary>
public sealed class LifBlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;

  private int _dirStartSector;
  private int _dirSectors;
  private int _firstDataSector;

  /// <summary>
  /// Initialises the mover by parsing the LIF volume header.
  /// Must be called before any move operations.
  /// </summary>
  public void Init(Stream image) {
    var buf = new byte[32];
    image.Position = 0;
    image.ReadExactly(buf);

    var magic = BinaryPrimitives.ReadUInt16BigEndian(buf);
    if (magic != LifReader.LifMagic)
      throw new InvalidDataException($"LIF: bad magic 0x{magic:X4}.");

    _dirStartSector = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8));
    _dirSectors = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16));
    _firstDataSector = _dirStartSector + _dirSectors;
  }

  /// <summary>Byte offset where the data region begins (past directory).</summary>
  public long DataOrigin => (long)_firstDataSector * SectorSize;

  /// <summary>Allocation unit size (one 256-byte sector).</summary>
  public int UnitSize => SectorSize;

  /// <summary>Converts a byte offset to a sector number.</summary>
  public int OffsetToSector(long offset) => (int)(offset / SectorSize);

  /// <summary>Converts a sector number to a byte offset.</summary>
  public long SectorToOffset(int sector) => (long)sector * SectorSize;

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
    var oldSector = OffsetToSector(oldOffset);
    var newSector = OffsetToSector(newOffset);

    // Read the full directory area.
    var dirByteOffset = (long)_dirStartSector * SectorSize;
    var dirBytes = _dirSectors * SectorSize;
    var dir = new byte[dirBytes];
    image.Position = dirByteOffset;
    image.ReadExactly(dir);

    var entriesPerSector = SectorSize / 32;
    var totalEntries = _dirSectors * entriesPerSector;
    var sanitized = SanitizeName(fileName);

    for (var i = 0; i < totalEntries; i++) {
      var off = i * 32;
      if (off + 32 > dir.Length) break;
      var first = dir[off];
      if (first == 0xFF) break;          // end of directory
      if (first == 0x00 || first == ' ') continue; // empty/deleted

      var name = Encoding.ASCII.GetString(dir, off, 10).TrimEnd(' ', '\0');
      var startSec = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(off + 12));

      if (startSec == oldSector &&
          (string.Equals(name.TrimEnd(), sanitized.TrimEnd(), StringComparison.Ordinal) ||
           fileName == "*")) {
        BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(off + 12), (uint)newSector);
        // Write back the modified directory.
        image.Position = dirByteOffset;
        image.Write(dir);
        // Crash barrier: metadata commit durable before return.
        image.Flush();
        return;
      }
    }
  }

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "";
    var s = raw;
    if (s.Length > 10) s = s[..10];
    return s;
  }
}
