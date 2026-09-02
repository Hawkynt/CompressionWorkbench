#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Iso;

/// <summary>
/// Reads ISO 9660 (ECMA-119) disc images with optional Joliet and Rock Ridge support.
/// </summary>
public sealed class IsoReader : IDisposable {
  private const int SectorSize = 2048;
  /// <summary>
  /// Random-access view over the image. Copying the volume into a byte[] capped
  /// the reader at the array limit, which no DVD or Blu-ray image respects.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<IsoEntry> _entries = [];
  private readonly bool _preferJoliet;
  private bool _joliet;

  /// <summary>All entries found in the image.</summary>
  public IReadOnlyList<IsoEntry> Entries => _entries;

  /// <summary>
  /// Opens an ISO 9660 image from the given stream. When <paramref name="useJoliet"/>
  /// is <see langword="true"/> (the default) and the image carries a Joliet
  /// Supplementary Volume Descriptor, the long UCS-2 names from the Joliet
  /// directory tree are returned; otherwise the primary ECMA-119 tree (short
  /// uppercased names) is read.
  /// </summary>
  public IsoReader(Stream stream, bool leaveOpen = false, bool useJoliet = true) {
    _preferJoliet = useJoliet;
    if (stream.CanSeek) stream.Position = 0;
    _data = new ImageAccessor(stream, leaveOpen: true);
    Parse();
  }

  private void Parse() {
    if (_data.Length < 17 * SectorSize)
      throw new InvalidDataException("ISO9660: image too small.");

    // Find PVD and optional Joliet SVD
    int pvdOffset = -1;
    int jolietOffset = -1;

    for (int sector = 16; sector < 256; sector++) {
      var off = sector * SectorSize;
      if (off + SectorSize > _data.Length) break;

      var type = _data.ReadByte(off);
      if (type == 0xFF) break; // terminator

      if (!IsCD001(off)) continue;

      if (type == 1 && pvdOffset < 0)
        pvdOffset = off;
      else if (type == 2 && jolietOffset < 0 && _preferJoliet) {
        // Check escape sequences at offset 88 for Joliet
        var esc = _data.Read(off + 88, 3);
        if (esc[0] == 0x25 && esc[1] == 0x2F && (esc[2] == 0x40 || esc[2] == 0x43 || esc[2] == 0x45))
          jolietOffset = off;
      }
    }

    if (pvdOffset < 0)
      throw new InvalidDataException("ISO9660: no Primary Volume Descriptor found.");

    // Prefer Joliet if available
    int descOff;
    if (jolietOffset >= 0) {
      _joliet = true;
      descOff = jolietOffset;
    } else {
      descOff = pvdOffset;
    }

    // Parse root directory record from chosen descriptor at offset 156
    var rootRec = descOff + 156;
    var rootLba = (int)_data.ReadUInt32(rootRec + 2);
    var rootLen = (int)_data.ReadUInt32(rootRec + 10);

    ReadDirectory(rootLba, rootLen, "");
  }

  private bool IsCD001(int vdOffset) =>
    _data.Length > vdOffset + 5 &&
    _data.ReadByte(vdOffset + 1) == 'C' && _data.ReadByte(vdOffset + 2) == 'D' &&
    _data.ReadByte(vdOffset + 3) == '0' && _data.ReadByte(vdOffset + 4) == '0' && _data.ReadByte(vdOffset + 5) == '1';

  private void ReadDirectory(int lba, int length, string basePath) {
    // 64-bit throughout: a directory extent past 2 GB has an LBA whose product
    // with the sector size overflows int and wraps to a negative offset.
    var offset = (long)lba * SectorSize;
    var end = offset + length;
    if (end > _data.Length) end = _data.Length;
    var pos = offset;

    while (pos < end) {
      var recLen = _data.ReadByte(pos);
      if (recLen == 0) {
        // Skip to next sector boundary
        var nextSector = ((pos / SectorSize) + 1) * SectorSize;
        pos = nextSector;
        continue;
      }
      if (pos + recLen > end) break;

      var extLba = (int)_data.ReadUInt32(pos + 2);
      var dataLen = (int)_data.ReadUInt32(pos + 10);
      var flags = _data.ReadByte(pos + 25);
      var nameLen = _data.ReadByte(pos + 32);
      var isDir = (flags & 2) != 0;

      string name;
      if (_joliet) {
        name = Encoding.BigEndianUnicode.GetString(_data.Read(pos + 33, nameLen));
      } else {
        // Check for Rock Ridge NM entry in System Use area
        var suOffset = 33 + nameLen;
        if ((nameLen & 1) == 0) suOffset++; // padding byte
        name = GetRockRidgeName(pos + suOffset, pos + recLen)
               ?? Encoding.ASCII.GetString(_data.Read(pos + 33, nameLen));
      }

      // Clean up name
      var semi = name.IndexOf(';');
      if (semi >= 0) name = name[..semi];
      name = name.TrimEnd('.');

      // Skip . and .. entries
      if (nameLen == 1 && (_data.ReadByte(pos + 33) == 0 || _data.ReadByte(pos + 33) == 1)) {
        pos += recLen;
        continue;
      }

      if (string.IsNullOrEmpty(name)) {
        pos += recLen;
        continue;
      }

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

      // Date/time: 7 bytes at offset 18
      DateTime? lastMod = null;
      if (pos + 24 < end) {
        var y = _data.ReadByte(pos + 18) + 1900;
        var m = _data.ReadByte(pos + 19);
        var d = _data.ReadByte(pos + 20);
        var h = _data.ReadByte(pos + 21);
        var mi = _data.ReadByte(pos + 22);
        var s = _data.ReadByte(pos + 23);
        if (y >= 1970 && m >= 1 && m <= 12 && d >= 1 && d <= 31)
          lastMod = new DateTime(y, m, d, h, mi, s, DateTimeKind.Utc);
      }

      _entries.Add(new IsoEntry {
        Name = fullPath,
        Size = isDir ? 0 : dataLen,
        IsDirectory = isDir,
        LastModified = lastMod,
        DataOffset = (long)extLba * SectorSize,
      });

      if (isDir)
        ReadDirectory(extLba, dataLen, fullPath);

      pos += recLen;
    }
  }

  private string? GetRockRidgeName(long start, long end) {
    var pos = start;
    while (pos + 4 <= end) {
      var sig0 = _data.ReadByte(pos);
      var sig1 = _data.ReadByte(pos + 1);
      var len = _data.ReadByte(pos + 2);
      if (len < 4) break;
      if (pos + len > end) break;

      if (sig0 == 'N' && sig1 == 'M' && len > 5) {
        var nameLen = len - 5;
        return Encoding.ASCII.GetString(_data.Read(pos + 5, nameLen));
      }
      pos += len;
    }
    return null;
  }

  /// <summary>
  /// Copies an entry's bytes into <paramref name="destination" /> a block at a
  /// time, so an entry larger than a byte[] can hold is extracted like any other.
  /// </summary>
  public void ExtractTo(IsoEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    if (entry.DataOffset + entry.Size > _data.Length) return;
    _data.CopyTo(entry.DataOffset, destination, entry.Size);
  }

  /// <summary>
  /// Extracts the raw data for the given entry.
  /// </summary>
  public byte[] Extract(IsoEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.Read(entry.DataOffset, (int)entry.Size);
  }

  /// <inheritdoc/>
    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
