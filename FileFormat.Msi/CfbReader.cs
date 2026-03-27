using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Msi;

/// <summary>
/// Reads OLE Compound File Binary Format (MS-CFB / Structured Storage) files.
/// Supports version 3 (512-byte sectors) and version 4 (4096-byte sectors).
/// </summary>
internal sealed class CfbReader {
  // Magic signature: D0 CF 11 E0 A1 B1 1A E1
  internal static readonly byte[] Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

  private const uint EndOfChain = 0xFFFFFFFE;
  private const uint FreeSect   = 0xFFFFFFFF;
  private const uint NoStream   = 0xFFFFFFFF;

  private readonly byte[] _data;
  private readonly int _sectorSize;
  private readonly int _miniSectorSize;
  private readonly uint _miniStreamCutoff;
  private readonly uint[] _fat;
  private readonly uint[] _miniFat;
  private readonly byte[] _miniStreamData;
  private readonly List<CfbDirectoryEntry> _entries = [];

  public IReadOnlyList<CfbDirectoryEntry> Entries => _entries;

  public CfbReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();

    if (_data.Length < 512)
      throw new InvalidDataException("CFB: file too small for header.");

    // Validate magic
    for (var i = 0; i < 8; i++)
      if (_data[i] != Magic[i])
        throw new InvalidDataException("CFB: invalid signature.");

    var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x1A));
    var sectorExp = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x1E));
    var miniSectorExp = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x20));

    _sectorSize = 1 << sectorExp;       // 512 or 4096
    _miniSectorSize = 1 << miniSectorExp; // typically 64

    var fatSectorCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x2C));
    var firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x30));
    _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x38));
    var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x3C));
    var numMiniFatSectors = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x40));
    var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x44));
    var numDifatSectors = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x48));

    // Build FAT sector list from header DIFAT array + DIFAT chain
    var fatSectorIds = new List<uint>();
    // First 109 DIFAT entries in header at offset 0x4C
    var maxHeaderDifat = Math.Min(fatSectorCount, 109);
    for (var i = 0; i < maxHeaderDifat; i++) {
      var sid = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x4C + i * 4));
      if (sid != FreeSect && sid != EndOfChain)
        fatSectorIds.Add(sid);
    }

    // Follow DIFAT chain for remaining FAT sectors
    if (numDifatSectors > 0 && firstDifatSector != EndOfChain) {
      var difatSector = firstDifatSector;
      for (var d = 0; d < numDifatSectors && difatSector != EndOfChain && difatSector != FreeSect; d++) {
        var difatOffset = SectorOffset(difatSector);
        var entriesPerDifat = (_sectorSize / 4) - 1; // last uint32 is next DIFAT sector
        for (var i = 0; i < entriesPerDifat && fatSectorIds.Count < fatSectorCount; i++) {
          var sid = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(difatOffset + i * 4));
          if (sid != FreeSect && sid != EndOfChain)
            fatSectorIds.Add(sid);
        }
        difatSector = BinaryPrimitives.ReadUInt32LittleEndian(
          _data.AsSpan(difatOffset + entriesPerDifat * 4));
      }
    }

    // Build FAT
    var fatEntryCount = fatSectorIds.Count * (_sectorSize / 4);
    _fat = new uint[fatEntryCount];
    for (var i = 0; i < fatSectorIds.Count; i++) {
      var off = SectorOffset(fatSectorIds[i]);
      var entriesThisSector = _sectorSize / 4;
      for (var j = 0; j < entriesThisSector; j++) {
        var idx = i * entriesThisSector + j;
        if (idx < _fat.Length && off + j * 4 + 4 <= _data.Length)
          _fat[idx] = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + j * 4));
      }
    }

    // Read directory entries
    var dirData = ReadSectorChain(firstDirSector);
    var entryCount = dirData.Length / 128;
    for (var i = 0; i < entryCount; i++) {
      var entry = ParseDirectoryEntry(dirData, i * 128, i);
      if (entry != null)
        _entries.Add(entry);
    }

    // Build Mini FAT
    if (numMiniFatSectors > 0 && firstMiniFatSector != EndOfChain) {
      var miniFatData = ReadSectorChain(firstMiniFatSector);
      _miniFat = new uint[miniFatData.Length / 4];
      for (var i = 0; i < _miniFat.Length; i++)
        _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatData.AsSpan(i * 4));
    } else {
      _miniFat = [];
    }

    // Build mini stream container (root entry's stream data)
    var rootEntry = _entries.Count > 0 ? _entries[0] : null;
    if (rootEntry is { EntryType: CfbEntryType.RootStorage, StartSector: not EndOfChain }) {
      _miniStreamData = ReadSectorChain(rootEntry.StartSector);
    } else {
      _miniStreamData = [];
    }
  }

  private int SectorOffset(uint sectorId) => (int)((_sectorSize) + sectorId * _sectorSize);

  private byte[] ReadSectorChain(uint startSector) {
    if (startSector == EndOfChain || startSector == FreeSect)
      return [];

    using var ms = new MemoryStream();
    var current = startSector;
    var safety = 0;
    while (current != EndOfChain && current != FreeSect && safety++ < 1_000_000) {
      var offset = SectorOffset(current);
      if (offset + _sectorSize > _data.Length) break;
      ms.Write(_data, offset, _sectorSize);
      current = current < _fat.Length ? _fat[current] : EndOfChain;
    }
    return ms.ToArray();
  }

  public byte[] ExtractStream(CfbDirectoryEntry entry) {
    if (entry.StreamSize == 0) return [];

    byte[] raw;
    if (entry.StreamSize < _miniStreamCutoff && entry.EntryType == CfbEntryType.Stream) {
      // Read from mini stream
      raw = ReadMiniStreamChain(entry.StartSector, (int)entry.StreamSize);
    } else {
      raw = ReadSectorChain(entry.StartSector);
    }

    // Trim to actual size
    if (raw.Length > (int)entry.StreamSize)
      return raw[..(int)entry.StreamSize];
    return raw;
  }

  private byte[] ReadMiniStreamChain(uint startMiniSector, int size) {
    if (startMiniSector == EndOfChain || _miniStreamData.Length == 0)
      return [];

    using var ms = new MemoryStream();
    var current = startMiniSector;
    var remaining = size;
    var safety = 0;
    while (current != EndOfChain && current != FreeSect && remaining > 0 && safety++ < 1_000_000) {
      var offset = (int)(current * _miniSectorSize);
      if (offset >= _miniStreamData.Length) break;
      var copyLen = Math.Min(_miniSectorSize, remaining);
      copyLen = Math.Min(copyLen, _miniStreamData.Length - offset);
      ms.Write(_miniStreamData, offset, copyLen);
      remaining -= copyLen;
      current = current < _miniFat.Length ? _miniFat[current] : EndOfChain;
    }
    return ms.ToArray();
  }

  private CfbDirectoryEntry? ParseDirectoryEntry(byte[] data, int offset, int index) {
    if (offset + 128 > data.Length) return null;

    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 0x40));
    var entryType = data[offset + 0x42];

    // Skip invalid/empty entries
    if (entryType == 0 || nameLen == 0) return null;

    // Name is UTF-16LE, nameLen includes null terminator (2 bytes)
    var nameByteCount = Math.Max(0, nameLen - 2);
    var name = nameByteCount > 0
      ? Encoding.Unicode.GetString(data, offset, nameByteCount)
      : string.Empty;

    var startSector = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0x74));
    var streamSize = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 0x78));

    // v3 files only use lower 32 bits
    if (_sectorSize == 512 && entryType == 2) // stream
      streamSize &= 0xFFFFFFFF;

    var childDid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0x4C));
    var leftDid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0x44));
    var rightDid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0x48));

    return new CfbDirectoryEntry {
      Index = index,
      Name = name,
      EntryType = entryType switch {
        1 => CfbEntryType.Storage,
        2 => CfbEntryType.Stream,
        5 => CfbEntryType.RootStorage,
        _ => CfbEntryType.Unknown,
      },
      StartSector = startSector,
      StreamSize = streamSize,
      ChildDid = childDid,
      LeftSiblingDid = leftDid,
      RightSiblingDid = rightDid,
    };
  }
}

internal enum CfbEntryType : byte {
  Unknown = 0,
  Storage = 1,
  Stream = 2,
  RootStorage = 5,
}

internal sealed class CfbDirectoryEntry {
  public int Index { get; init; }
  public string Name { get; init; } = "";
  public CfbEntryType EntryType { get; init; }
  public uint StartSector { get; init; }
  public long StreamSize { get; init; }
  public uint ChildDid { get; init; }
  public uint LeftSiblingDid { get; init; }
  public uint RightSiblingDid { get; init; }
}
