#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Iso;

/// <summary>
/// Reads ISO 9660 (ECMA-119) disc images with optional Joliet and Rock Ridge support.
/// </summary>
public sealed class IsoReader : IDisposable {
  private const int SectorSize = 2048;
  private readonly byte[] _data;
  private readonly List<IsoEntry> _entries = [];
  private bool _joliet;

  /// <summary>All entries found in the image.</summary>
  public IReadOnlyList<IsoEntry> Entries => _entries;

  /// <summary>
  /// Opens an ISO 9660 image from the given stream.
  /// </summary>
  public IsoReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
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

      var type = _data[off];
      if (type == 0xFF) break; // terminator

      if (!IsCD001(off)) continue;

      if (type == 1 && pvdOffset < 0)
        pvdOffset = off;
      else if (type == 2 && jolietOffset < 0) {
        // Check escape sequences at offset 88 for Joliet
        var esc = _data.AsSpan(off + 88, 3);
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
    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(rootRec + 2));
    var rootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(rootRec + 10));

    ReadDirectory(rootLba, rootLen, "");
  }

  private bool IsCD001(int vdOffset) =>
    _data.Length > vdOffset + 5 &&
    _data[vdOffset + 1] == 'C' && _data[vdOffset + 2] == 'D' &&
    _data[vdOffset + 3] == '0' && _data[vdOffset + 4] == '0' && _data[vdOffset + 5] == '1';

  private void ReadDirectory(int lba, int length, string basePath) {
    var offset = lba * SectorSize;
    var end = offset + length;
    if (end > _data.Length) end = _data.Length;
    var pos = offset;

    while (pos < end) {
      var recLen = _data[pos];
      if (recLen == 0) {
        // Skip to next sector boundary
        var nextSector = ((pos / SectorSize) + 1) * SectorSize;
        pos = nextSector;
        continue;
      }
      if (pos + recLen > end) break;

      var extLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 2));
      var dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 10));
      var flags = _data[pos + 25];
      var nameLen = _data[pos + 32];
      var isDir = (flags & 2) != 0;

      string name;
      if (_joliet) {
        name = Encoding.BigEndianUnicode.GetString(_data, pos + 33, nameLen);
      } else {
        // Check for Rock Ridge NM entry in System Use area
        var suOffset = 33 + nameLen;
        if ((nameLen & 1) == 0) suOffset++; // padding byte
        name = GetRockRidgeName(pos + suOffset, pos + recLen)
               ?? Encoding.ASCII.GetString(_data, pos + 33, nameLen);
      }

      // Clean up name
      var semi = name.IndexOf(';');
      if (semi >= 0) name = name[..semi];
      name = name.TrimEnd('.');

      // Skip . and .. entries
      if (nameLen == 1 && (_data[pos + 33] == 0 || _data[pos + 33] == 1)) {
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
        var y = _data[pos + 18] + 1900;
        var m = _data[pos + 19];
        var d = _data[pos + 20];
        var h = _data[pos + 21];
        var mi = _data[pos + 22];
        var s = _data[pos + 23];
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

  private string? GetRockRidgeName(int start, int end) {
    var pos = start;
    while (pos + 4 <= end) {
      var sig0 = _data[pos];
      var sig1 = _data[pos + 1];
      var len = _data[pos + 2];
      if (len < 4) break;
      if (pos + len > end) break;

      if (sig0 == 'N' && sig1 == 'M' && len > 5) {
        var nameLen = len - 5;
        return Encoding.ASCII.GetString(_data, pos + 5, nameLen);
      }
      pos += len;
    }
    return null;
  }

  /// <summary>
  /// Extracts the raw data for the given entry.
  /// </summary>
  public byte[] Extract(IsoEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan((int)entry.DataOffset, (int)entry.Size).ToArray();
  }

  /// <inheritdoc/>
  public void Dispose() { }
}
