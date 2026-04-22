#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// Builds a minimal ISO 9660 (ECMA-119) disc image.
/// </summary>
public sealed class IsoWriter {
  private const int SectorSize = 2048;
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to the image.
  /// </summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the complete ISO 9660 image and returns it as a byte array.
  /// </summary>
  public byte[] Build() {
    // Calculate layout
    var rootDirSector = 20;
    // Root directory needs: dot, dotdot, + each file entry
    var rootDirSize = CalculateDirectorySize();
    var rootDirSectors = (rootDirSize + SectorSize - 1) / SectorSize;
    var dataSector = rootDirSector + rootDirSectors;

    // Assign file locations
    var fileLocations = new List<(int Sector, int Size)>();
    var currentSector = dataSector;
    foreach (var (_, data) in _files) {
      fileLocations.Add((currentSector, data.Length));
      currentSector += (data.Length + SectorSize - 1) / SectorSize;
      if (data.Length == 0) currentSector++; // empty file still gets a sector
    }

    var totalSectors = currentSector;
    var image = new byte[totalSectors * SectorSize];

    // Sector 16: Primary Volume Descriptor
    WritePVD(image, totalSectors, rootDirSector, rootDirSize);

    // Sector 17: Terminator
    image[17 * SectorSize] = 0xFF; // type = terminator
    "CD001"u8.CopyTo(image.AsSpan(17 * SectorSize + 1));
    image[17 * SectorSize + 6] = 1; // version

    // Sector 18: L Path Table
    WritePathTable(image, 18 * SectorSize, rootDirSector, littleEndian: true);

    // Sector 19: M Path Table
    WritePathTable(image, 19 * SectorSize, rootDirSector, littleEndian: false);

    // Root directory
    WriteRootDirectory(image, rootDirSector, rootDirSize, fileLocations);

    // File data
    for (var i = 0; i < _files.Count; i++) {
      var (_, data) = _files[i];
      var (sector, _) = fileLocations[i];
      data.CopyTo(image, sector * SectorSize);
    }

    return image;
  }

  private int CalculateDirectorySize() {
    var size = 34 + 34; // dot and dotdot
    foreach (var (name, _) in _files) {
      var idLen = Encoding.ASCII.GetByteCount(name.ToUpperInvariant()) + 2; // ";1"
      var recLen = 33 + idLen;
      if ((recLen & 1) != 0) recLen++; // pad to even
      // Check if record fits in current sector
      var currentSectorUsed = size % SectorSize;
      if (currentSectorUsed + recLen > SectorSize) {
        size += SectorSize - currentSectorUsed; // pad to next sector
      }
      size += recLen;
    }
    // Pad to sector boundary
    if (size % SectorSize != 0)
      size += SectorSize - (size % SectorSize);
    if (size == 0) size = SectorSize;
    return size;
  }

  private void WritePVD(byte[] image, int totalSectors, int rootDirSector, int rootDirSize) {
    var off = 16 * SectorSize;
    image[off] = 1; // type = PVD
    "CD001"u8.CopyTo(image.AsSpan(off + 1));
    image[off + 6] = 1; // version

    // Volume identifier (offset 40, 32 bytes)
    PadString(image, off + 40, 32, "CDROM");

    // Volume space size (offset 80: LE uint32, offset 84: BE uint32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 80), (uint)totalSectors);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 84), (uint)totalSectors);

    // Volume set size (offset 120: LE uint16 + BE uint16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 120), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 122), 1);

    // Volume sequence number (offset 124)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 124), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 126), 1);

    // Logical block size (offset 128: LE uint16 + BE uint16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 128), SectorSize);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 130), SectorSize);

    // Path table size (offset 132: LE + BE) - our path table has one entry: root
    var pathTableSize = 10; // 1 byte name length + 1 byte ext attr + 4 byte extent + 2 byte parent + 2 byte name (padded)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 132), (uint)pathTableSize);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 136), (uint)pathTableSize);

    // L path table location (offset 140, LE uint32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 140), 18);
    // M path table location (offset 148, BE uint32)
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 148), 19);

    // Root directory record (offset 156, 34 bytes)
    WriteDirectoryRecord(image, off + 156, rootDirSector, rootDirSize, 0x02, [0]);
  }

  private static void WriteDirectoryRecord(byte[] image, int off, int lba, int size, byte flags, byte[] identifier) {
    var idLen = identifier.Length;
    var recLen = 33 + idLen;
    if ((recLen & 1) != 0) recLen++;

    image[off] = (byte)recLen;
    // Extended attribute record length = 0
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 2), (uint)lba);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 6), (uint)lba);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 10), (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 14), (uint)size);

    // Date/time (7 bytes at offset 18): simple current date
    var now = DateTime.UtcNow;
    image[off + 18] = (byte)(now.Year - 1900);
    image[off + 19] = (byte)now.Month;
    image[off + 20] = (byte)now.Day;
    image[off + 21] = (byte)now.Hour;
    image[off + 22] = (byte)now.Minute;
    image[off + 23] = (byte)now.Second;
    image[off + 24] = 0; // GMT offset

    image[off + 25] = flags;
    // File unit size, interleave gap = 0
    // Volume sequence number
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 28), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 30), 1);

    image[off + 32] = (byte)idLen;
    identifier.CopyTo(image, off + 33);
  }

  private void WriteRootDirectory(byte[] image, int rootSector, int rootDirSize, List<(int Sector, int Size)> fileLocations) {
    var baseOff = rootSector * SectorSize;
    var pos = baseOff;

    // . entry (self)
    WriteDirectoryRecord(image, pos, rootSector, rootDirSize, 0x02, [0]);
    pos += image[pos]; // advance by record length

    // .. entry (parent = self for root)
    WriteDirectoryRecord(image, pos, rootSector, rootDirSize, 0x02, [1]);
    pos += image[pos];

    // File entries
    for (var i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var (sector, _) = fileLocations[i];
      var identifier = Encoding.ASCII.GetBytes(name.ToUpperInvariant() + ";1");
      var recLen = 33 + identifier.Length;
      if ((recLen & 1) != 0) recLen++;

      // Check if fits in current sector
      var sectorOffset = (pos - baseOff) % SectorSize;
      if (sectorOffset + recLen > SectorSize) {
        // Pad to next sector
        pos += SectorSize - sectorOffset;
      }

      WriteDirectoryRecord(image, pos, sector, data.Length, 0x00, identifier);
      pos += recLen;
    }
  }

  private static void WritePathTable(byte[] image, int offset, int rootDirSector, bool littleEndian) {
    // Root entry: name length=1, ext attr=0, extent location, parent=1, name=\x01 (root)
    image[offset] = 1; // name length
    image[offset + 1] = 0; // ext attr length
    if (littleEndian)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 2), (uint)rootDirSector);
    else
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 2), (uint)rootDirSector);
    if (littleEndian)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 6), 1);
    else
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 6), 1);
    image[offset + 8] = 1; // root directory identifier (padding to even)
    // 10 bytes total
  }

  private static void PadString(byte[] image, int offset, int length, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    Array.Fill(image, (byte)0x20, offset, length); // fill with spaces
    Array.Copy(bytes, 0, image, offset, Math.Min(bytes.Length, length));
  }
}
