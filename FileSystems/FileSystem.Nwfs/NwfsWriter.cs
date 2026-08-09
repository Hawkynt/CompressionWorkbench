#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nwfs;

/// <summary>
/// Writes a NetWare 386 disk image: a partition table naming one NetWare
/// partition, the hotfix, mirror and volume headers that open it, and a volume
/// whose files a NetWare reader walks by the same route a real one does.
/// </summary>
/// <remarks>
/// <para><b>How a volume is found.</b> A reader takes the partition's start
/// from the partition table, reads the hotfix header at sector 32 of it, and
/// takes from there how many redirection sectors separate that header from the
/// volume area. The volume area names the volume, its block size, and the block
/// its directory begins at. Everything after the volume area is the data area,
/// and block numbers count from its first byte.</para>
///
/// <para><b>How a file is found.</b> The directory is a chain of blocks holding
/// fixed 128-byte entries, each naming the directory it sits in rather than
/// being nested under it — so a reader collects the lot and then filters by
/// parent. A file entry carries its length and its first block; the rest of it
/// is followed through the FAT, which sits at the very start of the data area
/// and gives, for each block, the block that comes after it.</para>
///
/// <para><b>What is written for the sake of being ordinary.</b> A volume
/// carries a volume-information entry ahead of its files, its unused directory
/// slots are marked available rather than left zero — a zeroed slot would read
/// as an unnamed file in the root — and the directory is written twice, the
/// second copy where a real volume keeps its own.</para>
/// </remarks>
public sealed class NwfsWriter {

  private readonly List<(string Path, byte[] Data)> _files = [];

  /// <summary>Bytes to a block. A NetWare volume may use 1 KB to 256 KB, by powers of two.</summary>
  public int BlockSize { get; set; } = 4096;

  /// <summary>What the volume is called. NetWare's own first volume is SYS.</summary>
  public string VolumeName { get; set; } = "SYS";

  /// <summary>Where the NetWare partition begins, in sectors.</summary>
  public uint PartitionStartSector { get; set; } = 32;

  /// <summary>Sectors between the hotfix header and the volume area.</summary>
  public uint RedirectionSectors { get; set; } = 128;

  /// <summary>When the volume and everything on it is dated.</summary>
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;

  /// <summary>Adds a file. Directories in <paramref name="path" /> are made as needed.</summary>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((path.Replace('\\', '/').Trim('/'), data));
  }

  private sealed class Directory {
    public required string Name;
    public required uint Id;
    public required uint ParentId;
    public readonly Dictionary<string, Directory> Children = new(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>Builds the image.</summary>
  public byte[] Build() {
    if (!NwfsLayout.IsValidBlockSize(this.BlockSize))
      throw new InvalidOperationException($"block size {this.BlockSize} is not one NetWare names");

    var volumeName = this.VolumeName.ToUpperInvariant();
    if (volumeName.Length is 0 or > NwfsLayout.MaxVolumeNameLength)
      throw new InvalidOperationException("volume name must be 1 to 19 characters");

    // The directory tree, and an id for every directory in it. The root is
    // zero, which is what entries at the top of the volume name as their parent.
    var root = new Directory { Name = "", Id = NwfsLayout.RootDirectoryId, ParentId = NwfsLayout.RootDirectoryId };
    var directories = new List<Directory>();
    var nextDirectoryId = NwfsLayout.RootDirectoryId + 1;

    var placed = new List<(Directory Parent, string Name, byte[] Data)>();
    foreach (var (path, data) in this._files) {
      var pieces = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (pieces.Length == 0) throw new InvalidOperationException("a file needs a name");

      var here = root;
      for (var i = 0; i < pieces.Length - 1; ++i) {
        var name = Normalise(pieces[i]);
        if (!here.Children.TryGetValue(name, out var child)) {
          child = new Directory { Name = name, Id = nextDirectoryId++, ParentId = here.Id };
          here.Children[name] = child;
          directories.Add(child);
        }

        here = child;
      }

      placed.Add((here, Normalise(pieces[^1]), data));
    }

    var entriesPerBlock = this.BlockSize / NwfsLayout.DirectoryEntryBytes;
    var usedEntries = 1 + directories.Count + placed.Count;   // the volume-information entry, then the rest
    var directoryBlocks = Math.Max(1, (usedEntries + entriesPerBlock - 1) / entriesPerBlock);

    var fileBlocks = 0;
    foreach (var (_, _, data) in placed)
      fileBlocks += (data.Length + this.BlockSize - 1) / this.BlockSize;

    // The FAT lives in the data area and so describes itself. Its size depends
    // on the block count, which depends on its size, so settle the two.
    var fatBlocks = 1;
    int totalBlocks;
    while (true) {
      totalBlocks = fatBlocks + directoryBlocks * 2 + fileBlocks;
      var needed = Math.Max(1, ((long)totalBlocks * NwfsLayout.FatEntryBytes + this.BlockSize - 1) / this.BlockSize);
      if (needed == fatBlocks) break;
      fatBlocks = (int)needed;
    }

    var firstDirectoryBlock = (uint)fatBlocks;
    var firstDirectoryCopyBlock = firstDirectoryBlock + (uint)directoryBlocks;
    var firstFileBlock = firstDirectoryCopyBlock + (uint)directoryBlocks;

    var hotfixOffset = (long)this.PartitionStartSector * NwfsLayout.SectorSize + NwfsLayout.HotfixOffsetInPartition;
    var volumeAreaOffset = hotfixOffset + (long)this.RedirectionSectors * NwfsLayout.SectorSize;
    var dataAreaOffset = volumeAreaOffset + NwfsLayout.VolumeAreaBytes;

    var image = new byte[dataAreaOffset + (long)totalBlocks * this.BlockSize];
    var stamp = DosTimestamp(this.Timestamp);

    WritePartitionTable(image, this.PartitionStartSector,
      (uint)((image.LongLength - (long)this.PartitionStartSector * NwfsLayout.SectorSize) / NwfsLayout.SectorSize));

    // Hotfix. The redirection sector count is what a reader adds to this
    // header's own position to reach the volume area.
    var hotfix = image.AsSpan((int)hotfixOffset);
    "HOTFIX00"u8.CopyTo(hotfix);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[8..], HotfixId);
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[20..],
      (uint)((long)totalBlocks * this.BlockSize / NwfsLayout.SectorSize));
    BinaryPrimitives.WriteUInt32LittleEndian(hotfix[24..], this.RedirectionSectors);

    // Mirror. The flags word reads 0x90000 on a partition that is not mirrored,
    // and both hotfix slots name this partition's own hotfix area.
    var mirror = image.AsSpan((int)(hotfixOffset + NwfsLayout.SectorSize));
    "MIRROR00"u8.CopyTo(mirror);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[8..], stamp);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[12..], 0x90000);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[32..], HotfixId);
    BinaryPrimitives.WriteUInt32LittleEndian(mirror[36..], HotfixId);

    // Volume area: the header, then one entry for the single volume.
    var volumes = image.AsSpan((int)volumeAreaOffset);
    "NetWare Volumes\0"u8.CopyTo(volumes);
    BinaryPrimitives.WriteUInt32LittleEndian(volumes[16..], 1);

    var entry = volumes[32..];
    var nameBytes = Encoding.ASCII.GetBytes(volumeName);
    entry[0] = (byte)nameBytes.Length;
    nameBytes.CopyTo(entry[1..]);
    BinaryPrimitives.WriteUInt16LittleEndian(entry[22..], 0);                         // first segment
    BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], NwfsLayout.FirstSectorOfFirstSegment);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..],
      (uint)((long)totalBlocks * this.BlockSize / NwfsLayout.SectorSize));            // sectors in the segment
    BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], (uint)totalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], 0);                         // blocks count from here
    BinaryPrimitives.WriteUInt32LittleEndian(entry[44..], NwfsLayout.BlockValue(this.BlockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[48..], firstDirectoryBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[52..], firstDirectoryCopyBlock);

    // The FAT. Every entry starts free; chains are laid over it as blocks are used.
    var fat = image.AsSpan((int)dataAreaOffset, totalBlocks * NwfsLayout.FatEntryBytes);
    for (var i = 0; i < totalBlocks; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * NwfsLayout.FatEntryBytes)..], NwfsLayout.NoBlock);
      BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * NwfsLayout.FatEntryBytes + 4)..], NwfsLayout.NoBlock);
    }

    Chain(image, dataAreaOffset, firstDirectoryBlock, directoryBlocks);
    Chain(image, dataAreaOffset, firstDirectoryCopyBlock, directoryBlocks);

    // The directory, built whole and then laid into its blocks.
    var slots = new byte[directoryBlocks * entriesPerBlock][];
    for (var i = 0; i < slots.Length; ++i) {
      var available = new byte[NwfsLayout.DirectoryEntryBytes];
      BinaryPrimitives.WriteUInt32LittleEndian(available, NwfsLayout.DirIdAvailable);
      slots[i] = available;
    }

    var next = 0;
    slots[next++] = VolumeInformationEntry(stamp);
    foreach (var directory in directories)
      slots[next++] = DirectoryEntry(directory, stamp);

    var block = firstFileBlock;
    foreach (var (parent, name, data) in placed) {
      var blocks = (data.Length + this.BlockSize - 1) / this.BlockSize;
      var first = blocks == 0 ? NwfsLayout.NoBlock : block;
      if (blocks > 0) {
        Chain(image, dataAreaOffset, block, blocks);
        data.CopyTo(image.AsSpan((int)(dataAreaOffset + (long)block * this.BlockSize)));
        block += (uint)blocks;
      }

      slots[next++] = FileEntry(parent.Id, name, data.Length, first, stamp);
    }

    for (var i = 0; i < slots.Length; ++i) {
      var at = (long)(firstDirectoryBlock + i / entriesPerBlock) * this.BlockSize
               + i % entriesPerBlock * NwfsLayout.DirectoryEntryBytes;
      slots[i].CopyTo(image.AsSpan((int)(dataAreaOffset + at)));
      var copy = at + (long)directoryBlocks * this.BlockSize;
      slots[i].CopyTo(image.AsSpan((int)(dataAreaOffset + copy)));
    }

    return image;
  }

  /// <summary>An id shared by the hotfix header and the mirror entries naming it.</summary>
  private const uint HotfixId = 0x00000001;

  /// <summary>
  /// Lays a run of consecutive blocks into the FAT as one chain: each block
  /// numbered by its place in the run and pointing at the block after it, the
  /// last of them ending the chain.
  /// </summary>
  private static void Chain(byte[] image, long dataAreaOffset, uint first, int count) {
    for (var i = 0; i < count; ++i) {
      var at = (int)(dataAreaOffset + (long)(first + i) * NwfsLayout.FatEntryBytes);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), (uint)i);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 4),
        i == count - 1 ? NwfsLayout.NoBlock : first + (uint)i + 1);
    }
  }

  private static string Normalise(string name) {
    var upper = name.ToUpperInvariant();
    if (upper.Length > NwfsLayout.MaxNameLength)
      throw new InvalidOperationException($"'{name}' is longer than the twelve characters an entry holds");
    return upper;
  }

  private static byte[] VolumeInformationEntry(uint stamp) {
    var e = new byte[NwfsLayout.DirectoryEntryBytes];
    BinaryPrimitives.WriteUInt32LittleEndian(e, NwfsLayout.DirIdVolumeInfo);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(24), stamp);
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(28), NwfsLayout.SupervisorObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(40), stamp);
    return e;
  }

  private static void WriteCommon(Span<byte> e, uint parentId, uint attributes, string name, uint stamp) {
    BinaryPrimitives.WriteUInt32LittleEndian(e, parentId);
    BinaryPrimitives.WriteUInt32LittleEndian(e[4..], attributes);
    var bytes = Encoding.ASCII.GetBytes(name);
    e[11] = (byte)bytes.Length;
    bytes.CopyTo(e[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(e[24..], stamp);
    BinaryPrimitives.WriteUInt32BigEndian(e[28..], NwfsLayout.SupervisorObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(e[40..], stamp);
  }

  private static byte[] DirectoryEntry(Directory directory, uint stamp) {
    var e = new byte[NwfsLayout.DirectoryEntryBytes];
    WriteCommon(e, directory.ParentId, NwfsLayout.AttributeDirectory, directory.Name, stamp);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(100), 0xFFFF);   // every right inherited
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(120), directory.Id);
    return e;
  }

  private static byte[] FileEntry(uint parentId, string name, int length, uint firstBlock, uint stamp) {
    var e = new byte[NwfsLayout.DirectoryEntryBytes];
    WriteCommon(e, parentId, NwfsLayout.AttributeArchive, name, stamp);
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(44), NwfsLayout.SupervisorObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(48), (uint)length);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(52), firstBlock);
    return e;
  }

  /// <summary>
  /// A partition table naming one NetWare partition, which is how a reader
  /// finds where the hotfix header is to be looked for.
  /// </summary>
  private static void WritePartitionTable(Span<byte> image, uint startSector, uint sectors) {
    const int tableOffset = 446;
    const byte netWare386 = 0x65;

    var e = image[tableOffset..];
    e[0] = 0x00;                    // not bootable
    WriteChs(e[1..], startSector);
    e[4] = netWare386;
    WriteChs(e[5..], startSector + sectors - 1);
    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], startSector);
    BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sectors);

    image[510] = 0x55;
    image[511] = 0xAA;
  }

  /// <summary>
  /// The cylinder-head-sector form of a sector number, capped where the three
  /// bytes stop counting — which is what a table for any sizeable disk holds.
  /// </summary>
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

  /// <summary>
  /// The packed date and time NetWare shares with DOS: the date in the high
  /// half, the time in the low, and seconds counted in twos.
  /// </summary>
  private static uint DosTimestamp(DateTime when) {
    if (when.Year < 1980) when = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var date = (uint)(when.Year - 1980 << 9 | when.Month << 5 | when.Day);
    var time = (uint)(when.Hour << 11 | when.Minute << 5 | when.Second / 2);
    return date << 16 | time;
  }
}
