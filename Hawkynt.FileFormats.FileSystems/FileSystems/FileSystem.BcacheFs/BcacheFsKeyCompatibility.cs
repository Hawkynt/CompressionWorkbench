#pragma warning disable CS1591
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Compatibility transforms that affect the common bkey header and therefore
/// matter before type-specific value decoding. This mirrors the structural
/// parts of bch2_bkey_compat() needed by journal and clean-checkpoint keys.
/// </summary>
internal static class BcacheFsKeyCompatibility {
  private const uint VersionBkeyRenumber = 10;
  private const uint VersionInodeBtreeChange = 11;
  private const uint VersionSnapshot = 12;

  internal static BcacheFsRawKey Apply(
      BcacheFsRawKey key,
      byte btreeId,
      byte level,
      uint version) {
    ArgumentNullException.ThrowIfNull(key);

    var rawType = key.RawType;
    if (version < VersionBkeyRenumber)
      rawType = RenumberLegacyKeyType(btreeId, level, rawType);

    var position = key.Position;
    if (version < VersionInodeBtreeChange && btreeId == (byte)BcacheFsBtreeId.Inodes)
      position = new Bpos(position.Offset, position.Inode, position.Snapshot);

    if (version < VersionSnapshot && (level != 0 || HasSnapshots(btreeId)))
      position = position with { Snapshot = uint.MaxValue };

    return key with { RawType = rawType, Position = position };
  }

  private static byte RenumberLegacyKeyType(byte btreeId, byte level, byte rawType) {
    if (level != 0)
      return rawType == 128 ? (byte)BcacheFsKeyType.BtreePtr : rawType;

    return ((BcacheFsBtreeId)btreeId, rawType) switch {
      (BcacheFsBtreeId.Extents, 128 or 129) => (byte)BcacheFsKeyType.Extent,
      (BcacheFsBtreeId.Extents, 130) => (byte)BcacheFsKeyType.Reservation,
      (BcacheFsBtreeId.Inodes, 128) => (byte)BcacheFsKeyType.Inode,
      (BcacheFsBtreeId.Inodes, 130) => (byte)BcacheFsKeyType.InodeGeneration,
      (BcacheFsBtreeId.Dirents, 128) => (byte)BcacheFsKeyType.Dirent,
      (BcacheFsBtreeId.Dirents, 129) => (byte)BcacheFsKeyType.HashWhiteout,
      (BcacheFsBtreeId.Xattrs, 128) => (byte)BcacheFsKeyType.Xattr,
      (BcacheFsBtreeId.Xattrs, 129) => (byte)BcacheFsKeyType.HashWhiteout,
      (BcacheFsBtreeId.Alloc, 128) => (byte)BcacheFsKeyType.Alloc,
      (BcacheFsBtreeId.Quotas, 128) => (byte)BcacheFsKeyType.Quota,
      _ => rawType,
    };
  }

  private static bool HasSnapshots(byte btreeId)
    => btreeId is
      (byte)BcacheFsBtreeId.Extents or
      (byte)BcacheFsBtreeId.Inodes or
      (byte)BcacheFsBtreeId.Dirents or
      (byte)BcacheFsBtreeId.Xattrs;
}
