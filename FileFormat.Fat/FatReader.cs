#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Fat;

/// <summary>
/// Reads FAT12/FAT16/FAT32 filesystem images. Enumerates files and directories,
/// supports extraction. Handles boot sector parsing, FAT chain following,
/// and directory entry reading with LFN (Long File Name) support.
/// </summary>
public sealed class FatReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<FatEntry> _entries = [];

  public IReadOnlyList<FatEntry> Entries => _entries;
  public int FatType { get; private set; } // 12, 16, or 32

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount; // FAT12/16 only
  private int _totalSectors;
  private int _fatSize; // sectors per FAT
  private int _rootDirSectors;
  private int _firstDataSector;
  private int _totalDataClusters;
  private int _rootCluster; // FAT32 only

  public FatReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("FAT: image too small.");

    // Check for valid boot sector
    if (_data[0] != 0xEB && _data[0] != 0xE9 && _data[0] != 0x00)
      throw new InvalidDataException("FAT: invalid boot jump.");

    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(11));
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = _data[13];
    if (_sectorsPerCluster == 0) _sectorsPerCluster = 1;
    _reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(14));
    _fatCount = _data[16];
    if (_fatCount == 0) _fatCount = 2;
    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(17));

    _totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(19));
    if (_totalSectors == 0)
      _totalSectors = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(32));

    _fatSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(22));
    if (_fatSize == 0)
      _fatSize = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(36));

    _rootDirSectors = (_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector;
    _firstDataSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;
    _totalDataClusters = (_totalSectors - _firstDataSector) / _sectorsPerCluster;

    FatType = _totalDataClusters < 4085 ? 12 : _totalDataClusters < 65525 ? 16 : 32;

    if (FatType == 32)
      _rootCluster = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(44));

    // Read root directory
    if (FatType == 32) {
      ReadDirectory(_rootCluster, "");
    } else {
      var rootOffset = (_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      ReadDirectoryFixed(rootOffset, _rootEntryCount, "");
    }
  }

  private void ReadDirectory(int cluster, string path) {
    var clusterData = ReadClusterChain(cluster);
    var entryCount = clusterData.Length / 32;
    ReadDirectoryEntries(clusterData, entryCount, path);
  }

  private void ReadDirectoryFixed(int offset, int maxEntries, string path) {
    var size = maxEntries * 32;
    if (offset + size > _data.Length) size = _data.Length - offset;
    ReadDirectoryEntries(_data.AsSpan(offset, size).ToArray(), maxEntries, path);
  }

  private void ReadDirectoryEntries(byte[] dirData, int maxEntries, string path) {
    var lfnParts = new SortedDictionary<int, string>();

    for (var i = 0; i < maxEntries; i++) {
      var off = i * 32;
      if (off + 32 > dirData.Length) break;

      var firstByte = dirData[off];
      if (firstByte == 0x00) break; // end of directory
      if (firstByte == 0xE5) { lfnParts.Clear(); continue; } // deleted

      var attr = dirData[off + 11];

      // LFN entry
      if ((attr & 0x3F) == 0x0F) {
        var seq = dirData[off] & 0x3F;
        var part = new StringBuilder();
        // Characters at offsets: 1-10 (5 chars), 14-25 (6 chars), 28-31 (2 chars)
        ReadLfnChars(dirData, off + 1, 5, part);
        ReadLfnChars(dirData, off + 14, 6, part);
        ReadLfnChars(dirData, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        continue;
      }

      // Short name entry
      if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

      var shortName = GetShortName(dirData, off);
      string name;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var part in lfnParts.Values)
          sb.Append(part);
        name = sb.ToString().TrimEnd('\0', '\xFFFF');
        lfnParts.Clear();
      } else {
        name = shortName;
      }

      var isDir = (attr & 0x10) != 0;
      var fileSize = BinaryPrimitives.ReadInt32LittleEndian(dirData.AsSpan(off + 28));
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 26));
      if (FatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 20)) << 16;

      // Skip . and .. entries
      if (name is "." or "..") continue;

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      var date = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 24));
      var time = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 22));
      DateTime? lastMod = null;
      if (date != 0) {
        try {
          lastMod = new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F,
            time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
        } catch { /* ignore invalid dates */ }
      }

      _entries.Add(new FatEntry {
        Name = fullPath,
        Size = isDir ? 0 : fileSize,
        IsDirectory = isDir,
        StartCluster = startCluster,
        LastModified = lastMod,
      });

      if (isDir && startCluster >= 2)
        ReadDirectory(startCluster, fullPath);
    }
  }

  private static void ReadLfnChars(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; j++) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  private byte[] ReadClusterChain(int startCluster) {
    var clusterSize = _sectorsPerCluster * _bytesPerSector;
    using var ms = new MemoryStream();
    var cluster = startCluster;
    var seen = new HashSet<int>();

    while (cluster >= 2 && !IsEndOfChain(cluster) && seen.Add(cluster)) {
      var offset = (_firstDataSector + (cluster - 2) * _sectorsPerCluster) * _bytesPerSector;
      if (offset + clusterSize > _data.Length) break;
      ms.Write(_data, offset, clusterSize);
      cluster = GetNextCluster(cluster);
    }

    return ms.ToArray();
  }

  private int GetNextCluster(int cluster) {
    var fatOffset = _reservedSectors * _bytesPerSector;
    return FatType switch {
      12 => GetFat12Entry(fatOffset, cluster),
      16 => fatOffset + cluster * 2 + 2 <= _data.Length
        ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(fatOffset + cluster * 2))
        : 0xFFF,
      32 => fatOffset + cluster * 4 + 4 <= _data.Length
        ? BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(fatOffset + cluster * 4)) & 0x0FFFFFFF
        : 0x0FFFFFF8,
      _ => 0
    };
  }

  private int GetFat12Entry(int fatOffset, int cluster) {
    var bytePos = fatOffset + cluster * 3 / 2;
    if (bytePos + 2 > _data.Length) return 0xFFF;
    var val = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(bytePos));
    return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
  }

  private bool IsEndOfChain(int cluster) => FatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true
  };

  public byte[] Extract(FatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.StartCluster < 2) return [];

    var data = ReadClusterChain(entry.StartCluster);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
