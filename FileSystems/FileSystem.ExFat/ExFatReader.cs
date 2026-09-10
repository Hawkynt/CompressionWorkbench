#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Reads exFAT filesystem images. Parses VBR, FAT, and directory entry sets
/// (File 0x85 + Stream Extension 0xC0 + File Name 0xC1). Supports subdirectories.
/// </summary>
public sealed class ExFatReader : IDisposable {
  /// <summary>
  /// Random-access view over the volume. exFAT exists precisely to carry volumes
  /// past FAT32's limits, so reading one into a byte[] would cap the reader well
  /// below the sizes the format is used for.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly object _gate = new();
  private readonly List<ExFatEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<ExFatEntry> Entries => _entries;

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private uint _fatOffset;       // in bytes
  private uint _fatLengthBytes;
  private uint _clusterHeapOffset; // in bytes
  private uint _clusterCount;
  private uint _rootDirCluster;

  /// <summary>
  /// Initializes a new instance of <see cref="ExFatReader"/>.
  /// </summary>
  public ExFatReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _data = new ImageAccessor(stream, leaveOpen);
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("exFAT: image too small.");

    var sig = Encoding.ASCII.GetString(_data.Read(3, 8));
    if (sig != "EXFAT   ")
      throw new InvalidDataException("exFAT: invalid signature.");

    if (_data.ReadByte(510) != 0x55 || _data.ReadByte(511) != 0xAA)
      throw new InvalidDataException("exFAT: missing boot signature.");

    var bytesPerSectorShift = _data.ReadByte(108);
    var sectorsPerClusterShift = _data.ReadByte(109);
    if (bytesPerSectorShift is < 9 or > 12 || sectorsPerClusterShift > 25 - bytesPerSectorShift)
      throw new InvalidDataException("exFAT: invalid sector/cluster shift geometry.");
    _bytesPerSector = 1 << bytesPerSectorShift;
    _sectorsPerCluster = 1 << sectorsPerClusterShift;
    _clusterSize = checked(_bytesPerSector * _sectorsPerCluster);

    var fatOffsetSectors = _data.ReadUInt32(80);
    var fatLengthSectors = _data.ReadUInt32(84);
    var clusterHeapOffsetSectors = _data.ReadUInt32(88);
    _clusterCount = _data.ReadUInt32(92);
    _rootDirCluster = _data.ReadUInt32(96);

    _fatOffset = checked(fatOffsetSectors * (uint)_bytesPerSector);
    _fatLengthBytes = checked(fatLengthSectors * (uint)_bytesPerSector);
    _clusterHeapOffset = checked(clusterHeapOffsetSectors * (uint)_bytesPerSector);

    ReadDirectory(_rootDirCluster, "", generalSecondaryFlags: 0, dataLength: -1);
  }

  private void ReadDirectory(uint cluster, string path, byte generalSecondaryFlags, long dataLength) {
    var dirData = ReadAllocation(cluster, generalSecondaryFlags, dataLength);
    var entryCount = dirData.Length / 32;

    for (var i = 0; i < entryCount; i++) {
      var off = i * 32;
      var entryType = dirData[off];
      if (entryType == 0x00) break;

      if (entryType != 0x85) continue;
      var secondaryCount = dirData[off + 1];
      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4));
      var isDir = (attributes & 0x10) != 0;

      var modTime = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 12));
      var modDate = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 14));
      DateTime? lastMod = null;
      if (modDate != 0) {
        try {
          lastMod = new DateTime(
            1980 + (modDate >> 9), (modDate >> 5) & 0xF, modDate & 0x1F,
            modTime >> 11, (modTime >> 5) & 0x3F, (modTime & 0x1F) * 2);
        } catch { /* malformed timestamps do not prevent data recovery */ }
      }

      if (i + 1 >= entryCount) break;
      var streamOff = (i + 1) * 32;
      if (dirData[streamOff] != 0xC0) { i += secondaryCount; continue; }

      var streamFlags = dirData[streamOff + 1];
      var nameLength = dirData[streamOff + 3];
      var validDataLength = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(streamOff + 8));
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(streamOff + 20));
      var entryDataLength = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(streamOff + 24));

      var nameBuilder = new StringBuilder();
      var nameEntriesNeeded = (nameLength + 14) / 15;
      for (var n = 0; n < nameEntriesNeeded && i + 2 + n < entryCount; n++) {
        var nameOff = (i + 2 + n) * 32;
        if (dirData[nameOff] != 0xC1) break;
        var charsToRead = Math.Min(15, nameLength - n * 15);
        for (var c = 0; c < charsToRead; c++) {
          var charOff = nameOff + 2 + c * 2;
          if (charOff + 2 > dirData.Length) break;
          var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(charOff));
          if (ch == 0) break;
          nameBuilder.Append(ch);
        }
      }

      var name = nameBuilder.ToString();
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      _entries.Add(new ExFatEntry {
        Name = fullPath,
        Size = isDir ? 0 : Math.Max(0, entryDataLength),
        IsDirectory = isDir,
        LastModified = lastMod,
        FirstCluster = firstCluster,
        GeneralSecondaryFlags = streamFlags,
        ValidDataLength = Math.Max(0, validDataLength),
      });

      if (isDir && firstCluster >= 2)
        ReadDirectory(firstCluster, fullPath, streamFlags, entryDataLength);

      i += secondaryCount;
    }
  }

  private byte[] ReadAllocation(uint startCluster, byte generalSecondaryFlags, long dataLength) {
    using var ms = new MemoryStream();
    foreach (var cluster in EnumerateAllocation(startCluster, generalSecondaryFlags, dataLength)) {
      var offset = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
      if (offset < 0 || offset + _clusterSize > _data.Length) break;
      _data.CopyTo(offset, ms, _clusterSize);
    }
    return ms.ToArray();
  }

  private IEnumerable<uint> EnumerateAllocation(uint startCluster, byte generalSecondaryFlags, long dataLength) {
    if (startCluster < 2 || startCluster > _clusterCount + 1) yield break;

    if ((generalSecondaryFlags & 0x02) != 0 && dataLength >= 0) {
      var count = dataLength == 0 ? 0L : (dataLength + _clusterSize - 1) / _clusterSize;
      for (long i = 0; i < count; ++i) {
        var cluster = startCluster + (uint)i;
        if (cluster > _clusterCount + 1) yield break;
        yield return cluster;
      }
      yield break;
    }

    var clusterCursor = startCluster;
    var seen = new HashSet<uint>();
    while (clusterCursor >= 2 && clusterCursor <= _clusterCount + 1 && seen.Add(clusterCursor)) {
      yield return clusterCursor;
      var next = GetNextCluster(clusterCursor);
      if (next >= 0xFFFFFFF8) yield break;
      clusterCursor = next;
    }
  }

  private uint GetNextCluster(uint cluster) {
    var pos = (long)_fatOffset + (long)cluster * 4;
    if (pos < _fatOffset || pos + 4 > (long)_fatOffset + _fatLengthBytes || pos + 4 > _data.Length)
      return 0xFFFFFFF8;
    return _data.ReadUInt32(pos);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(ExFatEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size <= 0 || entry.FirstCluster < 2) return [];
    if (entry.Size > Array.MaxLength)
      throw new NotSupportedException("exFAT entry is too large for in-memory extraction; use the mounted positional read path.");

    var result = new byte[(int)entry.Size];
    ReadAt(entry, 0, result);
    return result;
  }

  /// <summary>
  /// Reads file bytes at a logical offset without materialising the whole file.
  /// Bytes between ValidDataLength and DataLength are defined by exFAT as zeros.
  /// </summary>
  internal int ReadAt(ExFatEntry entry, long offset, Span<byte> destination) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) throw new UnauthorizedAccessException("Directories do not expose file data.");
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.IsEmpty || offset >= entry.Size) return 0;

    var count = checked((int)Math.Min(destination.Length, entry.Size - offset));
    var target = destination[..count];
    target.Clear();
    var validLength = Math.Min(entry.Size, Math.Max(0, entry.ValidDataLength));
    if (offset >= validLength || entry.FirstCluster < 2) return count;
    var validCount = checked((int)Math.Min(count, validLength - offset));
    var validTarget = target[..validCount];

    lock (_gate) {
      var logicalCluster = offset / _clusterSize;
      var withinCluster = checked((int)(offset % _clusterSize));
      var copied = 0;
      var allocationIndex = 0L;
      foreach (var cluster in EnumerateAllocation(entry.FirstCluster, entry.GeneralSecondaryFlags, entry.Size)) {
        if (allocationIndex++ < logicalCluster) continue;
        var physical = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize + withinCluster;
        var take = Math.Min(validCount - copied, _clusterSize - withinCluster);
        if (_data.Read(physical, validTarget.Slice(copied, take)) != take)
          throw new EndOfStreamException($"exFAT cluster {cluster} is truncated in the image.");
        copied += take;
        withinCluster = 0;
        if (copied >= validCount) break;
      }
      if (copied != validCount)
        throw new InvalidDataException($"exFAT file '{entry.Name}' allocation supplies only {copied:N0} of {validCount:N0} requested valid bytes.");
    }
    return count;
  }

  internal long GetAllocatedSize(ExFatEntry entry) {
    if (entry.FirstCluster < 2) return 0;
    long clusters = 0;
    lock (_gate)
      foreach (var _ in EnumerateAllocation(entry.FirstCluster, entry.GeneralSecondaryFlags, entry.IsDirectory ? -1 : entry.Size))
        ++clusters;
    return checked(clusters * _clusterSize);
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => _data.Dispose();
}
