#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Fat;

/// <summary>
/// Reads FAT12/FAT16/FAT32 filesystem images. Enumerates files and directories,
/// supports extraction. Handles boot sector parsing, FAT chain following,
/// and directory entry reading with LFN (Long File Name) support.
/// </summary>
public sealed class FatReader : IDisposable {
  /// <summary>
  /// Random-access view over the image. Reading the volume into a byte[] would cap
  /// FAT32 at the ~2 GB array limit, which no FAT32 volume is obliged to respect.
  /// </summary>
  private readonly ImageAccessor _img;
  private readonly List<FatEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<FatEntry> Entries => _entries;
    /// <summary>
  /// Gets or sets the fat type.
  /// </summary>
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

    /// <summary>
  /// Initializes a new instance of <see cref="FatReader"/>.
  /// </summary>
public FatReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _img = new ImageAccessor(stream, leaveOpen: true);
    Parse();
  }

  private void Parse() {
    if (_img.Length < 512)
      throw new InvalidDataException("FAT: image too small.");

    // Check for valid boot sector
    var jump = _img.ReadByte(0);
    if (jump != 0xEB && jump != 0xE9 && jump != 0x00)
      throw new InvalidDataException("FAT: invalid boot jump.");

    _bytesPerSector = _img.ReadUInt16(11);
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = _img.ReadByte(13);
    if (_sectorsPerCluster == 0) _sectorsPerCluster = 1;
    _reservedSectors = _img.ReadUInt16(14);
    _fatCount = _img.ReadByte(16);
    if (_fatCount == 0) _fatCount = 2;
    _rootEntryCount = _img.ReadUInt16(17);

    _totalSectors = _img.ReadUInt16(19);
    if (_totalSectors == 0)
      _totalSectors = _img.ReadInt32(32);

    // BPB_FATSz16 == 0 is the definitive FAT32 indicator (FAT32 always zeroes this
    // field and stores the FAT size in BPB_FATSz32 at offset 36 instead).
    var fatSz16 = _img.ReadUInt16(22);
    var isFat32ByBpb = fatSz16 == 0;
    _fatSize = isFat32ByBpb
      ? _img.ReadInt32(36)
      : fatSz16;

    _rootDirSectors = (_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector;
    _firstDataSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;
    _totalDataClusters = (_totalSectors - _firstDataSector) / _sectorsPerCluster;

    // Prefer the BPB-level FAT32 indicator over the cluster-count heuristic so
    // that images explicitly formatted as FAT32 (even small floppy-sized ones)
    // are read correctly.
    FatType = isFat32ByBpb ? 32
      : _totalDataClusters < 4085 ? 12
      : _totalDataClusters < 65525 ? 16
      : 32;

    if (FatType == 32)
      _rootCluster = _img.ReadInt32(44);

    // Read root directory
    if (FatType == 32) {
      ReadDirectory(_rootCluster, "");
    } else {
      var rootOffset = (long)(_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      ReadDirectoryFixed(rootOffset, _rootEntryCount, "");
    }
  }

  private void ReadDirectory(int cluster, string path) {
    var clusterData = ReadClusterChain(cluster);
    var entryCount = clusterData.Length / 32;
    ReadDirectoryEntries(clusterData, entryCount, path);
  }

  private void ReadDirectoryFixed(long offset, int maxEntries, string path) {
    var size = (int)Math.Min((long)maxEntries * 32, Math.Max(0, _img.Length - offset));
    ReadDirectoryEntries(_img.Read(offset, size), maxEntries, path);
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
    // VFAT/NTFS NT case bits at byte 12: bit 3 (0x08) = base is lowercase,
    // bit 4 (0x10) = extension is lowercase. The 11-byte name field stores
    // uppercase; we apply the case bits here to restore the user's spelling.
    var ntCase = data[offset + 12];
    if ((ntCase & 0x08) != 0) name = name.ToLowerInvariant();
    if ((ntCase & 0x10) != 0) ext = ext.ToLowerInvariant();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  private byte[] ReadClusterChain(int startCluster) {
    var clusterSize = _sectorsPerCluster * _bytesPerSector;
    using var ms = new MemoryStream();
    var cluster = startCluster;
    var seen = new HashSet<int>();

    while (cluster >= 2 && !IsEndOfChain(cluster) && seen.Add(cluster)) {
      // 64-bit throughout: on a multi-gigabyte volume the sector-to-byte product
      // overflows int and silently wraps to a bogus (often negative) offset.
      var offset = ((long)_firstDataSector + (long)(cluster - 2) * _sectorsPerCluster) * _bytesPerSector;
      if (offset + clusterSize > _img.Length) break;
      _img.CopyTo(offset, ms, clusterSize);
      cluster = GetNextCluster(cluster);
    }

    return ms.ToArray();
  }

  private int GetNextCluster(int cluster) {
    var fatOffset = (long)_reservedSectors * _bytesPerSector;
    return FatType switch {
      12 => GetFat12Entry(fatOffset, cluster),
      16 => fatOffset + (long)cluster * 2 + 2 <= _img.Length
        ? _img.ReadUInt16(fatOffset + (long)cluster * 2)
        : 0xFFF,
      32 => fatOffset + (long)cluster * 4 + 4 <= _img.Length
        ? _img.ReadInt32(fatOffset + (long)cluster * 4) & 0x0FFFFFFF
        : 0x0FFFFFF8,
      _ => 0
    };
  }

  private int GetFat12Entry(long fatOffset, int cluster) {
    var bytePos = fatOffset + (long)cluster * 3 / 2;
    if (bytePos + 2 > _img.Length) return 0xFFF;
    var val = _img.ReadUInt16(bytePos);
    return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
  }

  private bool IsEndOfChain(int cluster) => FatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true
  };

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(FatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.StartCluster < 2) return [];

    var data = ReadClusterChain(entry.StartCluster);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  /// <summary>
  /// Opens a forward-only <see cref="Stream"/> that walks the cluster chain
  /// for <paramref name="entry"/>, pulling one cluster at a time. Peak
  /// memory cost is bounded by the cluster size, not the file size — the
  /// underlying image snapshot is shared with this reader.
  /// </summary>
  /// <remarks>
  /// Wrap the returned stream in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to <c>entry.Size</c> to obtain the per-entry isolation contract:
  /// reads past the entry's logical size return 0, slack-byte leakage is
  /// physically impossible.
  /// </remarks>
  internal FatChainStream OpenChainStream(FatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return new FatChainStream(
      _img, entry.StartCluster, entry.Size,
      FatType, _bytesPerSector, _sectorsPerCluster,
      _reservedSectors, _fatCount, _fatSize, _firstDataSector);
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
