#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nwfs;

/// <summary>
/// Writes the managed, writable Traditional NetWare profile: one 0x65
/// partition, one unmirrored logical partition, one volume segment, DOS
/// namespace entries and ordinary FAT chains.
/// </summary>
public sealed class NwfsWriter {
  private const uint PartitionId = 0x55500001;
  private const uint MirrorGroupId = 0x55500002;
  private const uint MirrorClosedStatus = 0x534F4C43;

  private readonly List<(string Path, byte[] Data)> _files = [];
  private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>NetWare allocation-cluster size: 4, 8, 16, 32 or 64 KiB.</summary>
  public int BlockSize { get; set; } = 4096;

  /// <summary>Length-prefixed Traditional volume name, at most 15 ASCII characters.</summary>
  public string VolumeName { get; set; } = "SYS";

  /// <summary>Where the NetWare partition begins, in 512-byte sectors.</summary>
  public uint PartitionStartSector { get; set; } = 32;

  /// <summary>Timestamp used for freshly-authored directory records.</summary>
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;

  /// <summary>
  /// Minimum total image length. The image is rounded up to a complete
  /// allocation cluster and the added clusters stay free.
  /// </summary>
  public long MinimumImageSize { get; set; }

  /// <summary>Adds a file. Directories in <paramref name="path" /> are made as needed.</summary>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((NormalizePath(path), data));
  }

  /// <summary>Adds an explicit directory, including an empty one.</summary>
  public void AddDirectory(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var normalized = NormalizePath(path);
    if (normalized.Length > 0)
      this._directories.Add(normalized);
  }

  private sealed class DirectoryNode {
    public required string Name;
    public required uint RecordNumber;
    public required uint ParentRecordNumber;
    public readonly Dictionary<string, DirectoryNode> Children = new(StringComparer.OrdinalIgnoreCase);
  }

  private sealed record PlacedFile(DirectoryNode Parent, string Name, byte[] Data, uint RecordNumber);

  /// <summary>Builds the image.</summary>
  public byte[] Build() {
    if (!NwfsLayout.IsValidBlockSize(this.BlockSize))
      throw new InvalidOperationException(
        $"cluster size {this.BlockSize} is not a Traditional NWFS cluster size (4, 8, 16, 32 or 64 KiB)");
    if (this.MinimumImageSize < 0)
      throw new InvalidOperationException("minimum image size cannot be negative");

    var volumeName = NormalizeVolumeName(this.VolumeName);
    var root = new DirectoryNode {
      Name = "",
      RecordNumber = NwfsLayout.RootDirectoryRecord,
      ParentRecordNumber = NwfsLayout.RootDirectoryRecord,
    };
    var directories = new List<DirectoryNode>();

    DirectoryNode EnsureDirectory(string path) {
      var here = root;
      foreach (var piece in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
        var name = NormalizeDosName(piece);
        if (!here.Children.TryGetValue(name, out var child)) {
          child = new DirectoryNode {
            Name = name,
            RecordNumber = checked((uint)directories.Count + 1),
            ParentRecordNumber = here.RecordNumber,
          };
          here.Children.Add(name, child);
          directories.Add(child);
        }
        here = child;
      }
      return here;
    }

    foreach (var path in this._directories.Order(StringComparer.OrdinalIgnoreCase))
      _ = EnsureDirectory(path);

    var pendingFiles = new List<(DirectoryNode Parent, string Name, byte[] Data)>();
    foreach (var (path, data) in this._files) {
      var pieces = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (pieces.Length == 0)
        throw new InvalidOperationException("a file needs a name");
      var parent = pieces.Length == 1 ? root : EnsureDirectory(string.Join('/', pieces[..^1]));
      pendingFiles.Add((parent, NormalizeDosName(pieces[^1]), data));
    }

    var placedFiles = new List<PlacedFile>(pendingFiles.Count);
    var firstFileRecord = checked((uint)directories.Count + 1);
    for (var i = 0; i < pendingFiles.Count; ++i) {
      var file = pendingFiles[i];
      placedFiles.Add(new PlacedFile(
        file.Parent,
        file.Name,
        file.Data,
        checked(firstFileRecord + (uint)i)));
    }

    var entriesPerCluster = this.BlockSize / NwfsLayout.DirectoryEntryBytes;
    var usedDirectoryEntries = checked(1 + directories.Count + placedFiles.Count);
    var directoryClusters = Math.Max(1,
      (usedDirectoryEntries + entriesPerCluster - 1) / entriesPerCluster);

    var fileClusterCount = 0;
    foreach (var file in placedFiles)
      fileClusterCount = checked(fileClusterCount
        + (int)NwfsLayout.DivideRoundUp(file.Data.LongLength, this.BlockSize));

    var volumeOffset = NwfsLayout.VolumeOffset(this.PartitionStartSector);
    var minimumClusters = this.MinimumImageSize <= volumeOffset
      ? 0u
      : checked((uint)NwfsLayout.DivideRoundUp(this.MinimumImageSize - volumeOffset, this.BlockSize));

    var plan = NwfsLayout.Plan(this.BlockSize, directoryClusters, fileClusterCount, minimumClusters);
    var imageLength = NwfsLayout.TightImageLength(this.PartitionStartSector, this.BlockSize, plan.ClusterCount);
    if (imageLength > int.MaxValue)
      throw new InvalidOperationException("NWFS writer currently supports images up to 2 GiB");

    var image = new byte[(int)imageLength];
    var stamp = DosTimestamp(this.Timestamp);
    var partitionOffset = NwfsLayout.PartitionOffset(this.PartitionStartSector);
    var logicalPartitionOffset = NwfsLayout.LogicalPartitionOffset(this.PartitionStartSector);
    var logicalBlocks = checked(NwfsLayout.VolumeSegmentStartBlock
      + (int)((long)plan.ClusterCount * plan.BlocksPerCluster));
    var partitionSectors = checked((uint)((long)(NwfsLayout.HotfixBlocks + logicalBlocks)
      * NwfsLayout.SectorsPerIoBlock));

    WritePartitionTable(image, this.PartitionStartSector, partitionSectors);
    WriteMasterMetadataCopies(image, partitionOffset, checked((uint)(logicalBlocks * NwfsLayout.SectorsPerIoBlock)), stamp);
    WriteHotfixTables(image, partitionOffset);
    WriteVolumeTableCopies(image, logicalPartitionOffset, volumeName, plan);

    var managedClusterCount = checked((int)plan.ClusterCount);
    var fatIndex = new uint[managedClusterCount];
    var fatNext = new uint[managedClusterCount];

    MarkChain(fatIndex, fatNext, plan.Fat1Clusters);
    MarkChain(fatIndex, fatNext, plan.Fat2Clusters);
    MarkChain(fatIndex, fatNext, plan.Directory1Clusters);
    MarkChain(fatIndex, fatNext, plan.Directory2Clusters);

    var fileChains = new Dictionary<uint, uint[]>();
    var dataCursor = 0;
    foreach (var file in placedFiles) {
      var count = (int)NwfsLayout.DivideRoundUp(file.Data.LongLength, this.BlockSize);
      var chain = count == 0 ? [] : plan.DataClusters.AsSpan(dataCursor, count).ToArray();
      dataCursor += count;
      fileChains.Add(file.RecordNumber, chain);
      MarkChain(fatIndex, fatNext, chain);
    }

    WriteFatCopies(image, volumeOffset, plan, fatIndex, fatNext);

    var directoryBytes = BuildDirectoryTable(
      directories,
      placedFiles,
      fileChains,
      directoryClusters,
      entriesPerCluster,
      stamp);
    WriteClusterChain(image, volumeOffset, plan.Directory1Clusters, directoryBytes);
    WriteClusterChain(image, volumeOffset, plan.Directory2Clusters, directoryBytes);

    foreach (var file in placedFiles) {
      var chain = fileChains[file.RecordNumber];
      var remaining = file.Data.AsSpan();
      foreach (var cluster in chain) {
        var offset = checked((int)(volumeOffset + (long)cluster * this.BlockSize));
        var take = Math.Min(this.BlockSize, remaining.Length);
        remaining[..take].CopyTo(image.AsSpan(offset, take));
        remaining = remaining[take..];
      }
    }

    return image;
  }

  private static byte[] BuildDirectoryTable(
      List<DirectoryNode> directories,
      List<PlacedFile> files,
      IReadOnlyDictionary<uint, uint[]> fileChains,
      int directoryClusters,
      int entriesPerCluster,
      uint stamp) {
    var entries = checked(directoryClusters * entriesPerCluster);
    var table = new byte[checked(entries * NwfsLayout.DirectoryEntryBytes)];
    for (var i = 0; i < entries; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(
        table.AsSpan(i * NwfsLayout.DirectoryEntryBytes),
        NwfsLayout.FreeNode);

    WriteRootEntry(table.AsSpan(0, NwfsLayout.DirectoryEntryBytes), stamp);

    foreach (var directory in directories) {
      var entry = table.AsSpan(
        checked((int)directory.RecordNumber * NwfsLayout.DirectoryEntryBytes),
        NwfsLayout.DirectoryEntryBytes);
      WriteCommonEntry(
        entry,
        directory.ParentRecordNumber,
        NwfsLayout.AttributeDirectory,
        (byte)(NwfsLayout.FlagSubdirectory | NwfsLayout.FlagPrimaryNamespace),
        directory.Name,
        stamp);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[100..], 0xFFFF);
    }

    foreach (var file in files) {
      var entry = table.AsSpan(
        checked((int)file.RecordNumber * NwfsLayout.DirectoryEntryBytes),
        NwfsLayout.DirectoryEntryBytes);
      WriteCommonEntry(
        entry,
        file.Parent.RecordNumber,
        NwfsLayout.AttributeArchive,
        NwfsLayout.FlagPrimaryNamespace,
        file.Name,
        stamp);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[48..], checked((uint)file.Data.Length));
      var chain = fileChains[file.RecordNumber];
      BinaryPrimitives.WriteUInt32LittleEndian(entry[52..],
        chain.Length == 0 ? NwfsLayout.EndOfChain : chain[0]);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[96..], 0xFFFF);
    }

    return table;
  }

  private static void WriteRootEntry(Span<byte> entry, uint stamp) {
    entry.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(entry, NwfsLayout.RootNode);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], NwfsLayout.AttributeDirectory);
    entry[9] = (byte)(NwfsLayout.FlagSubdirectory | NwfsLayout.FlagPrimaryNamespace);
    entry[10] = NwfsLayout.DosNameSpace;
    entry[11] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], NwfsLayout.SupervisorObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], stamp);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[100..], 0xFFFF);
  }

  private static void WriteCommonEntry(
      Span<byte> entry,
      uint parent,
      uint attributes,
      byte flags,
      string name,
      uint stamp) {
    entry.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(entry, parent);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], attributes);
    entry[9] = flags;
    entry[10] = NwfsLayout.DosNameSpace;
    var bytes = Encoding.ASCII.GetBytes(name);
    entry[11] = checked((byte)bytes.Length);
    bytes.CopyTo(entry[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], NwfsLayout.SupervisorObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], stamp);
  }

  private static void MarkChain(uint[] indices, uint[] next, ReadOnlySpan<uint> chain) {
    for (var i = 0; i < chain.Length; ++i) {
      var cluster = chain[i];
      if (cluster >= (uint)indices.Length)
        throw new InvalidOperationException("NWFS layout allocated a cluster beyond the volume.");
      var at = checked((int)cluster);
      indices[at] = checked((uint)i);
      next[at] = i + 1 == chain.Length ? NwfsLayout.EndOfChain : chain[i + 1];
    }
  }

  private static void WriteFatCopies(
      byte[] image,
      long volumeOffset,
      NwfsLayout.VolumePlan plan,
      uint[] indices,
      uint[] next) {
    var block = new byte[NwfsLayout.IoBlockSize];
    for (var streamBlock = 0; streamBlock < plan.FatPhysicalBlocks; ++streamBlock) {
      block.AsSpan().Clear();
      var firstEntry = checked(streamBlock * NwfsLayout.FatEntriesPerIoBlock);
      var entries = Math.Min(
        NwfsLayout.FatEntriesPerIoBlock,
        Math.Max(0, indices.Length - firstEntry));

      for (var i = 0; i < entries; ++i) {
        var at = i * NwfsLayout.FatEntryBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), indices[firstEntry + i]);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 4), next[firstEntry + i]);
      }

      var primaryBlock = NwfsLayout.FatPrimaryPhysicalBlock(streamBlock);
      var mirrorBlock = NwfsLayout.FatMirrorPhysicalBlock(streamBlock);
      block.CopyTo(image.AsSpan(checked((int)(volumeOffset + (long)primaryBlock * NwfsLayout.IoBlockSize))));
      block.CopyTo(image.AsSpan(checked((int)(volumeOffset + (long)mirrorBlock * NwfsLayout.IoBlockSize))));
    }
  }

  private void WriteVolumeTableCopies(
      byte[] image,
      long logicalPartitionOffset,
      string volumeName,
      NwfsLayout.VolumePlan plan) {
    var table = new byte[NwfsLayout.VolumeTableBytes];
    "NetWare Volumes\0"u8.CopyTo(table);
    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(16), 1);

    var entry = table.AsSpan(32, NwfsLayout.VolumeEntryBytes);
    var nameBytes = Encoding.ASCII.GetBytes(volumeName);
    entry[0] = checked((byte)nameBytes.Length);
    nameBytes.CopyTo(entry[1..]);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], 0);
    var signature = checked((uint)(0x100 | NwfsLayout.ClusterCode(this.BlockSize)));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], signature);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], NwfsLayout.VolumeRootSector);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..],
      checked(plan.ClusterCount * (uint)plan.BlocksPerCluster * (uint)NwfsLayout.SectorsPerIoBlock));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], plan.ClusterCount);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], plan.Fat1);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[44..], plan.Fat2);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[48..], plan.Directory1);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[52..], plan.Directory2);

    foreach (var logicalBlock in NwfsLayout.VolumeTableLogicalBlocks) {
      var offset = checked((int)(logicalPartitionOffset + (long)logicalBlock * NwfsLayout.IoBlockSize));
      table.CopyTo(image.AsSpan(offset, table.Length));
    }
  }

  private static void WriteMasterMetadataCopies(
      byte[] image,
      long partitionOffset,
      uint logicalSectors,
      uint stamp) {
    var block = new byte[NwfsLayout.IoBlockSize];
    var hotfix = block.AsSpan(0, NwfsLayout.SectorSize);
    "HOTFIX00"u8.CopyTo(hotfix);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[8..], PartitionId);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[12..], NwfsLayout.HotfixFlags);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[16..], NwfsLayout.FormatStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[20..], logicalSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[24..], NwfsLayout.HotfixSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[28..], checked((uint)(20 * NwfsLayout.SectorsPerIoBlock)));
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[32..], checked((uint)(28 * NwfsLayout.SectorsPerIoBlock)));
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[36..], checked((uint)(36 * NwfsLayout.SectorsPerIoBlock)));
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[40..], checked((uint)(44 * NwfsLayout.SectorsPerIoBlock)));

    var mirror = block.AsSpan(NwfsLayout.SectorSize, NwfsLayout.SectorSize);
    "MIRROR00"u8.CopyTo(mirror);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[8..], PartitionId);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[12..], NwfsLayout.MirrorInSyncFlags);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[16..], NwfsLayout.FormatStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[20..], NwfsLayout.MirrorBaseStatus);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[24..], logicalSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[28..], MirrorGroupId);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[32..], PartitionId);

    var internalMirror = block.AsSpan(NwfsLayout.SectorSize * 2, NwfsLayout.SectorSize);
    "NWVP MIRROR 0001"u8.CopyTo(internalMirror);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[16..], MirrorGroupId);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[20..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[24..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[32..], PartitionId);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[36..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[40..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(internalMirror[44..], MirrorClosedStatus);

    foreach (var sector in NwfsLayout.MasterCopySectors) {
      var offset = checked((int)(partitionOffset + (long)sector * NwfsLayout.SectorSize));
      block.CopyTo(image.AsSpan(offset, block.Length));
    }
  }

  private static void WriteHotfixTables(byte[] image, long partitionOffset) {
    var hotfix = new byte[NwfsLayout.IoBlockSize];
    var bad = new byte[NwfsLayout.IoBlockSize];

    static void MarkReserved(Span<byte> table, int entry)
      => BinaryPrimitives.WriteUInt32LittleEndian(table[(entry * sizeof(uint))..], uint.MaxValue);

    for (var i = 0; i < 20; ++i) {
      MarkReserved(hotfix, i);
      MarkReserved(bad, i);
    }
    foreach (var i in new[] { 20, 28, 36, 44 }) {
      MarkReserved(hotfix, i);
      MarkReserved(bad, i);
    }

    hotfix.CopyTo(image.AsSpan(checked((int)(partitionOffset + 20L * NwfsLayout.IoBlockSize))));
    bad.CopyTo(image.AsSpan(checked((int)(partitionOffset + 28L * NwfsLayout.IoBlockSize))));
    hotfix.CopyTo(image.AsSpan(checked((int)(partitionOffset + 36L * NwfsLayout.IoBlockSize))));
    bad.CopyTo(image.AsSpan(checked((int)(partitionOffset + 44L * NwfsLayout.IoBlockSize))));
  }

  private void WriteClusterChain(
      byte[] image,
      long volumeOffset,
      ReadOnlySpan<uint> chain,
      ReadOnlySpan<byte> data) {
    var remaining = data;
    foreach (var cluster in chain) {
      var offset = checked((int)(volumeOffset + (long)cluster * this.BlockSize));
      var take = Math.Min(this.BlockSize, remaining.Length);
      if (take > 0)
        remaining[..take].CopyTo(image.AsSpan(offset, take));
      remaining = remaining[take..];
    }
    if (!remaining.IsEmpty)
      throw new InvalidOperationException("NWFS cluster chain is shorter than its data.");
  }

  private static string NormalizePath(string path)
    => path.Replace('\\', '/').Trim('/');

  private static string NormalizeDosName(string name) {
    var upper = name.ToUpperInvariant();
    if (upper.Length is 0 or > NwfsLayout.MaxNameLength || upper.Any(static c => c > 0x7F))
      throw new InvalidOperationException($"'{name}' must be 1 to 12 ASCII characters long");
    return upper;
  }

  private static string NormalizeVolumeName(string name) {
    var upper = (name ?? string.Empty).Trim().ToUpperInvariant();
    if (upper.Length is 0 or > NwfsLayout.MaxVolumeNameLength || upper.Any(static c => c > 0x7F))
      throw new InvalidOperationException("volume name must be 1 to 15 ASCII characters long");
    return upper;
  }

  private static void WritePartitionTable(Span<byte> image, uint startSector, uint sectors) {
    const int tableOffset = 446;
    const byte netWare386 = 0x65;

    var e = image[tableOffset..];
    e[0] = 0;
    WriteChs(e[1..], startSector);
    e[4] = netWare386;
    WriteChs(e[5..], checked(startSector + sectors - 1));
    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], startSector);
    BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sectors);
    image[510] = 0x55;
    image[511] = 0xAA;
  }

  private static void WriteChs(Span<byte> chs, uint sector) {
    const int headsPerCylinder = 255;
    const int sectorsPerTrack = 63;

    var cylinder = sector / (headsPerCylinder * sectorsPerTrack);
    var head = sector / sectorsPerTrack % headsPerCylinder;
    var inTrack = sector % sectorsPerTrack + 1;
    if (cylinder > 1023) {
      chs[0] = 0xFE;
      chs[1] = 0xFF;
      chs[2] = 0xFF;
      return;
    }

    chs[0] = (byte)head;
    chs[1] = (byte)(inTrack | (cylinder >> 2 & 0xC0));
    chs[2] = (byte)cylinder;
  }

  private static uint DosTimestamp(DateTime when) {
    if (when.Year < 1980)
      when = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var date = (uint)(when.Year - 1980 << 9 | when.Month << 5 | when.Day);
    var time = (uint)(when.Hour << 11 | when.Minute << 5 | when.Second / 2);
    return date << 16 | time;
  }
}
