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
  private const uint EocMarker = 0xFFFFFFFFu;

  /// <summary>
  /// Random-access view over the volume. exFAT exists precisely to carry volumes
  /// past FAT32's limits, so reading one into a byte[] would cap the reader well
  /// below the sizes the format is used for.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly object _gate = new();
  private readonly List<ExFatEntry> _entries = [];
  private readonly HashSet<uint> _visitedDirectories = [];

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
    if (cluster < 2 || cluster > _clusterCount + 1)
      throw new InvalidDataException($"exFAT directory '{path}' starts at invalid cluster {cluster}.");
    if (!_visitedDirectories.Add(cluster))
      throw new InvalidDataException($"exFAT directory '{path}' reuses already-visited cluster {cluster}; directory cycles/cross-links are not valid.");

    var dirData = ReadAllocation(cluster, generalSecondaryFlags, dataLength);
    var entryCount = dirData.Length / 32;

    for (var i = 0; i < entryCount; i++) {
      var off = i * 32;
      var entryType = dirData[off];
      if (entryType == 0x00) break;

      if (entryType != 0x85) continue;
      var secondaryCount = dirData[off + 1];
      if (secondaryCount < 2)
        throw new InvalidDataException($"exFAT file entry at directory slot {i} has only {secondaryCount} secondary entries.");
      var setLength = checked((secondaryCount + 1) * 32);
      if (off + setLength > dirData.Length)
        throw new InvalidDataException($"exFAT file entry set at directory slot {i} is truncated.");
      var entrySet = dirData.AsSpan(off, setLength);
      var storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(entrySet[2..4]);
      var computedChecksum = ComputeEntrySetChecksum(entrySet);
      if (storedChecksum != computedChecksum)
        throw new InvalidDataException(
          $"exFAT file entry set at directory slot {i} has checksum 0x{storedChecksum:X4}; expected 0x{computedChecksum:X4}.");

      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(entrySet[4..6]);
      var isDir = (attributes & 0x10) != 0;

      var modTime = BinaryPrimitives.ReadUInt16LittleEndian(entrySet[12..14]);
      var modDate = BinaryPrimitives.ReadUInt16LittleEndian(entrySet[14..16]);
      DateTime? lastMod = null;
      if (modDate != 0) {
        try {
          lastMod = new DateTime(
            1980 + (modDate >> 9), (modDate >> 5) & 0xF, modDate & 0x1F,
            modTime >> 11, (modTime >> 5) & 0x3F, (modTime & 0x1F) * 2);
        } catch { /* malformed timestamps do not prevent data recovery */ }
      }

      const int streamRelativeOffset = 32;
      if (entrySet[streamRelativeOffset] != 0xC0)
        throw new InvalidDataException($"exFAT file entry set at directory slot {i} does not begin with a Stream Extension secondary.");

      var streamFlags = entrySet[streamRelativeOffset + 1];
      if ((streamFlags & 0xFC) != 0)
        throw new InvalidDataException($"exFAT Stream Extension for directory slot {i} has reserved GeneralSecondaryFlags bits set: 0x{streamFlags:X2}.");
      var nameLength = entrySet[streamRelativeOffset + 3];
      if (nameLength == 0)
        throw new InvalidDataException($"exFAT file entry set at directory slot {i} has an empty FileName.");

      var validDataLengthRaw = BinaryPrimitives.ReadUInt64LittleEndian(entrySet[(streamRelativeOffset + 8)..]);
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(entrySet[(streamRelativeOffset + 20)..]);
      var dataLengthRaw = BinaryPrimitives.ReadUInt64LittleEndian(entrySet[(streamRelativeOffset + 24)..]);
      if (validDataLengthRaw > long.MaxValue || dataLengthRaw > long.MaxValue)
        throw new NotSupportedException("exFAT stream length exceeds the signed 64-bit mounted-file contract.");
      var validDataLength = (long)validDataLengthRaw;
      var entryDataLength = (long)dataLengthRaw;
      if (validDataLength > entryDataLength)
        throw new InvalidDataException($"exFAT stream at directory slot {i} has ValidDataLength greater than DataLength.");
      if (entryDataLength > 0 && firstCluster < 2)
        throw new InvalidDataException($"exFAT stream at directory slot {i} has non-zero DataLength but no valid first cluster.");
      if (isDir && validDataLength != entryDataLength)
        throw new InvalidDataException($"exFAT directory stream at slot {i} has ValidDataLength different from DataLength.");

      var nameBuilder = new StringBuilder(nameLength);
      var nameEntriesNeeded = (nameLength + 14) / 15;
      if (1 + nameEntriesNeeded > secondaryCount)
        throw new InvalidDataException($"exFAT file entry set at directory slot {i} does not contain enough File Name secondaries.");
      for (var n = 0; n < nameEntriesNeeded; n++) {
        var nameRelativeOffset = 64 + n * 32;
        if (entrySet[nameRelativeOffset] != 0xC1)
          throw new InvalidDataException($"exFAT file entry set at directory slot {i} has a non-FileName secondary where a File Name entry is required.");
        var charsToRead = Math.Min(15, nameLength - n * 15);
        for (var c = 0; c < charsToRead; c++) {
          var charOffset = nameRelativeOffset + 2 + c * 2;
          var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(entrySet[charOffset..]);
          if (ch == 0)
            throw new InvalidDataException($"exFAT file entry set at directory slot {i} contains a NUL inside its declared filename length.");
          nameBuilder.Append(ch);
        }
      }

      var name = nameBuilder.ToString();
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      var entry = new ExFatEntry {
        Name = fullPath,
        Size = isDir ? 0 : entryDataLength,
        IsDirectory = isDir,
        LastModified = lastMod,
        FirstCluster = firstCluster,
        GeneralSecondaryFlags = streamFlags,
        ValidDataLength = validDataLength,
        DataLength = entryDataLength,
      };
      _entries.Add(entry);

      if (isDir && firstCluster >= 2)
        ReadDirectory(firstCluster, fullPath, streamFlags, entryDataLength);

      i += secondaryCount;
    }
  }

  private byte[] ReadAllocation(uint startCluster, byte generalSecondaryFlags, long dataLength) {
    using var ms = new MemoryStream();
    foreach (var cluster in EnumerateAllocation(startCluster, generalSecondaryFlags, dataLength)) {
      var offset = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
      if (offset < 0 || offset + _clusterSize > _data.Length)
        throw new EndOfStreamException($"exFAT cluster {cluster} extends beyond the image.");
      _data.CopyTo(offset, ms, _clusterSize);
    }
    return ms.ToArray();
  }

  private IEnumerable<uint> EnumerateAllocation(uint startCluster, byte generalSecondaryFlags, long dataLength) {
    if (startCluster < 2 || startCluster > _clusterCount + 1) yield break;

    if ((generalSecondaryFlags & 0x02) != 0 && dataLength >= 0) {
      var count = dataLength == 0 ? 0L : checked((dataLength + _clusterSize - 1) / _clusterSize);
      if (count > _clusterCount || (ulong)startCluster + (ulong)count > (ulong)_clusterCount + 2)
        throw new InvalidDataException("exFAT NoFatChain allocation extends beyond the cluster heap.");
      for (long i = 0; i < count; ++i)
        yield return startCluster + (uint)i;
      yield break;
    }

    var clusterCursor = startCluster;
    var seen = new HashSet<uint>();
    while (true) {
      if (clusterCursor < 2 || clusterCursor > _clusterCount + 1)
        throw new InvalidDataException($"exFAT FAT chain references invalid cluster {clusterCursor}.");
      if (!seen.Add(clusterCursor))
        throw new InvalidDataException($"exFAT FAT chain contains a loop at cluster {clusterCursor}.");
      yield return clusterCursor;
      var next = GetNextCluster(clusterCursor);
      if (next == EocMarker) yield break;
      if (next < 2 || next > _clusterCount + 1)
        throw new InvalidDataException($"exFAT FAT chain from cluster {clusterCursor} terminates with invalid value 0x{next:X8}.");
      clusterCursor = next;
    }
  }

  private uint GetNextCluster(uint cluster) {
    var pos = (long)_fatOffset + (long)cluster * 4;
    if (pos < _fatOffset || pos + 4 > (long)_fatOffset + _fatLengthBytes || pos + 4 > _data.Length)
      throw new InvalidDataException($"exFAT FAT entry for cluster {cluster} lies outside the declared FAT.");
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
    var validLength = Math.Min(entry.Size, entry.ValidDataLength);
    if (offset >= validLength || entry.FirstCluster < 2) return count;
    var validCount = checked((int)Math.Min(count, validLength - offset));
    var validTarget = target[..validCount];

    lock (_gate) {
      var logicalCluster = offset / _clusterSize;
      var withinCluster = checked((int)(offset % _clusterSize));
      var copied = 0;
      var allocationIndex = 0L;
      foreach (var cluster in EnumerateAllocation(entry.FirstCluster, entry.GeneralSecondaryFlags, entry.DataLength)) {
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
      foreach (var _ in EnumerateAllocation(entry.FirstCluster, entry.GeneralSecondaryFlags, entry.DataLength))
        ++clusters;
    return checked(clusters * _clusterSize);
  }

  private static ushort ComputeEntrySetChecksum(ReadOnlySpan<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i is 2 or 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    return checksum;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => _data.Dispose();
}
