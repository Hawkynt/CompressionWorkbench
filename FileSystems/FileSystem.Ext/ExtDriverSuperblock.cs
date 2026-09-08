#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ext;

internal sealed record ExtDriverInode(
  uint Number,
  ushort Mode,
  FilesystemNodeKind Kind,
  long Size,
  long AllocatedBytes,
  uint LinkCount,
  uint Generation,
  uint Flags,
  DateTimeOffset? Modified,
  uint FileAclBlock);

internal readonly record struct ExtDriverSuperblock(
  uint InodesCount,
  ulong BlocksCount,
  int BlockSize,
  uint BlocksPerGroup,
  uint InodesPerGroup,
  ushort InodeSize,
  uint FirstDataBlock,
  uint FeatureCompat,
  uint FeatureIncompat,
  uint FeatureRoCompat,
  ushort State,
  ushort DescriptorSize) {

  public const uint CompatHasJournal = 0x0004;
  private const uint IncompatRecover = 0x0004;
  private const uint IncompatJournalDevice = 0x0008;
  private const uint IncompatMetaBg = 0x0010;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatDirData = 0x1000;
  private const uint IncompatInlineData = 0x8000;
  private const uint IncompatEncrypt = 0x10000;
  private const uint IncompatCasefold = 0x20000;
  private const uint IncompatVerity = 0x100000;
  private const uint RoCompatBigalloc = 0x0200;

  public string ProfileName {
    get {
      var ext4 = (FeatureIncompat & (0x0040u | Incompat64Bit)) != 0 || (FeatureRoCompat & 0x0400u) != 0;
      if (ext4) return "ext4 native inode reader";
      return (FeatureCompat & CompatHasJournal) != 0
        ? "ext3 native inode reader"
        : "ext2 native inode reader";
    }
  }

  public static ExtDriverSuperblock Parse(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext driver probing requires a readable, seekable image.", nameof(image));
    if (image.Length < 2048) throw new InvalidDataException("ext image is too small for its superblock.");
    var sb = new byte[1024];
    var original = image.Position;
    try {
      image.Position = 1024;
      image.ReadExactly(sb);
    } finally {
      image.Position = original;
    }
    if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56, 2)) != 0xEF53)
      throw new InvalidDataException("ext superblock magic is not 0xEF53.");

    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24, 4));
    if (logBlock > 6) throw new NotSupportedException($"ext block-size shift {logBlock} is unsupported.");
    var blockSize = checked(1024 << (int)logBlock);
    var blocksLo = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4, 4));
    var incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96, 4));
    var blocksHi = (incompat & Incompat64Bit) != 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150, 4))
      : 0u;
    var blocks = ((ulong)blocksHi << 32) | blocksLo;
    var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88, 2));
    if (inodeSize == 0) inodeSize = 128;
    var descSize = (incompat & Incompat64Bit) != 0
      ? Math.Max((ushort)64, BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xFE, 2)))
      : (ushort)32;

    return new ExtDriverSuperblock(
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0, 4)),
      blocks,
      blockSize,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40, 4)),
      inodeSize,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(92, 4)),
      incompat,
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100, 4)),
      BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(58, 2)),
      descSize);
  }

  public void ValidateReadableProfile(Stream image) {
    if (InodesCount == 0 || BlocksCount == 0 || BlocksPerGroup == 0 || InodesPerGroup == 0)
      throw new InvalidDataException("ext superblock has zero geometry fields.");
    var declared = checked((long)Math.Min(BlocksCount, (ulong)long.MaxValue) * BlockSize);
    if ((ulong)declared / (ulong)BlockSize != BlocksCount)
      throw new NotSupportedException("ext declared volume size exceeds the addressable stream range.");
    if (image.Length < declared)
      throw new InvalidDataException($"ext volume declares {declared:N0} bytes but image has {image.Length:N0}.");

    var unsupported = FeatureIncompat &
      (IncompatRecover | IncompatJournalDevice | IncompatMetaBg | IncompatDirData |
       IncompatInlineData | IncompatEncrypt | IncompatCasefold | IncompatVerity);
    if (unsupported != 0)
      throw new NotSupportedException(
        $"ext profile has unsupported/unsafe incompat feature bits 0x{unsupported:X8}; mount remains fail-closed.");
    if ((FeatureRoCompat & RoCompatBigalloc) != 0)
      throw new NotSupportedException("ext bigalloc cluster semantics are not decoded by the current mounted reader.");
    if ((State & 0x0001) == 0 && (FeatureCompat & CompatHasJournal) != 0)
      throw new NotSupportedException("ext journaled volume is not marked clean; JBD/JBD2 replay is required before mounting this snapshot.");
  }

  public ExtDriverInode ReadInode(Stream image, uint inodeNumber) {
    if (inodeNumber == 0 || inodeNumber > InodesCount)
      throw new InvalidDataException($"ext inode {inodeNumber} lies outside 1..{InodesCount}.");
    var group = (inodeNumber - 1) / InodesPerGroup;
    var index = (inodeNumber - 1) % InodesPerGroup;
    var groupCount = checked((BlocksCount + BlocksPerGroup - 1) / BlocksPerGroup);
    if (group >= groupCount) throw new InvalidDataException($"ext inode {inodeNumber} has invalid block group {group}.");

    var bgdtBlock = FirstDataBlock + 1UL;
    var descriptorOffset = checked((long)bgdtBlock * BlockSize + (long)group * DescriptorSize);
    Span<byte> descriptor = stackalloc byte[64];
    var original = image.Position;
    try {
      image.Position = descriptorOffset;
      image.ReadExactly(descriptor[..DescriptorSize]);
    } finally {
      image.Position = original;
    }
    var tableLo = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(8, 4));
    var tableHi = DescriptorSize >= 64
      ? BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(0x28, 4))
      : 0u;
    var tableBlock = ((ulong)tableHi << 32) | tableLo;
    var inodeOffset = checked((long)tableBlock * BlockSize + (long)index * InodeSize);
    if (inodeOffset < 0 || inodeOffset > image.Length - InodeSize)
      throw new InvalidDataException($"ext inode {inodeNumber} table slot lies outside the image.");

    var inode = new byte[InodeSize];
    try {
      image.Position = inodeOffset;
      image.ReadExactly(inode);
    } finally {
      image.Position = original;
    }

    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0, 2));
    var kind = (mode & 0xF000) switch {
      0x4000 => FilesystemNodeKind.Directory,
      0x8000 => FilesystemNodeKind.RegularFile,
      0xA000 => FilesystemNodeKind.SymbolicLink,
      0x2000 => FilesystemNodeKind.CharacterDevice,
      0x6000 => FilesystemNodeKind.BlockDevice,
      0x1000 => FilesystemNodeKind.Fifo,
      0xC000 => FilesystemNodeKind.Socket,
      _ => FilesystemNodeKind.Unknown,
    };
    var sizeLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4, 4));
    var sizeHi = kind == FilesystemNodeKind.RegularFile && inode.Length >= 112
      ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(108, 4))
      : 0u;
    var size = checked((long)(((ulong)sizeHi << 32) | sizeLo));
    var blocksLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(28, 4));
    var blocksHi = inode.Length >= 118 ? BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(116, 2)) : (ushort)0;
    var sectors = ((ulong)blocksHi << 32) | blocksLo;
    var allocated = checked((long)checked(sectors * 512UL));
    var links = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(26, 2));
    var generation = inode.Length >= 104 ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)) : 0u;
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32, 4));
    var fileAcl = inode.Length >= 108 ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(104, 4)) : 0u;
    var mtime = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(16, 4));
    DateTimeOffset? modified = null;
    if (mtime != 0) {
      try { modified = DateTimeOffset.FromUnixTimeSeconds(mtime); } catch { }
    }
    return new ExtDriverInode(inodeNumber, mode, kind, size, allocated, links, generation, flags, modified, fileAcl);
  }
}