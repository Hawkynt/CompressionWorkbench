#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nwfs;

/// <summary>
/// Reads the supported single-segment Traditional NetWare profile, selecting
/// valid redundant master/volume metadata and reconstructing the mirrored FAT.
/// </summary>
/// <remarks>
/// <para>The route is the one a NetWare reader takes. The partition table gives
/// the partition; the hotfix header at sector 32 of it gives the distance to
/// the logical partition; the volume table in that logical partition gives the
/// allocation-cluster size and the clusters the FAT and the directory start at;
/// and the volume area, which follows the volume table, is what every cluster
/// number counts from.</para>
///
/// <para>Master and volume metadata are held in several copies and the FAT is
/// mirrored, so each structure is taken from whichever copy is valid rather
/// than from a fixed one.</para>
///
/// <para>Directory entries are flat. Each names the directory it belongs to
/// rather than being nested inside it, so a path is walked by collecting every
/// entry once and then following parent ids down from the root.</para>
/// </remarks>
public sealed class NwfsReader {

  /// <summary>One thing on the volume: a file or a directory.</summary>
  public sealed record Item(string Path, bool IsDirectory, long Length, uint FirstBlock);

  private readonly byte[] _image;
  private readonly uint[] _fatIndex;
  private readonly uint[] _fatNext;
  private readonly List<Raw> _directory;
  private readonly long _partitionOffset;
  private readonly long _logicalPartitionOffset;
  private readonly long _volumeOffset;
  private readonly int _clusterSize;
  private readonly int _blocksPerCluster;
  private readonly uint _directory1;
  private readonly uint _directory2;
  private readonly uint _clusterCount;
  private readonly int _fatPhysicalBlocks;

  /// <summary>What the volume calls itself.</summary>
  public string VolumeName { get; }

  /// <summary>Bytes to an allocation cluster on this volume.</summary>
  public int BlockSize => this._clusterSize;

  internal long PartitionOffset => this._partitionOffset;
  internal long LogicalPartitionOffset => this._logicalPartitionOffset;
  internal long DataAreaOffset => this._volumeOffset;
  internal long VolumeOffset => this._volumeOffset;
  internal int BlocksPerCluster => this._blocksPerCluster;
  internal uint RootDirectoryBlock => this._directory1;
  internal uint SecondDirectoryBlock => this._directory2;
  internal uint TotalBlocks => this._clusterCount;
  internal uint TotalClusters => this._clusterCount;
  internal int FatPhysicalBlocks => this._fatPhysicalBlocks;
  internal int TotalPhysicalBlocks => checked((int)this._clusterCount * this._blocksPerCluster);

  private sealed record Raw(
    uint RecordNumber,
    uint ParentId,
    bool IsDirectory,
    string Name,
    uint Length,
    uint FirstBlock);

  private NwfsReader(
      byte[] image,
      long partitionOffset,
      long logicalPartitionOffset,
      long volumeOffset,
      int clusterSize,
      uint directory1,
      uint directory2,
      uint clusterCount,
      int fatPhysicalBlocks,
      uint[] fatIndex,
      uint[] fatNext,
      string volumeName,
      List<Raw> directory) {
    this._image = image;
    this._partitionOffset = partitionOffset;
    this._logicalPartitionOffset = logicalPartitionOffset;
    this._volumeOffset = volumeOffset;
    this._clusterSize = clusterSize;
    this._blocksPerCluster = NwfsLayout.BlocksPerCluster(clusterSize);
    this._directory1 = directory1;
    this._directory2 = directory2;
    this._clusterCount = clusterCount;
    this._fatPhysicalBlocks = fatPhysicalBlocks;
    this._fatIndex = fatIndex;
    this._fatNext = fatNext;
    this.VolumeName = volumeName;
    this._directory = directory;
  }

  /// <summary>Opens the first NetWare volume in <paramref name="image" />, or null if there is none.</summary>
  public static NwfsReader? TryOpen(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    try {
      return OpenCore(image);
    } catch (Exception e) when (e is ArgumentException or ArithmeticException or InvalidDataException) {
      return null;
    }
  }

  private static NwfsReader? OpenCore(byte[] image) {
    var partition = FindPartition(image);
    if (partition.Sectors == 0)
      return null;

    if (!TryReadMasterMetadata(
          image,
          partition,
          out var hotfixSizeSectors,
          out var logicalSectors))
      return null;

    if (hotfixSizeSectors % NwfsLayout.SectorsPerIoBlock != 0
        || logicalSectors % NwfsLayout.SectorsPerIoBlock != 0)
      return null;

    var logicalPartitionOffset = checked(
      partition.Offset + (long)hotfixSizeSectors * NwfsLayout.SectorSize);
    var tables = new List<byte[]>();
    foreach (var logicalBlock in NwfsLayout.VolumeTableLogicalBlocks) {
      var offset = checked(logicalPartitionOffset + (long)logicalBlock * NwfsLayout.IoBlockSize);
      if (offset < 0 || offset + NwfsLayout.VolumeTableBytes > image.LongLength)
        continue;
      var table = image.AsSpan((int)offset, NwfsLayout.VolumeTableBytes);
      if (IsSupportedVolumeTable(table))
        tables.Add(table.ToArray());
    }

    var selectedTable = SelectRedundantCopy(tables);
    if (selectedTable == null)
      return null;

    var entry = selectedTable.AsSpan(32, NwfsLayout.VolumeEntryBytes);
    var nameLength = entry[0];
    if (nameLength is 0 or > NwfsLayout.MaxVolumeNameLength)
      return null;
    var volumeName = Encoding.ASCII.GetString(entry.Slice(1, nameLength));

    var lastSegment = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
    var signature = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
    var segmentIndex = (signature >> 16) & 0xFF;
    var segmentCount = (signature >> 8) & 0xFF;
    var clusterSize = NwfsLayout.ClusterSizeFromCode((int)(signature & 0xFF));
    if (lastSegment != 0 || segmentIndex != 0 || segmentCount != 1
        || !NwfsLayout.IsValidBlockSize(clusterSize))
      return null;

    var volumeRootSectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
    var segmentSectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]);
    var segmentClusterStart = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
    var fat1 = BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]);
    var fat2 = BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]);
    var directory1 = BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]);
    var directory2 = BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]);

    if (clusterCount == 0 || segmentClusterStart != 0
        || volumeRootSectors < NwfsLayout.VolumeRootSector
        || volumeRootSectors % NwfsLayout.SectorsPerIoBlock != 0)
      return null;

    var blocksPerCluster = NwfsLayout.BlocksPerCluster(clusterSize);
    var expectedSegmentSectors = checked(
      clusterCount * (uint)blocksPerCluster * (uint)NwfsLayout.SectorsPerIoBlock);
    if (segmentSectors != expectedSegmentSectors)
      return null;

    var expectedGap = checked((uint)NwfsLayout.FatGapClusters(clusterSize));
    if (fat1 != 0 || fat2 != expectedGap
        || directory1 >= clusterCount || directory2 >= clusterCount
        || directory1 == directory2)
      return null;

    var volumeOffset = checked(
      logicalPartitionOffset + (long)volumeRootSectors * NwfsLayout.SectorSize);
    var volumeBytes = checked((long)clusterCount * clusterSize);
    if (volumeOffset < 0 || volumeOffset + volumeBytes > image.LongLength)
      return null;

    var fatPhysicalBlocks = NwfsLayout.FatPhysicalBlockCount(clusterCount, clusterSize);
    var managedClusters = checked((int)clusterCount);
    var fatIndex = new uint[managedClusters];
    var fatNext = new uint[managedClusters];
    if (!TryReadFat(
          image,
          volumeOffset,
          clusterCount,
          fatPhysicalBlocks,
          fatIndex,
          fatNext))
      return null;

    var provisional = new NwfsReader(
      image,
      partition.Offset,
      logicalPartitionOffset,
      volumeOffset,
      clusterSize,
      directory1,
      directory2,
      clusterCount,
      fatPhysicalBlocks,
      fatIndex,
      fatNext,
      volumeName,
      []);

    if (!provisional.ValidateFatMetadataHeads(fat1, fat2))
      return null;

    var copy1Valid = provisional.TryReadDirectoryCopy(directory1, out var copy1);
    var copy2Valid = provisional.TryReadDirectoryCopy(directory2, out var copy2);
    List<Raw>? directory;
    if (copy1Valid && copy2Valid) {
      if (!DirectoryCopiesEqual(copy1, copy2))
        return null;
      directory = copy1;
    } else if (copy1Valid) {
      directory = copy1;
    } else if (copy2Valid) {
      directory = copy2;
    } else {
      return null;
    }

    var reader = new NwfsReader(
      image,
      partition.Offset,
      logicalPartitionOffset,
      volumeOffset,
      clusterSize,
      directory1,
      directory2,
      clusterCount,
      fatPhysicalBlocks,
      fatIndex,
      fatNext,
      volumeName,
      directory);

    return reader.ValidateFileChains() ? reader : null;
  }

  private bool ValidateFatMetadataHeads(uint fat1, uint fat2) {
    var expectedClusters = this._fatPhysicalBlocks / this._blocksPerCluster;
    return this.TryWalkIndexedChain(fat1, out var first)
           && this.TryWalkIndexedChain(fat2, out var second)
           && first.Count == expectedClusters
           && second.Count == expectedClusters
           && first.Select(static x => x.Index).SequenceEqual(Enumerable.Range(0, expectedClusters).Select(static i => (uint)i))
           && second.Select(static x => x.Index).SequenceEqual(Enumerable.Range(0, expectedClusters).Select(static i => (uint)i));
  }

  private bool ValidateFileChains() {
    foreach (var item in this._directory) {
      if (item.IsDirectory)
        continue;
      if (item.Length == 0) {
        if (item.FirstBlock != NwfsLayout.EndOfChain)
          return false;
        continue;
      }
      if (item.FirstBlock == NwfsLayout.EndOfChain)
        return false;
      if (!this.TryWalkIndexedChain(item.FirstBlock, out var chain) || chain.Count == 0)
        return false;

      uint previous = uint.MaxValue;
      foreach (var (_, index) in chain) {
        if (previous != uint.MaxValue && index <= previous)
          return false;
        var offset = (long)index * this._clusterSize;
        if (offset >= item.Length)
          return false;
        previous = index;
      }

      var last = chain[^1].Index;
      if ((long)last * this._clusterSize >= item.Length)
        return false;
    }
    return true;
  }

  private static bool TryReadMasterMetadata(
      ReadOnlySpan<byte> image,
      Partition partition,
      out uint hotfixSizeSectors,
      out uint logicalSectors) {
    hotfixSizeSectors = 0;
    logicalSectors = 0;
    var valid = new List<(uint HotfixSize, uint LogicalSectors)>();

    foreach (var sector in NwfsLayout.MasterCopySectors) {
      var offset = checked(partition.Offset + (long)sector * NwfsLayout.SectorSize);
      if (offset < 0 || offset + NwfsLayout.IoBlockSize > image.Length)
        continue;

      var block = image.Slice((int)offset, NwfsLayout.IoBlockSize);
      var hotfix = block[..NwfsLayout.SectorSize];
      var mirror = block.Slice(NwfsLayout.SectorSize, NwfsLayout.SectorSize);
      if (!hotfix[..8].SequenceEqual("HOTFIX00"u8)
          || !mirror[..8].SequenceEqual("MIRROR00"u8))
        continue;

      var partitionId = BinaryPrimitives.ReadUInt32LittleEndian(hotfix[8..]);
      var mirrorPartitionId = BinaryPrimitives.ReadUInt32LittleEndian(mirror[8..]);
      var total = BinaryPrimitives.ReadUInt32LittleEndian(hotfix[20..]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(hotfix[24..]);
      var mirrorTotal = BinaryPrimitives.ReadUInt32LittleEndian(mirror[24..]);
      if (partitionId == 0 || partitionId != mirrorPartitionId
          || total == 0 || size == 0 || mirrorTotal != total
          || (ulong)total + size > partition.Sectors)
        continue;

      valid.Add((size, total));
    }

    if (valid.Count == 0)
      return false;

    var best = valid
      .GroupBy(static x => (x.HotfixSize, x.LogicalSectors))
      .OrderByDescending(static g => g.Count())
      .First();
    if (valid.Count > 1 && best.Count() * 2 <= valid.Count)
      return false;

    hotfixSizeSectors = best.Key.HotfixSize;
    logicalSectors = best.Key.LogicalSectors;
    return true;
  }

  private static byte[]? SelectRedundantCopy(List<byte[]> copies) {
    if (copies.Count == 0)
      return null;
    if (copies.Count == 1)
      return copies[0];

    var groups = new List<(byte[] Value, int Count)>();
    foreach (var copy in copies) {
      var existing = groups.FindIndex(g => g.Value.AsSpan().SequenceEqual(copy));
      if (existing < 0) {
        groups.Add((copy, 1));
      } else {
        var entry = groups[existing];
        groups[existing] = (entry.Value, entry.Count + 1);
      }
    }

    var best = groups.OrderByDescending(static g => g.Count).First();
    return best.Count * 2 > copies.Count ? best.Value : null;
  }

  private static bool IsSupportedVolumeTable(ReadOnlySpan<byte> table) {
    if (table.Length < NwfsLayout.VolumeTableBytes
        || !table[..16].SequenceEqual("NetWare Volumes\0"u8))
      return false;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(table[16..]);
    return count == 1;
  }

  private static bool TryReadFat(
      ReadOnlySpan<byte> image,
      long volumeOffset,
      uint clusterCount,
      int fatPhysicalBlocks,
      uint[] fatIndex,
      uint[] fatNext) {
    for (var streamBlock = 0; streamBlock < fatPhysicalBlocks; ++streamBlock) {
      var primaryBlock = NwfsLayout.FatPrimaryPhysicalBlock(streamBlock);
      var mirrorBlock = NwfsLayout.FatMirrorPhysicalBlock(streamBlock);
      var primaryOffset = checked(volumeOffset + (long)primaryBlock * NwfsLayout.IoBlockSize);
      var mirrorOffset = checked(volumeOffset + (long)mirrorBlock * NwfsLayout.IoBlockSize);
      if (primaryOffset < 0 || mirrorOffset < 0
          || primaryOffset + NwfsLayout.IoBlockSize > image.Length
          || mirrorOffset + NwfsLayout.IoBlockSize > image.Length)
        return false;

      var primary = image.Slice((int)primaryOffset, NwfsLayout.IoBlockSize);
      var mirror = image.Slice((int)mirrorOffset, NwfsLayout.IoBlockSize);
      var firstEntry = checked(streamBlock * NwfsLayout.FatEntriesPerIoBlock);
      var primaryValid = IsFatBlockValid(primary, firstEntry, clusterCount);
      var mirrorValid = IsFatBlockValid(mirror, firstEntry, clusterCount);
      ReadOnlySpan<byte> selected;
      if (primaryValid && mirrorValid) {
        if (!primary.SequenceEqual(mirror))
          return false;
        selected = primary;
      } else if (primaryValid) {
        selected = primary;
      } else if (mirrorValid) {
        selected = mirror;
      } else {
        return false;
      }

      var count = Math.Min(
        NwfsLayout.FatEntriesPerIoBlock,
        Math.Max(0, fatIndex.Length - firstEntry));
      for (var i = 0; i < count; ++i) {
        var at = i * NwfsLayout.FatEntryBytes;
        fatIndex[firstEntry + i] = BinaryPrimitives.ReadUInt32LittleEndian(selected[at..]);
        fatNext[firstEntry + i] = BinaryPrimitives.ReadUInt32LittleEndian(selected[(at + 4)..]);
      }
    }

    return true;
  }

  private static bool IsFatBlockValid(
      ReadOnlySpan<byte> block,
      int firstEntry,
      uint clusterCount) {
    for (var i = 0; i < NwfsLayout.FatEntriesPerIoBlock; ++i) {
      var at = i * NwfsLayout.FatEntryBytes;
      var index = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
      var next = BinaryPrimitives.ReadUInt32LittleEndian(block[(at + 4)..]);
      var cluster = (long)firstEntry + i;
      if (cluster >= clusterCount) {
        if (index != 0 || next != 0)
          return false;
        continue;
      }

      if (next == NwfsLayout.FreeFatCluster) {
        if (index != NwfsLayout.FreeFatIndex)
          return false;
        continue;
      }

      if (index >= 0x01000000)
        return false;
      if (next == NwfsLayout.EndOfChain)
        continue;
      if ((next & 0x80000000) != 0 || next >= clusterCount)
        return false;
    }

    return true;
  }

  private bool TryReadDirectoryCopy(uint head, out List<Raw> items) {
    items = [];
    if (!this.TryWalkIndexedChain(head, out var chain) || chain.Count == 0)
      return false;

    var entriesPerCluster = this._clusterSize / NwfsLayout.DirectoryEntryBytes;
    var sawRoot = false;
    foreach (var (cluster, index) in chain) {
      if (index >= this._clusterCount)
        return false;
      var at = this.BlockOffset(cluster);
      if (at < 0 || at + this._clusterSize > this._image.LongLength)
        return false;

      var recordBase = checked(index * (uint)entriesPerCluster);
      for (var i = 0; i < entriesPerCluster; ++i) {
        var recordNumber = checked(recordBase + (uint)i);
        var entry = this._image.AsSpan(
          checked((int)(at + (long)i * NwfsLayout.DirectoryEntryBytes)),
          NwfsLayout.DirectoryEntryBytes);
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(entry);

        if (recordNumber == NwfsLayout.RootDirectoryRecord) {
          if (parent != NwfsLayout.RootNode
              || entry[10] != NwfsLayout.DosNameSpace
              || (entry[9] & NwfsLayout.FlagSubdirectory) == 0)
            return false;
          sawRoot = true;
          continue;
        }

        if (parent == NwfsLayout.FreeNode)
          continue;
        if (parent is NwfsLayout.TrusteeNode or NwfsLayout.RootNode or NwfsLayout.RestrictionNode)
          return false;

        var flags = entry[9];
        if ((flags & (NwfsLayout.FlagDeleted3 | NwfsLayout.FlagDeleted4)) != 0)
          return false;
        if (entry[10] != NwfsLayout.DosNameSpace
            || (flags & NwfsLayout.FlagPrimaryNamespace) == 0)
          return false;

        var nameLength = entry[11];
        if (nameLength is 0 or > NwfsLayout.MaxNameLength)
          return false;
        var name = Encoding.ASCII.GetString(entry.Slice(12, nameLength));
        var isDirectory = (flags & NwfsLayout.FlagSubdirectory) != 0;
        if (isDirectory) {
          items.Add(new Raw(recordNumber, parent, true, name, 0, NwfsLayout.EndOfChain));
        } else {
          var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]);
          var first = BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]);
          if (first != NwfsLayout.EndOfChain && first >= this._clusterCount)
            return false;
          items.Add(new Raw(recordNumber, parent, false, name, length, first));
        }
      }
    }

    return sawRoot;
  }

  private static bool DirectoryCopiesEqual(List<Raw> first, List<Raw> second)
    => first.Count == second.Count && first.SequenceEqual(second);

  private bool TryWalkIndexedChain(uint firstCluster, out List<(uint Cluster, uint Index)> chain) {
    chain = [];
    if (firstCluster == NwfsLayout.EndOfChain)
      return true;

    var current = firstCluster;
    var seen = new HashSet<uint>();
    while (current != NwfsLayout.EndOfChain) {
      if (current >= this._clusterCount || !seen.Add(current))
        return false;

      var currentIndex = checked((int)current);
      var next = this._fatNext[currentIndex];
      var index = this._fatIndex[currentIndex];
      if (next == NwfsLayout.FreeFatCluster)
        return false;

      chain.Add((current, index));
      current = next;
      if (chain.Count > this._clusterCount)
        return false;
    }

    return true;
  }

  internal long BlockOffset(uint cluster)
    => cluster >= this._clusterCount
      ? -1
      : checked(this._volumeOffset + (long)cluster * this._clusterSize);

  internal bool TryReadFatEntry(uint cluster, out uint index, out uint next) {
    if (cluster >= this._clusterCount) {
      index = next = NwfsLayout.EndOfChain;
      return false;
    }

    var at = checked((int)cluster);
    index = this._fatIndex[at];
    next = this._fatNext[at];
    return true;
  }

  internal uint NextBlock(uint cluster)
    => this.TryReadFatEntry(cluster, out _, out var next) ? next : NwfsLayout.EndOfChain;

  internal IEnumerable<uint> WalkChain(uint firstCluster) {
    if (!this.TryWalkIndexedChain(firstCluster, out var chain))
      yield break;
    foreach (var (cluster, _) in chain)
      yield return cluster;
  }

  internal IEnumerable<(uint Cluster, uint Index)> WalkIndexedChain(uint firstCluster) {
    if (!this.TryWalkIndexedChain(firstCluster, out var chain))
      yield break;
    foreach (var item in chain)
      yield return item;
  }

  internal IEnumerable<int> EnumerateFatPhysicalBlocks() {
    for (var i = 0; i < this._fatPhysicalBlocks; ++i) {
      yield return NwfsLayout.FatPrimaryPhysicalBlock(i);
      yield return NwfsLayout.FatMirrorPhysicalBlock(i);
    }
  }

  /// <summary>Everything on the volume, each with the path it is reached by.</summary>
  public List<Item> List() {
    var found = new List<Item>();
    var pending = new Queue<(uint Id, string Prefix)>();
    pending.Enqueue((NwfsLayout.RootDirectoryRecord, ""));
    var seen = new HashSet<uint> { NwfsLayout.RootDirectoryRecord };

    while (pending.Count > 0) {
      var (id, prefix) = pending.Dequeue();
      foreach (var item in this._directory) {
        if (item.ParentId != id)
          continue;

        var path = prefix.Length == 0 ? item.Name : prefix + "/" + item.Name;
        if (item.IsDirectory) {
          found.Add(new Item(path, true, 0, NwfsLayout.EndOfChain));
          if (seen.Add(item.RecordNumber))
            pending.Enqueue((item.RecordNumber, path));
        } else {
          found.Add(new Item(path, false, item.Length, item.FirstBlock));
        }
      }
    }

    return found;
  }

  /// <summary>The bytes of the file at <paramref name="path" />, or null if there is none.</summary>
  public byte[]? ReadFile(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var wanted = path.Replace('\\', '/').Trim('/');
    var item = this.List().Find(i => !i.IsDirectory
                                     && string.Equals(i.Path, wanted, StringComparison.OrdinalIgnoreCase));
    return item == null ? null : this.Read(item);
  }

  /// <summary>The bytes of <paramref name="item" />, followed through the FAT.</summary>
  public byte[] Read(Item item) {
    ArgumentNullException.ThrowIfNull(item);
    if (item.Length > int.MaxValue)
      throw new InvalidDataException("NWFS file is too large for the managed reader.");

    var data = new byte[(int)item.Length];
    if (item.Length == 0)
      return data;

    if (!this.TryWalkIndexedChain(item.FirstBlock, out var chain))
      throw new InvalidDataException("NWFS file has a corrupt FAT chain.");

    var writtenLogical = new HashSet<uint>();
    foreach (var (cluster, index) in chain) {
      if (!writtenLogical.Add(index))
        throw new InvalidDataException("NWFS file reuses a logical FAT index.");

      var destination = checked((long)index * this._clusterSize);
      if (destination >= data.LongLength)
        throw new InvalidDataException("NWFS FAT index lies beyond the file length.");

      var source = this.BlockOffset(cluster);
      if (source < 0 || source + this._clusterSize > this._image.LongLength)
        throw new InvalidDataException("NWFS file cluster lies beyond the image.");

      var take = (int)Math.Min(this._clusterSize, data.LongLength - destination);
      this._image.AsSpan((int)source, take).CopyTo(data.AsSpan((int)destination, take));
    }

    return data;
  }

  private readonly record struct Partition(long Offset, uint Sectors);

  private static Partition FindPartition(ReadOnlySpan<byte> image) {
    const int tableOffset = 446;
    if (image.Length >= NwfsLayout.SectorSize
        && image[510] == 0x55 && image[511] == 0xAA) {
      for (var i = 0; i < 4; ++i) {
        var entry = image.Slice(tableOffset + i * 16, 16);
        if (entry[4] is not (0x65 or 0x77))
          continue;
        var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        var sectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        var offset = checked((long)start * NwfsLayout.SectorSize);
        if (sectors > 0 && offset >= 0
            && offset + (long)sectors * NwfsLayout.SectorSize <= image.Length)
          return new Partition(offset, sectors);
      }
    }

    var bareSectors = image.Length / NwfsLayout.SectorSize;
    return new Partition(0, checked((uint)bareSectors));
  }
}
