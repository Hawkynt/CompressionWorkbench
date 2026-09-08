#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.MinixFs;

internal enum MinixMountedVersion {
  V1,
  V2,
  V3,
}

internal sealed record MinixMountedGeometry(
  MinixMountedVersion Version,
  ushort Magic,
  int BlockSize,
  int InodeSize,
  int PointerSize,
  int PointerSlots,
  int NameLength,
  int DirectoryInodeSize,
  uint InodeCount,
  uint ZoneCount,
  ushort InodeBitmapBlocks,
  ushort ZoneBitmapBlocks,
  uint FirstDataZone,
  ushort LogZoneSize,
  uint MaxFileSize,
  ushort State) {

  public const ushort MagicV1_14 = 0x137F;
  public const ushort MagicV1_30 = 0x138F;
  public const ushort MagicV2_14 = 0x2468;
  public const ushort MagicV2_30 = 0x2478;
  public const ushort MagicV3 = 0x4D5A;
  public const ushort StateValid = 0x0001;
  public const ushort StateError = 0x0002;

  public const int SuperblockOffset = 1024;
  public const int DirectZoneCount = 7;

  public int DirectoryEntrySize => DirectoryInodeSize + NameLength;
  public int PointersPerBlock => BlockSize / PointerSize;
  public int MaxIndirectDepth => PointerSlots - DirectZoneCount;
  public long InodeBitmapOffset => 2L * BlockSize;
  public long ZoneBitmapOffset => InodeBitmapOffset + (long)InodeBitmapBlocks * BlockSize;
  public long InodeTableOffset => ZoneBitmapOffset + (long)ZoneBitmapBlocks * BlockSize;
  public int MaxLinks => Version == MinixMountedVersion.V1 ? 250 : 65530;
  public bool HasSeparateTimes => Version != MinixMountedVersion.V1;
  public string VersionName => Version switch {
    MinixMountedVersion.V1 => Magic == MagicV1_30 ? "MINIX v1 (30-char names)" : "MINIX v1 (14-char names)",
    MinixMountedVersion.V2 => Magic == MagicV2_30 ? "MINIX v2 (30-char names)" : "MINIX v2 (14-char names)",
    _ => "MINIX v3",
  };

  public static MinixMountedGeometry Parse(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("MINIX mounted access requires a readable, seekable image.", nameof(image));
    if (image.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MINIX image is too small for its superblock.");

    Span<byte> sb = stackalloc byte[32];
    var original = image.Position;
    try {
      image.Position = SuperblockOffset;
      image.ReadExactly(sb);
    } finally {
      image.Position = original;
    }

    var magic16 = BinaryPrimitives.ReadUInt16LittleEndian(sb[16..18]);
    var magic24 = BinaryPrimitives.ReadUInt16LittleEndian(sb[24..26]);

    MinixMountedGeometry geometry;
    if (magic24 == MagicV3) {
      var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sb[28..30]);
      if (blockSize == 0) blockSize = 1024;
      geometry = new MinixMountedGeometry(
        MinixMountedVersion.V3,
        MagicV3,
        blockSize,
        InodeSize: 64,
        PointerSize: 4,
        PointerSlots: 10,
        NameLength: 60,
        DirectoryInodeSize: 4,
        InodeCount: BinaryPrimitives.ReadUInt32LittleEndian(sb[0..4]),
        ZoneCount: BinaryPrimitives.ReadUInt32LittleEndian(sb[20..24]),
        InodeBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[6..8]),
        ZoneBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[8..10]),
        FirstDataZone: BinaryPrimitives.ReadUInt16LittleEndian(sb[10..12]),
        LogZoneSize: BinaryPrimitives.ReadUInt16LittleEndian(sb[12..14]),
        MaxFileSize: BinaryPrimitives.ReadUInt32LittleEndian(sb[16..20]),
        State: StateValid);
    } else if (magic16 is MagicV1_14 or MagicV1_30) {
      geometry = new MinixMountedGeometry(
        MinixMountedVersion.V1,
        magic16,
        BlockSize: 1024,
        InodeSize: 32,
        PointerSize: 2,
        PointerSlots: 9,
        NameLength: magic16 == MagicV1_30 ? 30 : 14,
        DirectoryInodeSize: 2,
        InodeCount: BinaryPrimitives.ReadUInt16LittleEndian(sb[0..2]),
        ZoneCount: BinaryPrimitives.ReadUInt16LittleEndian(sb[2..4]),
        InodeBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[4..6]),
        ZoneBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[6..8]),
        FirstDataZone: BinaryPrimitives.ReadUInt16LittleEndian(sb[8..10]),
        LogZoneSize: BinaryPrimitives.ReadUInt16LittleEndian(sb[10..12]),
        MaxFileSize: BinaryPrimitives.ReadUInt32LittleEndian(sb[12..16]),
        State: BinaryPrimitives.ReadUInt16LittleEndian(sb[18..20]));
    } else if (magic16 is MagicV2_14 or MagicV2_30) {
      var zones32 = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..24]);
      geometry = new MinixMountedGeometry(
        MinixMountedVersion.V2,
        magic16,
        BlockSize: 1024,
        InodeSize: 64,
        PointerSize: 4,
        PointerSlots: 10,
        NameLength: magic16 == MagicV2_30 ? 30 : 14,
        DirectoryInodeSize: 2,
        InodeCount: BinaryPrimitives.ReadUInt16LittleEndian(sb[0..2]),
        ZoneCount: zones32 != 0 ? zones32 : BinaryPrimitives.ReadUInt16LittleEndian(sb[2..4]),
        InodeBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[4..6]),
        ZoneBitmapBlocks: BinaryPrimitives.ReadUInt16LittleEndian(sb[6..8]),
        FirstDataZone: BinaryPrimitives.ReadUInt16LittleEndian(sb[8..10]),
        LogZoneSize: BinaryPrimitives.ReadUInt16LittleEndian(sb[10..12]),
        MaxFileSize: BinaryPrimitives.ReadUInt32LittleEndian(sb[12..16]),
        State: BinaryPrimitives.ReadUInt16LittleEndian(sb[18..20]));
    } else {
      throw new InvalidDataException(
        $"MINIX superblock magic is unsupported: 0x{magic16:X4} at +16, 0x{magic24:X4} at +24.");
    }

    geometry.Validate(image);
    return geometry;
  }

  private void Validate(Stream image) {
    if (BlockSize != 1024)
      throw new NotSupportedException(
        $"Mounted MINIX currently supports the canonical 1024-byte block layout; this {VersionName} image declares {BlockSize} bytes.");
    if (LogZoneSize != 0)
      throw new NotSupportedException(
        $"Mounted MINIX currently requires one block per zone; s_log_zone_size={LogZoneSize} needs sub-zone block mapping.");
    if (InodeCount == 0 || ZoneCount == 0 || InodeBitmapBlocks == 0 || ZoneBitmapBlocks == 0)
      throw new InvalidDataException("MINIX superblock contains zero geometry fields.");
    if (FirstDataZone < 2 || FirstDataZone >= ZoneCount)
      throw new InvalidDataException($"MINIX first data zone {FirstDataZone} is outside the volume.");
    if (PointerSize is not 2 and not 4 || BlockSize % PointerSize != 0)
      throw new InvalidDataException("MINIX zone-pointer geometry is invalid.");
    if (DirectoryEntrySize <= DirectoryInodeSize || BlockSize % DirectoryEntrySize != 0)
      throw new InvalidDataException("MINIX directory-entry geometry is invalid.");

    var inodeTableEnd = checked(InodeTableOffset + (long)InodeCount * InodeSize);
    var dataStart = checked((long)FirstDataZone * BlockSize);
    if (inodeTableEnd > dataStart)
      throw new InvalidDataException(
        $"MINIX inode table ends at {inodeTableEnd:N0}, beyond first data zone at {dataStart:N0}.");

    var declaredLength = checked((long)ZoneCount * BlockSize);
    if (image.Length < declaredLength)
      throw new InvalidDataException(
        $"MINIX volume declares {declaredLength:N0} bytes but the image contains {image.Length:N0}.");

    var inodeBits = checked((long)InodeBitmapBlocks * BlockSize * 8);
    if (inodeBits <= InodeCount)
      throw new InvalidDataException("MINIX inode bitmap is too small for the advertised inode count plus reserved bit 0.");
    var zoneBits = checked((long)ZoneBitmapBlocks * BlockSize * 8);
    var neededZoneBits = checked((long)ZoneCount - FirstDataZone + 1);
    if (zoneBits < neededZoneBits)
      throw new InvalidDataException("MINIX zone bitmap is too small for the advertised data zones.");
  }
}

internal sealed class MinixMountedInode {
  private const ushort TypeMask = 0xF000;
  private const ushort ModeDirectory = 0x4000;
  private const ushort ModeRegular = 0x8000;
  private const ushort ModeSymlink = 0xA000;
  private const ushort ModeCharacterDevice = 0x2000;
  private const ushort ModeBlockDevice = 0x6000;
  private const ushort ModeFifo = 0x1000;
  private const ushort ModeSocket = 0xC000;

  public MinixMountedInode(uint number, MinixMountedGeometry geometry, byte[] bytes) {
    Number = number;
    Geometry = geometry;
    Bytes = bytes;
  }

  public uint Number { get; }
  public MinixMountedGeometry Geometry { get; }
  public byte[] Bytes { get; }

  public ushort Mode {
    get => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(0, 2));
    set => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(0, 2), value);
  }

  public FilesystemNodeKind Kind => (Mode & TypeMask) switch {
    ModeDirectory => FilesystemNodeKind.Directory,
    ModeRegular => FilesystemNodeKind.RegularFile,
    ModeSymlink => FilesystemNodeKind.SymbolicLink,
    ModeCharacterDevice => FilesystemNodeKind.CharacterDevice,
    ModeBlockDevice => FilesystemNodeKind.BlockDevice,
    ModeFifo => FilesystemNodeKind.Fifo,
    ModeSocket => FilesystemNodeKind.Socket,
    _ => FilesystemNodeKind.Unknown,
  };

  public uint Size {
    get => Geometry.Version == MinixMountedVersion.V1
      ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4, 4))
      : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8, 4));
    set {
      if (Geometry.Version == MinixMountedVersion.V1)
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(4, 4), value);
      else
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(8, 4), value);
    }
  }

  public uint Links {
    get => Geometry.Version == MinixMountedVersion.V1
      ? Bytes[13]
      : BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(2, 2));
    set {
      if (value > Geometry.MaxLinks) throw new IOException($"{Geometry.VersionName} link-count limit {Geometry.MaxLinks} exceeded.");
      if (Geometry.Version == MinixMountedVersion.V1)
        Bytes[13] = checked((byte)value);
      else
        BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(2, 2), checked((ushort)value));
    }
  }

  public uint AccessTime {
    get => Geometry.Version == MinixMountedVersion.V1
      ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8, 4))
      : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(12, 4));
    set {
      if (Geometry.Version == MinixMountedVersion.V1)
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(8, 4), value);
      else
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(12, 4), value);
    }
  }

  public uint ModifyTime {
    get => Geometry.Version == MinixMountedVersion.V1
      ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(8, 4))
      : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(16, 4));
    set {
      if (Geometry.Version == MinixMountedVersion.V1)
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(8, 4), value);
      else
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(16, 4), value);
    }
  }

  public uint ChangeTime {
    get => Geometry.Version == MinixMountedVersion.V1 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(20, 4));
    set {
      if (Geometry.Version != MinixMountedVersion.V1)
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(20, 4), value);
    }
  }

  public uint GetZone(int slot) {
    if ((uint)slot >= Geometry.PointerSlots) throw new ArgumentOutOfRangeException(nameof(slot));
    var offset = Geometry.Version == MinixMountedVersion.V1 ? 14 : 24;
    return Geometry.PointerSize == 2
      ? BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset + slot * 2, 2))
      : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset + slot * 4, 4));
  }

  public void SetZone(int slot, uint zone) {
    if ((uint)slot >= Geometry.PointerSlots) throw new ArgumentOutOfRangeException(nameof(slot));
    var offset = Geometry.Version == MinixMountedVersion.V1 ? 14 : 24;
    if (Geometry.PointerSize == 2) {
      if (zone > ushort.MaxValue) throw new IOException("MINIX v1 cannot address a zone above 65535.");
      BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset + slot * 2, 2), (ushort)zone);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset + slot * 4, 4), zone);
    }
  }
}

internal readonly record struct MinixMountedDirectoryEntry(uint Inode, string Name, long Offset);

internal sealed class MinixMountedVolume {
  private const ushort ModeRegular = 0x8000 | 0x01A4;
  private const ushort ModeDirectory = 0x4000 | 0x01ED;
  private const ushort ModeSymlink = 0xA000 | 0x01FF;
  private const uint RootInode = 1;

  private readonly Stream _image;

  public MinixMountedVolume(Stream image, MinixMountedGeometry geometry) {
    _image = image;
    Geometry = geometry;
  }

  public MinixMountedGeometry Geometry { get; }

  public void ValidateNamespace() {
    if (!IsInodeAllocated(RootInode))
      throw new InvalidDataException("MINIX root inode 1 is not allocated in the inode bitmap.");
    var root = ReadInode(RootInode);
    if (root.Kind != FilesystemNodeKind.Directory)
      throw new InvalidDataException("MINIX root inode 1 is not a directory.");

    var visitedDirectories = new HashSet<uint>();
    var reachable = new HashSet<uint>();
    ValidateDirectory(root, RootInode, visitedDirectories, reachable);
  }

  private void ValidateDirectory(
      MinixMountedInode directory,
      uint expectedParent,
      HashSet<uint> visitedDirectories,
      HashSet<uint> reachable) {
    if (!visitedDirectories.Add(directory.Number)) return;
    var entries = ReadDirectoryEntries(directory);
    var dot = entries.FirstOrDefault(e => e.Name == ".");
    var dotDot = entries.FirstOrDefault(e => e.Name == "..");
    if (dot.Inode != directory.Number)
      throw new InvalidDataException($"MINIX directory inode {directory.Number} has an invalid '.' entry.");
    if (dotDot.Inode != expectedParent)
      throw new InvalidDataException($"MINIX directory inode {directory.Number} has an invalid '..' entry.");

    foreach (var entry in entries) {
      if (entry.Name is "." or "..") continue;
      if (entry.Inode == 0 || entry.Inode > Geometry.InodeCount || !IsInodeAllocated(entry.Inode))
        throw new InvalidDataException($"MINIX directory entry '{entry.Name}' references unallocated inode {entry.Inode}.");
      reachable.Add(entry.Inode);
      var child = ReadInode(entry.Inode);
      ValidateInodeZones(child);
      if (child.Kind == FilesystemNodeKind.Directory)
        ValidateDirectory(child, directory.Number, visitedDirectories, reachable);
    }
  }

  private void ValidateInodeZones(MinixMountedInode inode) {
    var visited = new HashSet<uint>();
    for (var i = 0; i < MinixMountedGeometry.DirectZoneCount; ++i) {
      var zone = inode.GetZone(i);
      if (zone != 0) ValidateAllocatedZone(zone);
    }
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var root = inode.GetZone(MinixMountedGeometry.DirectZoneCount + depth - 1);
      if (root != 0) ValidateIndirectTree(root, depth, visited);
    }
  }

  private void ValidateIndirectTree(uint zone, int depth, HashSet<uint> visited) {
    ValidateAllocatedZone(zone);
    if (!visited.Add(zone)) throw new InvalidDataException($"MINIX indirect zone tree contains a cycle at zone {zone}.");
    var block = ReadZone(zone);
    for (var i = 0; i < Geometry.PointersPerBlock; ++i) {
      var child = ReadPointer(block, i);
      if (child == 0) continue;
      if (depth == 1) ValidateAllocatedZone(child);
      else ValidateIndirectTree(child, depth - 1, visited);
    }
  }

  private void ValidateAllocatedZone(uint zone) {
    if (zone < Geometry.FirstDataZone || zone >= Geometry.ZoneCount)
      throw new InvalidDataException($"MINIX inode points outside the data-zone range: {zone}.");
    if (!IsZoneAllocated(zone))
      throw new InvalidDataException($"MINIX inode references zone {zone}, but its zone-bitmap bit is clear.");
  }

  public MinixMountedInode ReadInode(uint number) {
    if (number == 0 || number > Geometry.InodeCount)
      throw new FileNotFoundException($"MINIX inode {number} is outside 1..{Geometry.InodeCount}.");
    var bytes = new byte[Geometry.InodeSize];
    ReadExactly(InodeOffset(number), bytes);
    return new MinixMountedInode(number, Geometry, bytes);
  }

  public void WriteInode(MinixMountedInode inode)
    => WriteExactly(InodeOffset(inode.Number), inode.Bytes);

  private long InodeOffset(uint number)
    => checked(Geometry.InodeTableOffset + (long)(number - 1) * Geometry.InodeSize);

  public bool IsInodeAllocated(uint number) {
    if (number == 0 || number > Geometry.InodeCount) return false;
    return ReadBitmapBit(Geometry.InodeBitmapOffset, number);
  }

  public bool IsZoneAllocated(uint zone) {
    if (zone < Geometry.FirstDataZone || zone >= Geometry.ZoneCount) return false;
    return ReadBitmapBit(Geometry.ZoneBitmapOffset, ZoneBit(zone));
  }

  public MinixMountedInode AllocateInode(FilesystemNodeKind kind) {
    for (uint number = 1; number <= Geometry.InodeCount; ++number) {
      if (ReadBitmapBit(Geometry.InodeBitmapOffset, number)) continue;
      WriteBitmapBit(Geometry.InodeBitmapOffset, number, true);
      var bytes = new byte[Geometry.InodeSize];
      var inode = new MinixMountedInode(number, Geometry, bytes) {
        Mode = kind switch {
          FilesystemNodeKind.RegularFile => ModeRegular,
          FilesystemNodeKind.Directory => ModeDirectory,
          FilesystemNodeKind.SymbolicLink => ModeSymlink,
          _ => throw new NotSupportedException($"MINIX mounted creation does not support {kind} inodes."),
        },
        Links = kind == FilesystemNodeKind.Directory ? 2u : 1u,
      };
      var now = NowSeconds();
      inode.AccessTime = now;
      inode.ModifyTime = now;
      inode.ChangeTime = now;
      WriteInode(inode);
      return inode;
    }
    throw new IOException("MINIX inode bitmap has no free inode.");
  }

  public void FreeInode(MinixMountedInode inode) {
    if (inode.Number == RootInode) throw new IOException("MINIX root inode cannot be freed.");
    FreeAllZones(inode);
    Array.Clear(inode.Bytes);
    WriteInode(inode);
    WriteBitmapBit(Geometry.InodeBitmapOffset, inode.Number, false);
  }

  public uint AllocateZone() {
    var lastBit = checked(Geometry.ZoneCount - Geometry.FirstDataZone);
    for (uint bit = 1; bit <= lastBit; ++bit) {
      if (ReadBitmapBit(Geometry.ZoneBitmapOffset, bit)) continue;
      var zone = checked(Geometry.FirstDataZone + bit - 1);
      WriteBitmapBit(Geometry.ZoneBitmapOffset, bit, true);
      WriteZone(zone, new byte[Geometry.BlockSize]);
      return zone;
    }
    throw new IOException("MINIX zone bitmap has no free data zone.");
  }

  public void FreeZone(uint zone) {
    ValidateAllocatedZone(zone);
    WriteBitmapBit(Geometry.ZoneBitmapOffset, ZoneBit(zone), false);
  }

  private uint ZoneBit(uint zone) => checked(zone - Geometry.FirstDataZone + 1);

  private bool ReadBitmapBit(long baseOffset, uint bit) {
    Span<byte> b = stackalloc byte[1];
    ReadExactly(checked(baseOffset + bit / 8), b);
    return (b[0] & (1 << (int)(bit & 7))) != 0;
  }

  private void WriteBitmapBit(long baseOffset, uint bit, bool value) {
    Span<byte> b = stackalloc byte[1];
    var offset = checked(baseOffset + bit / 8);
    ReadExactly(offset, b);
    var mask = (byte)(1 << (int)(bit & 7));
    b[0] = value ? (byte)(b[0] | mask) : (byte)(b[0] & ~mask);
    WriteExactly(offset, b);
  }

  public uint GetLogicalZone(MinixMountedInode inode, ulong logicalZone) {
    if (logicalZone < MinixMountedGeometry.DirectZoneCount)
      return inode.GetZone((int)logicalZone);

    logicalZone -= MinixMountedGeometry.DirectZoneCount;
    var perBlock = (ulong)Geometry.PointersPerBlock;
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var capacity = Pow(perBlock, depth);
      if (logicalZone < capacity) {
        var root = inode.GetZone(MinixMountedGeometry.DirectZoneCount + depth - 1);
        return ReadIndirectPointer(root, depth, logicalZone);
      }
      logicalZone -= capacity;
    }
    throw new IOException($"{Geometry.VersionName} file exceeds its indirect-zone address space.");
  }

  private uint ReadIndirectPointer(uint root, int depth, ulong index) {
    if (root == 0) return 0;
    var current = root;
    var perBlock = (ulong)Geometry.PointersPerBlock;
    for (var level = depth; level > 0; --level) {
      var divisor = Pow(perBlock, level - 1);
      var slot = checked((int)(index / divisor));
      index %= divisor;
      var block = ReadZone(current);
      current = ReadPointer(block, slot);
      if (current == 0) return 0;
    }
    return current;
  }

  public uint EnsureLogicalZone(MinixMountedInode inode, ulong logicalZone) {
    if (logicalZone < MinixMountedGeometry.DirectZoneCount) {
      var slot = (int)logicalZone;
      var zone = inode.GetZone(slot);
      if (zone != 0) return zone;
      zone = AllocateZone();
      inode.SetZone(slot, zone);
      WriteInode(inode);
      return zone;
    }

    logicalZone -= MinixMountedGeometry.DirectZoneCount;
    var perBlock = (ulong)Geometry.PointersPerBlock;
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var capacity = Pow(perBlock, depth);
      if (logicalZone >= capacity) {
        logicalZone -= capacity;
        continue;
      }

      var rootSlot = MinixMountedGeometry.DirectZoneCount + depth - 1;
      var current = inode.GetZone(rootSlot);
      if (current == 0) {
        current = AllocateZone();
        inode.SetZone(rootSlot, current);
        WriteInode(inode);
      }

      for (var level = depth; level > 0; --level) {
        var divisor = Pow(perBlock, level - 1);
        var slot = checked((int)(logicalZone / divisor));
        logicalZone %= divisor;
        var block = ReadZone(current);
        var next = ReadPointer(block, slot);
        if (next == 0) {
          next = AllocateZone();
          WritePointer(block, slot, next);
          WriteZone(current, block);
        }
        current = next;
      }
      return current;
    }

    throw new IOException($"{Geometry.VersionName} file exceeds its indirect-zone address space.");
  }

  public int ReadFileBytes(MinixMountedInode inode, long offset, Span<byte> destination) {
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (offset >= inode.Size || destination.IsEmpty) return 0;
    var remaining = (int)Math.Min(destination.Length, (long)inode.Size - offset);
    var copied = 0;
    while (copied < remaining) {
      var absolute = offset + copied;
      var logical = checked((ulong)(absolute / Geometry.BlockSize));
      var inZone = (int)(absolute % Geometry.BlockSize);
      var count = Math.Min(remaining - copied, Geometry.BlockSize - inZone);
      var zone = GetLogicalZone(inode, logical);
      if (zone == 0)
        destination.Slice(copied, count).Clear();
      else
        ReadZone(zone).AsSpan(inZone, count).CopyTo(destination.Slice(copied, count));
      copied += count;
    }
    return copied;
  }

  public void WriteFileBytes(MinixMountedInode inode, long offset, ReadOnlySpan<byte> source) {
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (source.IsEmpty) return;
    var end = checked(offset + source.Length);
    EnsureLengthRepresentable(end);

    var copied = 0;
    while (copied < source.Length) {
      var absolute = offset + copied;
      var logical = checked((ulong)(absolute / Geometry.BlockSize));
      var inZone = (int)(absolute % Geometry.BlockSize);
      var count = Math.Min(source.Length - copied, Geometry.BlockSize - inZone);
      var zone = EnsureLogicalZone(inode, logical);
      if (inZone == 0 && count == Geometry.BlockSize) {
        WriteZone(zone, source.Slice(copied, count));
      } else {
        var block = ReadZone(zone);
        source.Slice(copied, count).CopyTo(block.AsSpan(inZone, count));
        WriteZone(zone, block);
      }
      copied += count;
    }

    if (end > inode.Size) inode.Size = checked((uint)end);
    Touch(inode, modify: true, change: true);
    WriteInode(inode);
  }

  public void SetFileLength(MinixMountedInode inode, long length) {
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    EnsureLengthRepresentable(length);
    if (length == inode.Size) return;

    if (length < inode.Size) {
      var keepZones = length == 0
        ? 0UL
        : checked((ulong)((length + Geometry.BlockSize - 1) / Geometry.BlockSize));
      if (length > 0 && length % Geometry.BlockSize != 0) {
        var logical = checked((ulong)(length / Geometry.BlockSize));
        var zone = GetLogicalZone(inode, logical);
        if (zone != 0) {
          var block = ReadZone(zone);
          block.AsSpan((int)(length % Geometry.BlockSize)).Clear();
          WriteZone(zone, block);
        }
      }
      TrimToLogicalZoneCount(inode, keepZones);
    }

    inode.Size = checked((uint)length);
    Touch(inode, modify: true, change: true);
    WriteInode(inode);
  }

  private void EnsureLengthRepresentable(long length) {
    if (length > uint.MaxValue)
      throw new IOException("MINIX inode size is limited to 32 bits.");
    if (Geometry.MaxFileSize != 0 && length > Geometry.MaxFileSize)
      throw new IOException($"{Geometry.VersionName} superblock limits files to {Geometry.MaxFileSize:N0} bytes.");
    var maxZones = (ulong)MinixMountedGeometry.DirectZoneCount;
    var perBlock = (ulong)Geometry.PointersPerBlock;
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth)
      maxZones = checked(maxZones + Pow(perBlock, depth));
    var capacity = checked(maxZones * (ulong)Geometry.BlockSize);
    if ((ulong)length > capacity)
      throw new IOException($"{Geometry.VersionName} block map can address at most {capacity:N0} bytes.");
  }

  public void TrimToLogicalZoneCount(MinixMountedInode inode, ulong keepZones) {
    for (var slot = 0; slot < MinixMountedGeometry.DirectZoneCount; ++slot) {
      if ((ulong)slot < keepZones) continue;
      var zone = inode.GetZone(slot);
      if (zone == 0) continue;
      FreeZone(zone);
      inode.SetZone(slot, 0);
    }

    var baseLogical = (ulong)MinixMountedGeometry.DirectZoneCount;
    var perBlock = (ulong)Geometry.PointersPerBlock;
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var slot = MinixMountedGeometry.DirectZoneCount + depth - 1;
      var root = inode.GetZone(slot);
      var capacity = Pow(perBlock, depth);
      if (root != 0 && TrimIndirect(root, depth, baseLogical, keepZones)) {
        FreeZone(root);
        inode.SetZone(slot, 0);
      }
      baseLogical = checked(baseLogical + capacity);
    }
    WriteInode(inode);
  }

  private bool TrimIndirect(uint zone, int depth, ulong baseLogical, ulong keepZones) {
    var block = ReadZone(zone);
    var childCapacity = Pow((ulong)Geometry.PointersPerBlock, depth - 1);
    var any = false;
    for (var slot = 0; slot < Geometry.PointersPerBlock; ++slot) {
      var child = ReadPointer(block, slot);
      if (child == 0) continue;
      var childBase = checked(baseLogical + (ulong)slot * childCapacity);
      if (childBase >= keepZones) {
        if (depth == 1) FreeZone(child);
        else FreeIndirectTree(child, depth - 1);
        WritePointer(block, slot, 0);
        continue;
      }
      if (depth > 1 && childBase + childCapacity > keepZones && TrimIndirect(child, depth - 1, childBase, keepZones)) {
        FreeZone(child);
        WritePointer(block, slot, 0);
        continue;
      }
      any = true;
    }
    WriteZone(zone, block);
    return !any && !ContainsPointer(block);
  }

  public void FreeAllZones(MinixMountedInode inode) {
    for (var slot = 0; slot < MinixMountedGeometry.DirectZoneCount; ++slot) {
      var zone = inode.GetZone(slot);
      if (zone == 0) continue;
      FreeZone(zone);
      inode.SetZone(slot, 0);
    }
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var slot = MinixMountedGeometry.DirectZoneCount + depth - 1;
      var root = inode.GetZone(slot);
      if (root == 0) continue;
      FreeIndirectTree(root, depth);
      inode.SetZone(slot, 0);
    }
    inode.Size = 0;
    WriteInode(inode);
  }

  private void FreeIndirectTree(uint zone, int depth) {
    var block = ReadZone(zone);
    for (var slot = 0; slot < Geometry.PointersPerBlock; ++slot) {
      var child = ReadPointer(block, slot);
      if (child == 0) continue;
      if (depth == 1) FreeZone(child);
      else FreeIndirectTree(child, depth - 1);
    }
    FreeZone(zone);
  }

  public long AllocatedBytes(MinixMountedInode inode) {
    var zones = 0L;
    for (var slot = 0; slot < MinixMountedGeometry.DirectZoneCount; ++slot)
      if (inode.GetZone(slot) != 0) ++zones;
    for (var depth = 1; depth <= Geometry.MaxIndirectDepth; ++depth) {
      var root = inode.GetZone(MinixMountedGeometry.DirectZoneCount + depth - 1);
      if (root != 0) zones += CountIndirectZones(root, depth, []);
    }
    return checked(zones * Geometry.BlockSize);
  }

  private long CountIndirectZones(uint zone, int depth, HashSet<uint> visited) {
    if (!visited.Add(zone)) throw new InvalidDataException($"MINIX indirect-zone cycle at {zone}.");
    var count = 1L;
    var block = ReadZone(zone);
    for (var slot = 0; slot < Geometry.PointersPerBlock; ++slot) {
      var child = ReadPointer(block, slot);
      if (child == 0) continue;
      count = checked(count + (depth == 1 ? 1 : CountIndirectZones(child, depth - 1, visited)));
    }
    return count;
  }

  public IReadOnlyList<MinixMountedDirectoryEntry> ReadDirectoryEntries(MinixMountedInode directory) {
    if (directory.Kind != FilesystemNodeKind.Directory)
      throw new DirectoryNotFoundException($"MINIX inode {directory.Number} is not a directory.");
    if (directory.Size > int.MaxValue)
      throw new NotSupportedException("MINIX directory is too large for mounted enumeration.");

    var bytes = new byte[(int)directory.Size];
    _ = ReadFileBytes(directory, 0, bytes);
    var entries = new List<MinixMountedDirectoryEntry>();
    for (var offset = 0; offset + Geometry.DirectoryEntrySize <= bytes.Length; offset += Geometry.DirectoryEntrySize) {
      var inode = Geometry.DirectoryInodeSize == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
        : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
      if (inode == 0) continue;
      var nameStart = offset + Geometry.DirectoryInodeSize;
      var nameBytes = bytes.AsSpan(nameStart, Geometry.NameLength);
      var nul = nameBytes.IndexOf((byte)0);
      if (nul >= 0) nameBytes = nameBytes[..nul];
      var name = Encoding.Latin1.GetString(nameBytes);
      entries.Add(new MinixMountedDirectoryEntry(inode, name, offset));
    }
    return entries;
  }

  public MinixMountedDirectoryEntry? FindDirectoryEntry(MinixMountedInode directory, string name)
    => ReadDirectoryEntries(directory).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal)) is { Inode: not 0 } entry
      ? entry
      : null;

  public void InsertDirectoryEntry(MinixMountedInode directory, string name, uint inodeNumber) {
    ValidateName(name);
    var existing = FindDirectoryEntry(directory, name);
    if (existing is not null) throw new IOException($"MINIX entry '{name}' already exists.");

    var entries = ReadDirectoryEntries(directory);
    var occupiedOffsets = entries.Select(e => e.Offset).ToHashSet();
    for (long offset = 0; offset + Geometry.DirectoryEntrySize <= directory.Size; offset += Geometry.DirectoryEntrySize) {
      if (occupiedOffsets.Contains(offset)) continue;
      WriteDirectoryEntry(directory, offset, inodeNumber, name, grow: false);
      return;
    }
    WriteDirectoryEntry(directory, directory.Size, inodeNumber, name, grow: true);
  }

  public void RemoveDirectoryEntry(MinixMountedInode directory, string name, uint expectedInode) {
    var entry = FindDirectoryEntry(directory, name)
      ?? throw new FileNotFoundException($"MINIX entry '{name}' does not exist.");
    if (entry.Inode != expectedInode)
      throw new InvalidDataException($"MINIX entry '{name}' changed inode identity during mutation.");
    var zeros = new byte[Geometry.DirectoryEntrySize];
    WriteFileBytes(directory, entry.Offset, zeros);
  }

  public void RenameDirectoryEntry(MinixMountedInode directory, string oldName, string newName, uint expectedInode) {
    ValidateName(newName);
    var source = FindDirectoryEntry(directory, oldName)
      ?? throw new FileNotFoundException($"MINIX entry '{oldName}' does not exist.");
    if (source.Inode != expectedInode)
      throw new InvalidDataException($"MINIX entry '{oldName}' changed inode identity during rename.");
    if (FindDirectoryEntry(directory, newName) is not null)
      throw new IOException($"MINIX entry '{newName}' already exists.");
    WriteDirectoryEntry(directory, source.Offset, expectedInode, newName, grow: false);
  }

  public void RewriteDotDot(MinixMountedInode directory, uint parentInode) {
    var entry = FindDirectoryEntry(directory, "..")
      ?? throw new InvalidDataException($"MINIX directory inode {directory.Number} has no '..' entry.");
    WriteDirectoryEntry(directory, entry.Offset, parentInode, "..", grow: false);
  }

  private void WriteDirectoryEntry(MinixMountedInode directory, long offset, uint inodeNumber, string name, bool grow) {
    var record = new byte[Geometry.DirectoryEntrySize];
    if (Geometry.DirectoryInodeSize == 2) {
      if (inodeNumber > ushort.MaxValue) throw new IOException("MINIX v1/v2 directory entry cannot encode an inode above 65535.");
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), (ushort)inodeNumber);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0, 4), inodeNumber);
    }
    var encoded = EncodeName(name);
    encoded.CopyTo(record.AsSpan(Geometry.DirectoryInodeSize));
    WriteFileBytes(directory, offset, record);
    if (!grow && offset + record.Length <= directory.Size) {
      Touch(directory, modify: true, change: true);
      WriteInode(directory);
    }
  }

  public byte[] EncodeName(string name) {
    ValidateName(name);
    var bytes = Encoding.Latin1.GetBytes(name);
    if (bytes.Length > Geometry.NameLength)
      throw new ArgumentException($"{Geometry.VersionName} names are limited to {Geometry.NameLength} bytes.", nameof(name));
    return bytes;
  }

  public void ValidateName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length == 0) throw new ArgumentException("MINIX name cannot be empty.", nameof(name));
    if (name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
      throw new ArgumentException("MINIX names cannot contain separators or NUL.", nameof(name));
    foreach (var c in name)
      if (c > byte.MaxValue)
        throw new ArgumentException("Mounted MINIX names must be representable as single-byte Latin-1 values.", nameof(name));
    if (Encoding.Latin1.GetByteCount(name) > Geometry.NameLength)
      throw new ArgumentException($"{Geometry.VersionName} names are limited to {Geometry.NameLength} bytes.", nameof(name));
  }

  public void InitializeDirectory(MinixMountedInode directory, uint parentInode) {
    InsertDirectoryEntry(directory, ".", directory.Number);
    InsertDirectoryEntry(directory, "..", parentInode);
    Touch(directory, modify: true, change: true);
    WriteInode(directory);
  }

  public static void Touch(MinixMountedInode inode, bool modify, bool change) {
    var now = NowSeconds();
    if (modify) inode.ModifyTime = now;
    if (change) inode.ChangeTime = now;
  }

  public static uint NowSeconds() {
    var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return seconds <= 0 ? 0u : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }

  public void Flush() => _image.Flush();

  private byte[] ReadZone(uint zone) {
    if (zone < Geometry.FirstDataZone || zone >= Geometry.ZoneCount)
      throw new InvalidDataException($"MINIX zone {zone} is outside the data-zone range.");
    var block = new byte[Geometry.BlockSize];
    ReadExactly(checked((long)zone * Geometry.BlockSize), block);
    return block;
  }

  private void WriteZone(uint zone, ReadOnlySpan<byte> data) {
    if (zone < Geometry.FirstDataZone || zone >= Geometry.ZoneCount)
      throw new InvalidDataException($"MINIX zone {zone} is outside the data-zone range.");
    if (data.Length != Geometry.BlockSize)
      throw new ArgumentException($"MINIX zone writes must be exactly {Geometry.BlockSize} bytes.", nameof(data));
    WriteExactly(checked((long)zone * Geometry.BlockSize), data);
  }

  private uint ReadPointer(ReadOnlySpan<byte> block, int slot) {
    var offset = checked(slot * Geometry.PointerSize);
    return Geometry.PointerSize == 2
      ? BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(offset, 2))
      : BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset, 4));
  }

  private void WritePointer(Span<byte> block, int slot, uint value) {
    var offset = checked(slot * Geometry.PointerSize);
    if (Geometry.PointerSize == 2) {
      if (value > ushort.MaxValue) throw new IOException("MINIX v1 indirect pointer cannot encode a zone above 65535.");
      BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(offset, 2), (ushort)value);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(offset, 4), value);
    }
  }

  private static bool ContainsPointer(byte[] block) {
    for (var i = 0; i < block.Length; ++i)
      if (block[i] != 0) return true;
    return false;
  }

  private static ulong Pow(ulong value, int exponent) {
    var result = 1UL;
    for (var i = 0; i < exponent; ++i) result = checked(result * value);
    return result;
  }

  private void ReadExactly(long offset, Span<byte> destination) {
    _image.Position = offset;
    _image.ReadExactly(destination);
  }

  private void WriteExactly(long offset, ReadOnlySpan<byte> source) {
    _image.Position = offset;
    _image.Write(source);
  }
}