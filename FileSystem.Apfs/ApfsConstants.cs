#pragma warning disable CS1591
namespace FileSystem.Apfs;

/// <summary>
/// Constants from Apple's "Apple File System Reference" (public spec).
/// </summary>
internal static class ApfsConstants {
  // Object type mask/flags (obj_phys_t.o_type low 16 bits = type, high 16 = flags).
  public const uint OBJECT_TYPE_MASK = 0x0000FFFF;
  public const uint OBJECT_TYPE_FLAGS_MASK = 0xFFFF0000;

  public const uint OBJECT_TYPE_NX_SUPERBLOCK = 0x00000001;
  public const uint OBJECT_TYPE_BTREE = 0x00000002;
  public const uint OBJECT_TYPE_BTREE_NODE = 0x00000003;
  public const uint OBJECT_TYPE_SPACEMAN = 0x00000005;
  public const uint OBJECT_TYPE_SPACEMAN_CAB = 0x00000006;
  public const uint OBJECT_TYPE_SPACEMAN_CIB = 0x00000007;
  public const uint OBJECT_TYPE_SPACEMAN_BITMAP = 0x00000008;
  public const uint OBJECT_TYPE_OMAP = 0x0000000B;
  public const uint OBJECT_TYPE_CHECKPOINT_MAP = 0x0000000C;
  public const uint OBJECT_TYPE_FS = 0x0000000D; // APFS volume
  public const uint OBJECT_TYPE_FSTREE = 0x0000000E;
  public const uint OBJECT_TYPE_BLOCKREFTREE = 0x0000000F;
  public const uint OBJECT_TYPE_SNAPMETATREE = 0x00000010;

  // Storage flags (upper 16 bits of o_type).
  public const uint OBJ_VIRTUAL = 0x00000000;
  public const uint OBJ_EPHEMERAL = 0x80000000;
  public const uint OBJ_PHYSICAL = 0x40000000;

  // NX features.
  public const ulong NX_INCOMPAT_VERSION2 = 0x2;

  // Filesystem-tree key types (high nibble of oid_and_type).
  public const int APFS_TYPE_SNAP_METADATA = 1;
  public const int APFS_TYPE_EXTENT = 2;
  public const int APFS_TYPE_INODE = 3;
  public const int APFS_TYPE_XATTR = 4;
  public const int APFS_TYPE_SIBLING_LINK = 5;
  public const int APFS_TYPE_DSTREAM_ID = 6;
  public const int APFS_TYPE_CRYPTO_STATE = 7;
  public const int APFS_TYPE_FILE_EXTENT = 8;
  public const int APFS_TYPE_DIR_REC = 9;
  public const int APFS_TYPE_DIR_STATS = 10;
  public const int APFS_TYPE_SNAP_NAME = 11;
  public const int APFS_TYPE_SIBLING_MAP = 12;

  // Reserved object IDs.
  public const ulong NX_SUPERBLOCK_OID = 1;
  public const ulong APFS_ROOT_DIR_INO_NUM = 2;
  public const ulong APFS_PRIV_DIR_INO_NUM = 3;
  public const ulong APFS_SNAP_DIR_INO_NUM = 6;
  public const ulong APFS_MIN_USER_INO_NUM = 16;

  // B-tree node flags.
  public const ushort BTNODE_ROOT = 0x0001;
  public const ushort BTNODE_LEAF = 0x0002;
  public const ushort BTNODE_FIXED_KV_SIZE = 0x0004;

  // Inode flags / modes.
  public const ushort APFS_DIR_REC_FLAGS_MASK = 0x000F;
  public const byte DT_DIR = 4;
  public const byte DT_REG = 8;

  public const ushort S_IFDIR = 0x4000;
  public const ushort S_IFREG = 0x8000;

  // BTOFF_INVALID (ffff) used in TOC for absent entries.
  public const ushort BTOFF_INVALID = 0xFFFF;

  // Default 4096-byte APFS block size.
  public const uint DEFAULT_BLOCK_SIZE = 4096;

  // Minimum viable APFS image per spec.
  public const long MIN_APFS_IMAGE_SIZE = 512L * 1024 * 1024;
}
