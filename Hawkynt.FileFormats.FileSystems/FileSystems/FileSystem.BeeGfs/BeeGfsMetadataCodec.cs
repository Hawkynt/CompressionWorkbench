#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.BeeGfs;

internal enum BeeGfsDiskMetadataType : byte {
  FileDentry = 1,
  DirectoryDentry = 2,
  FileInode = 3,
  DirectoryInode = 4,
}

internal sealed record BeeGfsRaid0Pattern(
  uint ChunkSize,
  ushort StoragePoolId,
  uint DefaultTargetCount,
  IReadOnlyList<ushort> TargetIds
);

internal sealed record BeeGfsDecodedDentry(
  BeeGfsDiskMetadataType MetadataType,
  byte StorageFormatVersion,
  ushort DentryFeatureFlags,
  FilesystemNodeKind Kind,
  string EntryId,
  uint OwnerNodeId,
  bool HasInlineInode,
  long Size,
  uint LinkCount,
  uint UserId,
  uint GroupId,
  uint OriginalUserId,
  string OriginalParentEntryId,
  uint Mode,
  uint InodeFeatureFlags,
  byte FileState,
  BeeGfsRaid0Pattern? Raid0Pattern
);

/// <summary>
/// Clean-room decoder for the small BeeGFS disk-metadata subset needed by the
/// offline multi-target reader. Layout facts come from the BeeGFS serialization
/// contracts; no upstream implementation code is copied.
/// </summary>
internal static class BeeGfsMetadataCodec {
  private const int MaxMetadataBytes = 4096;

  private const ushort DentryFeatureInodeInline = 1;
  private const ushort DentryFeatureIsFileInode = 2;
  private const ushort DentryFeatureMirrored = 4;
  private const ushort DentryFeatureBuddyMirrored = 8;
  private const ushort DentryFeature32BitIds = 16;
  private const ushort SupportedDentryFeatures =
    DentryFeatureInodeInline |
    DentryFeatureIsFileInode |
    DentryFeatureMirrored |
    DentryFeatureBuddyMirrored |
    DentryFeature32BitIds;

  private const uint FileInodeFeatureMirrored = 1;
  private const uint FileInodeFeatureBuddyMirrored = 8;
  private const uint FileInodeFeatureHasOriginalParentId = 16;
  private const uint FileInodeFeatureHasOriginalUid = 32;
  private const uint FileInodeFeatureHasStatFlags = 64;
  private const uint FileInodeFeatureHasVersions = 128;
  private const uint FileInodeFeatureHasRemoteStorageTargets = 256;
  private const uint FileInodeFeatureHasStateFlags = 512;
  private const uint SupportedFileInodeFeatures =
    FileInodeFeatureMirrored |
    FileInodeFeatureBuddyMirrored |
    FileInodeFeatureHasOriginalParentId |
    FileInodeFeatureHasOriginalUid |
    FileInodeFeatureHasStatFlags |
    FileInodeFeatureHasVersions |
    FileInodeFeatureHasRemoteStorageTargets |
    FileInodeFeatureHasStateFlags;

  private const uint StatFeatureSparseFile = 1;
  private const uint StripePatternHasNoPoolFlag = 1u << 24;
  private const uint StripePatternRaid0 = 1;
  private const uint MinimumChunkSize = 64 * 1024;

  public static BeeGfsDecodedDentry ParseDentry(ReadOnlySpan<byte> data, string currentParentEntryId) {
    ArgumentNullException.ThrowIfNull(currentParentEntryId);
    if (data.Length is < 8 or > MaxMetadataBytes)
      throw new InvalidDataException($"BeeGFS dentry metadata length {data.Length} is outside 8..{MaxMetadataBytes} bytes.");

    var reader = new Reader(data);
    var metadataType = (BeeGfsDiskMetadataType)reader.ReadByte();
    var version = reader.ReadByte();
    var dentryFeatures = reader.ReadUInt16();
    if ((dentryFeatures & ~SupportedDentryFeatures) != 0)
      throw new NotSupportedException($"BeeGFS dentry uses unknown feature flags 0x{dentryFeatures:X4}.");

    var kind = KindFor(reader.ReadByte());
    // The legacy mirrored-header variant stores a 16-bit mirror-node id in the
    // otherwise-unused three-byte tail. The value is not needed for read-only
    // decoding, but the fixed 8-byte header boundary must still be honoured.
    reader.Skip(3);

    return version switch {
      3 => ParseV3(ref reader, metadataType, version, dentryFeatures, kind),
      6 => ParseV6(ref reader, metadataType, version, dentryFeatures, kind, currentParentEntryId),
      _ => throw new NotSupportedException($"BeeGFS dentry storage format version {version} is unsupported by the read-only decoder."),
    };
  }

  private static BeeGfsDecodedDentry ParseV3(
      ref Reader reader,
      BeeGfsDiskMetadataType metadataType,
      byte version,
      ushort dentryFeatures,
      FilesystemNodeKind kind) {
    var entryId = reader.ReadAlignedString4();
    var ownerNodeId = (dentryFeatures & DentryFeature32BitIds) != 0
      ? reader.ReadUInt32()
      : reader.ReadUInt16();
    reader.RequireEnd();

    return new BeeGfsDecodedDentry(
      metadataType,
      version,
      dentryFeatures,
      kind,
      entryId,
      ownerNodeId,
      HasInlineInode: false,
      Size: 0,
      LinkCount: 0,
      UserId: 0,
      GroupId: 0,
      OriginalUserId: 0,
      OriginalParentEntryId: string.Empty,
      Mode: 0,
      InodeFeatureFlags: 0,
      FileState: 0,
      Raid0Pattern: null);
  }

  private static BeeGfsDecodedDentry ParseV6(
      ref Reader reader,
      BeeGfsDiskMetadataType metadataType,
      byte version,
      ushort dentryFeatures,
      FilesystemNodeKind kind,
      string currentParentEntryId) {
    if (kind == FilesystemNodeKind.Directory)
      throw new InvalidDataException("BeeGFS directory dentries use the V3 dentry layout, not V6.");
    if ((dentryFeatures & DentryFeatureInodeInline) == 0 && metadataType != BeeGfsDiskMetadataType.FileInode)
      throw new NotSupportedException("BeeGFS V6 file dentry has no inline inode; separate hard-link inode decoding is not implemented yet.");
    if ((dentryFeatures & DentryFeatureBuddyMirrored) != 0)
      throw new NotSupportedException("BeeGFS buddy-mirrored file metadata requires management buddy-group mapping.");

    var inodeFeatures = reader.ReadUInt32();
    if ((inodeFeatures & ~SupportedFileInodeFeatures) != 0)
      throw new NotSupportedException($"BeeGFS file inode uses unknown feature flags 0x{inodeFeatures:X8}.");
    if ((inodeFeatures & (FileInodeFeatureMirrored | FileInodeFeatureBuddyMirrored)) != 0)
      throw new NotSupportedException("BeeGFS mirrored file-inode metadata is outside the current read-only subset.");
    if ((inodeFeatures & FileInodeFeatureHasRemoteStorageTargets) != 0)
      throw new NotSupportedException("BeeGFS remote-storage-target metadata is outside the current read-only subset.");

    var state = (inodeFeatures & FileInodeFeatureHasStateFlags) != 0 ? reader.ReadByte() : (byte)0;
    reader.Skip(3);
    if ((state & 0x1F) != 0)
      throw new NotSupportedException($"BeeGFS file state 0x{state:X2} carries access restrictions; offline reads fail closed.");
    if ((state & 0xE0) != 0)
      throw new NotSupportedException($"BeeGFS file state 0x{state:X2} indicates non-default data availability.");

    var statFlags = reader.ReadUInt32();
    var mode = reader.ReadUInt32();
    if ((statFlags & ~StatFeatureSparseFile) != 0)
      throw new NotSupportedException($"BeeGFS stat data uses unknown feature flags 0x{statFlags:X8}.");
    if ((statFlags & StatFeatureSparseFile) != 0)
      throw new NotSupportedException("BeeGFS sparse-file chunk-block vectors are not decoded yet.");

    _ = reader.ReadInt64(); // creation
    _ = reader.ReadInt64(); // access
    _ = reader.ReadInt64(); // modification
    _ = reader.ReadInt64(); // attribute change
    var size = reader.ReadInt64();
    if (size < 0)
      throw new InvalidDataException($"BeeGFS file inode declares negative size {size}.");
    var links = reader.ReadUInt32();
    _ = reader.ReadUInt32(); // stat metadata version
    var uid = reader.ReadUInt32();
    var gid = reader.ReadUInt32();

    var originalUid = (inodeFeatures & FileInodeFeatureHasOriginalUid) != 0
      ? reader.ReadUInt32()
      : uid;
    var originalParentId = (inodeFeatures & FileInodeFeatureHasOriginalParentId) != 0
      ? reader.ReadAlignedString4()
      : currentParentEntryId;
    var entryId = reader.ReadAlignedString4();
    var pattern = ParseRaid0Pattern(ref reader);

    if ((inodeFeatures & FileInodeFeatureHasVersions) != 0) {
      _ = reader.ReadUInt32(); // file version
      _ = reader.ReadUInt32(); // metadata version
    }
    reader.RequireEnd();

    return new BeeGfsDecodedDentry(
      metadataType,
      version,
      dentryFeatures,
      kind,
      entryId,
      OwnerNodeId: 0,
      HasInlineInode: (dentryFeatures & DentryFeatureInodeInline) != 0,
      Size: size,
      LinkCount: links,
      UserId: uid,
      GroupId: gid,
      OriginalUserId: originalUid,
      OriginalParentEntryId: originalParentId,
      Mode: mode,
      InodeFeatureFlags: inodeFeatures,
      FileState: state,
      Raid0Pattern: pattern);
  }

  private static BeeGfsRaid0Pattern ParseRaid0Pattern(ref Reader reader) {
    var patternStart = reader.Offset;
    var patternLength = reader.ReadUInt32();
    if (patternLength < 18 || patternLength > MaxMetadataBytes)
      throw new InvalidDataException($"BeeGFS stripe pattern length {patternLength} is implausible.");
    if (patternStart + patternLength > reader.Length)
      throw new EndOfStreamException("BeeGFS stripe pattern extends beyond the metadata buffer.");

    var typeWithFlags = reader.ReadUInt32();
    var hasPool = (typeWithFlags & StripePatternHasNoPoolFlag) == 0;
    var type = typeWithFlags & ~StripePatternHasNoPoolFlag;
    if (type != StripePatternRaid0)
      throw new NotSupportedException($"BeeGFS stripe pattern type {type} is unsupported; only RAID0 is decoded currently.");

    var chunkSize = reader.ReadUInt32();
    if (chunkSize < MinimumChunkSize || (chunkSize & (chunkSize - 1)) != 0)
      throw new InvalidDataException($"BeeGFS RAID0 chunk size {chunkSize} is invalid.");
    var poolId = hasPool ? reader.ReadUInt16() : (ushort)1;

    var defaultTargets = reader.ReadUInt32();
    var vectorStart = reader.Offset;
    var vectorBytes = reader.ReadUInt32();
    var targetCount = reader.ReadUInt32();
    if (targetCount is 0 or > ushort.MaxValue)
      throw new InvalidDataException($"BeeGFS RAID0 target count {targetCount} is invalid.");
    var expectedVectorBytes = checked(8u + targetCount * 2u);
    if (vectorBytes != expectedVectorBytes)
      throw new InvalidDataException(
        $"BeeGFS RAID0 target vector length {vectorBytes} does not match {targetCount} target IDs ({expectedVectorBytes} bytes).");
    if (vectorStart + vectorBytes > patternStart + patternLength)
      throw new EndOfStreamException("BeeGFS RAID0 target vector extends past the stripe pattern.");

    var ids = new ushort[checked((int)targetCount)];
    var seen = new HashSet<ushort>();
    for (var i = 0; i < ids.Length; ++i) {
      var id = reader.ReadUInt16();
      if (id == 0)
        throw new InvalidDataException("BeeGFS RAID0 target IDs must be non-zero.");
      if (!seen.Add(id))
        throw new InvalidDataException($"BeeGFS RAID0 target ID {id} appears more than once.");
      ids[i] = id;
    }

    if (reader.Offset != patternStart + patternLength)
      throw new InvalidDataException(
        $"BeeGFS RAID0 stripe pattern consumed {reader.Offset - patternStart} bytes, declared length is {patternLength}.");
    return new BeeGfsRaid0Pattern(chunkSize, poolId, defaultTargets, ids);
  }

  private static FilesystemNodeKind KindFor(byte value) => value switch {
    1 => FilesystemNodeKind.Directory,
    2 => FilesystemNodeKind.RegularFile,
    3 => FilesystemNodeKind.SymbolicLink,
    4 => FilesystemNodeKind.BlockDevice,
    5 => FilesystemNodeKind.CharacterDevice,
    6 => FilesystemNodeKind.Fifo,
    7 => FilesystemNodeKind.Socket,
    _ => throw new InvalidDataException($"BeeGFS dentry type {value} is invalid."),
  };

  private ref struct Reader {
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public Reader(ReadOnlySpan<byte> data) {
      _data = data;
      _offset = 0;
    }

    public int Offset => _offset;
    public int Length => _data.Length;

    public byte ReadByte() {
      Ensure(1);
      return _data[_offset++];
    }

    public ushort ReadUInt16() {
      Ensure(2);
      var result = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
      _offset += 2;
      return result;
    }

    public uint ReadUInt32() {
      Ensure(4);
      var result = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
      _offset += 4;
      return result;
    }

    public long ReadInt64() {
      Ensure(8);
      var result = BinaryPrimitives.ReadInt64LittleEndian(_data[_offset..]);
      _offset += 8;
      return result;
    }

    public string ReadAlignedString4() {
      var fieldStart = _offset;
      var byteLength = ReadUInt32();
      if (byteLength > MaxMetadataBytes)
        throw new InvalidDataException($"BeeGFS metadata string length {byteLength} is implausible.");
      var length = checked((int)byteLength);
      Ensure(checked(length + 1));
      var bytes = _data.Slice(_offset, length);
      _offset += length;
      if (_data[_offset++] != 0)
        throw new InvalidDataException("BeeGFS metadata string is not NUL terminated.");
      var used = _offset - fieldStart;
      var padding = (4 - (used & 3)) & 3;
      Ensure(padding);
      for (var i = 0; i < padding; ++i)
        if (_data[_offset + i] != 0)
          throw new InvalidDataException("BeeGFS metadata string padding is non-zero.");
      _offset += padding;
      return Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int count) {
      Ensure(count);
      _offset += count;
    }

    public void RequireEnd() {
      if (_offset != _data.Length)
        throw new InvalidDataException($"BeeGFS metadata has {_data.Length - _offset} unexpected trailing byte(s).");
    }

    private void Ensure(int count) {
      if (count < 0 || _offset > _data.Length - count)
        throw new EndOfStreamException("BeeGFS metadata is truncated.");
    }
  }
}
