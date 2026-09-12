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
  public const uint OBJECT_TYPE_SPACEMAN_FREE_QUEUE = 0x00000009;
  public const uint OBJECT_TYPE_NX_REAPER = 0x00000011;

  /// <summary>The first object identifier an ephemeral object may take.</summary>
  public const ulong OID_RESERVED_COUNT = 1024;
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

  // ── Volume superblock (apfs_superblock) field offsets ──────────────────────
  //
  // These are the struct's own offsets. The encryption state at 0x60 is twenty
  // bytes; reading it as anything longer moves every field after it, including
  // the two identifiers a mount follows to reach the volume's object map and its
  // filesystem tree — which is how a volume comes to be reported as encrypted
  // and rootless when it is neither.
  public const int APSB_MAGIC = 0x20;
  public const int APSB_FS_INDEX = 0x24;
  public const int APSB_FEATURES = 0x28;
  public const int APSB_READONLY_COMPAT_FEATURES = 0x30;
  public const int APSB_INCOMPAT_FEATURES = 0x38;
  public const int APSB_UNMOUNT_TIME = 0x40;
  public const int APSB_RESERVE_BLOCK_COUNT = 0x48;
  public const int APSB_QUOTA_BLOCK_COUNT = 0x50;
  public const int APSB_ALLOC_COUNT = 0x58;
  public const int APSB_ROOT_TREE_TYPE = 0x74;
  public const int APSB_EXTENTREF_TREE_TYPE = 0x78;
  public const int APSB_SNAP_META_TREE_TYPE = 0x7C;
  public const int APSB_OMAP_OID = 0x80;
  public const int APSB_ROOT_TREE_OID = 0x88;
  public const int APSB_EXTENTREF_TREE_OID = 0x90;
  public const int APSB_SNAP_META_TREE_OID = 0x98;
  public const int APSB_REVERT_TO_XID = 0xA0;
  public const int APSB_REVERT_TO_SBLOCK_OID = 0xA8;
  public const int APSB_NEXT_OBJ_ID = 0xB0;
  public const int APSB_NUM_FILES = 0xB8;
  public const int APSB_NUM_DIRECTORIES = 0xC0;
  public const int APSB_VOL_UUID = 0xF0;
  public const int APSB_LAST_MOD_TIME = 0x100;
  public const int APSB_FS_FLAGS = 0x108;
  public const int APSB_FORMATTED_BY = 0x110;
  public const int APSB_VOLNAME = 0x2C0;
  public const int APSB_VOLNAME_LEN = 256;

  /// <summary>apfs_next_doc_id — the next document identifier this volume will hand out.</summary>
  public const int APSB_NEXT_DOC_ID = APSB_VOLNAME + APSB_VOLNAME_LEN;

  /// <summary>The lowest document identifier a volume may say it will hand out next.</summary>
  public const uint APFS_MIN_DOC_ID = 3;

  /// <summary>The apfs_fs_flags bit that says the volume is not encrypted.</summary>
  public const ulong APFS_FS_UNENCRYPTED = 0x1;

  /// <summary>The inode internal flag marking an object the filesystem owns itself.</summary>
  public const ulong APFS_INODE_IS_APFS_PRIVATE = 0x00000001;

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
  public const ulong APFS_ROOT_DIR_PARENT = 1;
  public const ulong APFS_ROOT_DIR_INO_NUM = 2;
  public const ulong APFS_PRIV_DIR_INO_NUM = 3;
  public const ulong APFS_SNAP_DIR_INO_NUM = 6;
  public const ulong APFS_MIN_USER_INO_NUM = 16;

  // B-tree node flags.
  public const ushort BTNODE_ROOT = 0x0001;
  public const ushort BTNODE_LEAF = 0x0002;
  public const ushort BTNODE_FIXED_KV_SIZE = 0x0004;

  /// <summary>btree_info flag: this tree's nodes are physical objects.</summary>
  /// <remarks>
  /// Read off a container mkfs.apfs built: its object map's footer carries 0x10
  /// and its free-queue trees carry 0x0C — ephemeral, allowing ghosts — while
  /// both state their key and value sizes. So the sizes are what say a tree is
  /// fixed; this bit says where its nodes live.
  /// </remarks>
  public const uint BTREE_PHYSICAL = 0x00000010;

  /// <summary>btree_info flag: this tree's nodes are ephemeral objects.</summary>
  public const uint BTREE_EPHEMERAL = 0x00000008;

  /// <summary>btree_info flag: a key may be present with no value.</summary>
  public const uint BTREE_ALLOW_GHOSTS = 0x00000004;

  /// <summary>btree_info flag: keys and values are not padded to an alignment.</summary>
  /// <remarks>
  /// Every variable-length tree in a container mkfs.apfs builds carries it — the
  /// filesystem tree, the extent-reference tree and the snapshot-metadata tree
  /// alike.
  /// </remarks>
  public const uint BTREE_KV_NONALIGNED = 0x00000040;

  // Inode flags / modes.
  public const ushort APFS_DIR_REC_FLAGS_MASK = 0x000F;
  public const byte DT_DIR = 4;
  public const byte DT_REG = 8;

  public const ushort S_IFDIR = 0x4000;
  public const ushort S_IFREG = 0x8000;
  public const ushort S_IFLNK = 0xA000;

  // Extended-attribute record flags (j_xattr_flags). Embedded xattrs carry their
  // value inline in the record; a symlink's target lives in the embedded xattr
  // named "com.apple.fs.symlink".
  public const ushort XATTR_DATA_STREAM = 0x0001;
  public const ushort XATTR_DATA_EMBEDDED = 0x0002;
  public const string SYMLINK_XATTR_NAME = "com.apple.fs.symlink";

  // BTOFF_INVALID (ffff) used in TOC for absent entries.
  public const ushort BTOFF_INVALID = 0xFFFF;

  // Default 4096-byte APFS block size.
  public const uint DEFAULT_BLOCK_SIZE = 4096;

  // Minimum viable APFS image per spec.
  public const long MIN_APFS_IMAGE_SIZE = 512L * 1024 * 1024;
}
