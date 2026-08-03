#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Sfs;

/// <summary>
/// The structures of the Amiga Smart File System, as the filesystem's own
/// source lays them out.
/// </summary>
/// <remarks>
/// <para>These track <c>rom/filesys/SFS/FS</c> in AROS, which is John
/// Hendrikx's SFS with the block structures unchanged. Everything is
/// big-endian, and every block that carries a header is checksummed the same
/// way: the header's checksum word is whatever makes the block's longwords sum
/// to zero.</para>
///
/// <para>The root block's field offsets were four bytes short here before. It
/// carries two reserved longwords after the flag byte, not one, so everything
/// from the partition's first byte onwards was read one word early — the block
/// count came out of the partition's last byte and the block size out of the
/// block count. Nothing noticed, because nothing read past the root block.</para>
/// </remarks>
internal static class SfsLayout {

  /// <summary>Bytes of header on every block that has one: id, checksum, own block.</summary>
  internal const int BlockHeaderBytes = 12;

  internal static readonly byte[] RootId = "SFS\0"u8.ToArray();
  internal const uint ObjectContainerId = 0x4F424A43;   // "OBJC"
  internal const uint BitmapId = 0x42544D50;            // "BTMP"
  internal const uint NodeContainerId = 0x4E444320;     // "NDC "
  internal const uint BNodeContainerId = 0x424E4443;    // "BNDC"
  internal const uint AdminSpaceContainerId = 0x41444D43; // "ADMC"

  /// <summary>The object node the root directory always has.</summary>
  internal const uint RootNode = 1;

  /// <summary>The version this writes, which is the one the structures describe.</summary>
  internal const ushort StructureVersion = 3;

  // ── root block ─────────────────────────────────────────────────────────
  internal const int RbVersion = 12;
  internal const int RbSequenceNumber = 14;
  internal const int RbDateCreated = 16;
  internal const int RbBits = 20;
  internal const int RbFirstByteHigh = 32;
  internal const int RbFirstByte = 36;
  internal const int RbLastByteHigh = 40;
  internal const int RbLastByte = 44;
  internal const int RbTotalBlocks = 48;
  internal const int RbBlockSize = 52;
  internal const int RbBitmapBase = 96;
  internal const int RbAdminSpaceContainer = 100;
  internal const int RbRootObjectContainer = 104;
  internal const int RbExtentBNodeRoot = 108;
  internal const int RbObjectNodeRoot = 112;
  internal const int RootBlockBytes = 128;

  /// <summary>The volume is case sensitive.</summary>
  internal const byte RootBitsCaseSensitive = 128;

  // ── object container ───────────────────────────────────────────────────
  internal const int OcParent = 12;
  internal const int OcNext = 16;
  internal const int OcPrevious = 20;
  /// <summary>Where the objects themselves begin.</summary>
  internal const int OcObjects = 24;

  // ── one object inside a container ──────────────────────────────────────
  internal const int ObOwnerUid = 0;
  internal const int ObOwnerGid = 2;
  internal const int ObObjectNode = 4;
  internal const int ObProtection = 8;
  /// <summary>A file's first extent key; a directory's hash table block.</summary>
  internal const int ObData = 12;
  /// <summary>A file's byte count; a directory's first directory block.</summary>
  internal const int ObSize = 16;
  internal const int ObDateModified = 20;
  internal const int ObBits = 24;
  /// <summary>Where the name starts. The comment follows its terminator.</summary>
  internal const int ObName = 25;

  internal const byte OTypeDir = 128;

  /// <summary>
  /// What one object occupies: the fixed part, both terminated strings, and a
  /// pad byte when that lands on an odd address.
  /// </summary>
  internal static int ObjectBytes(int nameLength, int commentLength) {
    var length = ObName + nameLength + 1 + commentLength + 1;
    return (length + 1) & ~1;
  }

  // ── B-tree container ───────────────────────────────────────────────────
  /// <summary>Where the tree's own header sits inside its block.</summary>
  internal const int BtcHeader = BlockHeaderBytes;
  internal const int BtcNodeCount = BtcHeader + 0;
  internal const int BtcIsLeaf = BtcHeader + 2;
  internal const int BtcNodeSize = BtcHeader + 3;
  /// <summary>Where the tree's entries begin.</summary>
  internal const int BtcNodes = BtcHeader + 4;

  /// <summary>
  /// One extent: the block it starts at, the keys either side of it in the
  /// file, and how many blocks it covers.
  /// </summary>
  internal const int ExtentNodeBytes = 14;
  internal const int ExKey = 0;
  internal const int ExNext = 4;
  internal const int ExPrev = 8;
  internal const int ExBlocks = 12;

  // ── node container ─────────────────────────────────────────────────────
  internal const int NcNodeNumber = 12;
  internal const int NcNodes = 16;
  internal const int NcFirstNode = 20;

  // ── admin space container ──────────────────────────────────────────────
  internal const int AscNext = 12;
  internal const int AscPrevious = 16;
  internal const int AscBits = 20;
  internal const int AscSpaces = 24;

  /// <summary>
  /// Stamps a block's checksum: the word that makes the whole block's
  /// longwords sum to zero, which is what the filesystem checks on reading one.
  /// </summary>
  internal static void SetChecksum(Span<byte> block) {
    BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0);

    var sum = 0u;
    for (var at = 0; at + 4 <= block.Length; at += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(block[at..]);

    BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0u - sum);
  }

  /// <summary>Whether a block's checksum is the one it should carry.</summary>
  internal static bool ChecksumHolds(ReadOnlySpan<byte> block) {
    var sum = 0u;
    for (var at = 0; at + 4 <= block.Length; at += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(block[at..]);
    return sum == 0;
  }
}
