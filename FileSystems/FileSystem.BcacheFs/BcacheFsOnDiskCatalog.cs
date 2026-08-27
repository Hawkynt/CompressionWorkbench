#pragma warning disable CS1591

namespace FileSystem.BcacheFs;

/// <summary>
/// Canonical identifiers from the bcachefs 1.38 on-disk format.
/// </summary>
/// <remarks>
/// Keep this file boring and exhaustive. Higher layers must not infer support
/// from a short hand-written list of trees/key types: a driver-grade reader must
/// be able to name every structure it encounters even when the semantic codec
/// for that structure lives elsewhere.
///
/// Source of truth: fs/bcachefs/bcachefs_format.h from the canonical bcachefs
/// source tree, metadata version 1.38.
/// </remarks>
internal static class BcacheFsOnDiskCatalog {
  internal const int MetadataVersionMajor = 1;
  internal const int MetadataVersionMinor = 38;
  internal const ushort MetadataVersion = (MetadataVersionMajor << 10) | MetadataVersionMinor;
  internal const int MaxBtreeDepth = 4;
  internal const int MaxBtreeId = 62;
  internal const ulong JournalSetMagicXor = 0x245235C1A3625032UL;
  internal const ulong BtreeSetMagicXor = 0x90135C78B99E07F5UL;

  internal static IReadOnlyList<BcacheFsBtreeId> KnownBtrees { get; } =
    Enum.GetValues<BcacheFsBtreeId>();

  internal static IReadOnlyList<BcacheFsKeyType> KnownKeyTypes { get; } =
    Enum.GetValues<BcacheFsKeyType>();

  internal static bool IsAllocationTree(BcacheFsBtreeId id) => id is
    BcacheFsBtreeId.Alloc or
    BcacheFsBtreeId.Backpointers or
    BcacheFsBtreeId.StripeBackpointers or
    BcacheFsBtreeId.NeedDiscard or
    BcacheFsBtreeId.Freespace or
    BcacheFsBtreeId.BucketGens or
    BcacheFsBtreeId.Lru or
    BcacheFsBtreeId.Accounting or
    BcacheFsBtreeId.ReconcileWork or
    BcacheFsBtreeId.ReconcileHipri or
    BcacheFsBtreeId.ReconcilePending or
    BcacheFsBtreeId.ReconcileScan;

  internal static bool CanReconstruct(BcacheFsBtreeId id) =>
    IsAllocationTree(id) || id is
      BcacheFsBtreeId.SnapshotTrees or
      BcacheFsBtreeId.DeletedInodes or
      BcacheFsBtreeId.ReconcileWork or
      BcacheFsBtreeId.ReconcileHipri or
      BcacheFsBtreeId.ReconcilePending or
      BcacheFsBtreeId.ReconcileScan or
      BcacheFsBtreeId.SubvolumeChildren;
}

internal enum BcacheFsSuperblockFieldType : uint {
  Journal = 0,
  MembersV1 = 1,
  Crypt = 2,
  ReplicasV0 = 3,
  Quota = 4,
  DiskGroups = 5,
  Clean = 6,
  Replicas = 7,
  JournalSequenceBlacklist = 8,
  JournalV2 = 9,
  Counters = 10,
  MembersV2 = 11,
  Errors = 12,
  Ext = 13,
  Downgrade = 14,
  RecoveryPasses = 15,
  ExtentTypeU64s = 16,
}

internal enum BcacheFsBtreeId : byte {
  Extents = 0,
  Inodes = 1,
  Dirents = 2,
  Xattrs = 3,
  Alloc = 4,
  Quotas = 5,
  Stripes = 6,
  Reflink = 7,
  Subvolumes = 8,
  Snapshots = 9,
  Lru = 10,
  Freespace = 11,
  NeedDiscard = 12,
  Backpointers = 13,
  BucketGens = 14,
  SnapshotTrees = 15,
  DeletedInodes = 16,
  LoggedOps = 17,
  ReconcileWork = 18,
  SubvolumeChildren = 19,
  Accounting = 20,
  ReconcileHipri = 21,
  ReconcilePending = 22,
  ReconcileScan = 23,
  ReconcileWorkPhysical = 24,
  ReconcileHipriPhysical = 25,
  BucketToStripe = 26,
  StripeBackpointers = 27,
}

internal enum BcacheFsKeyType : byte {
  Deleted = 0,
  Whiteout = 1,
  Error = 2,
  Cookie = 3,
  HashWhiteout = 4,
  BtreePtr = 5,
  Extent = 6,
  Reservation = 7,
  Inode = 8,
  InodeGeneration = 9,
  Dirent = 10,
  Xattr = 11,
  Alloc = 12,
  Quota = 13,
  Stripe = 14,
  ReflinkP = 15,
  ReflinkV = 16,
  InlineData = 17,
  BtreePtrV2 = 18,
  IndirectInlineData = 19,
  AllocV2 = 20,
  Subvolume = 21,
  Snapshot = 22,
  InodeV2 = 23,
  AllocV3 = 24,
  Set = 25,
  Lru = 26,
  AllocV4 = 27,
  Backpointer = 28,
  InodeV3 = 29,
  BucketGens = 30,
  SnapshotTree = 31,
  LoggedOpTruncate = 32,
  LoggedOpFinsert = 33,
  Accounting = 34,
  InodeAllocCursor = 35,
  ExtentWhiteout = 36,
  LoggedOpStripeUpdate = 37,
}

internal enum BcacheFsJournalEntryType : byte {
  BtreeKeys = 0,
  BtreeRoot = 1,
  PrioPtrs = 2,
  Blacklist = 3,
  BlacklistV2 = 4,
  Usage = 5,
  DataUsage = 6,
  Clock = 7,
  DevUsage = 8,
  Log = 9,
  Overwrite = 10,
  WriteBufferKeys = 11,
  DateTime = 12,
  LogBkey = 13,
  RewindLimit = 14,
  Rewind = 15,
}

internal enum BcacheFsChecksumType : byte {
  None = 0,
  Crc32CNonzero = 1,
  Crc64Nonzero = 2,
  ChaCha20Poly1305_80 = 3,
  ChaCha20Poly1305_128 = 4,
  Crc32C = 5,
  Crc64 = 6,
  XxHash = 7,
}

internal enum BcacheFsCompressionType : byte {
  None = 0,
  Lz4Old = 1,
  Gzip = 2,
  Lz4 = 3,
  Zstd = 4,
  Incompressible = 5,
}
