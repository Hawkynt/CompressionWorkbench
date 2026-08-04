#pragma warning disable CS1591
namespace FileSystem.Hpfs;

/// <summary>
/// Where the fields of an HPFS fnode actually are.
/// </summary>
/// <remarks>
/// <para>These follow <c>struct fnode</c> in the kernel's HPFS driver. The
/// offsets used here before were not that struct: the parent pointer sat in the
/// middle of the short name, the directory flag in the ACL length, and the
/// allocation list 138 bytes past where it belongs — inside the user-id field.
/// A volume written that way was self-consistent and read by nothing else.</para>
///
/// <para>The allocation header is also checked, not merely read. A driver
/// insists that the used and free node counts add up to the number of slots
/// that follow — eight runs, or twelve subtree pointers — and rejects the fnode
/// when they do not, which is what "bad number of nodes in fnode" means.</para>
/// </remarks>
internal static class HpfsLayout {

  internal const int SectorSize = 512;

  /// <summary>The parent directory's fnode.</summary>
  internal const int FnUp = 0x1C;

  /// <summary>Flags; bit 8 says the fnode is a directory.</summary>
  internal const int FnFlags = 0x36;

  internal const ushort FlagDirectory = 0x0100;

  /// <summary>The allocation b-plus header: flags, three fill bytes, counts, first free.</summary>
  internal const int FnBtree = 0x38;
  internal const int BtFlags = 0;
  internal const int BtFreeNodes = 4;
  internal const int BtUsedNodes = 5;
  internal const int BtFirstFree = 6;

  /// <summary>Where the runs themselves start.</summary>
  internal const int FnAlloc = 0x40;

  /// <summary>One run: the file offset it covers, how many sectors, and where.</summary>
  internal const int RunFileSector = 0;
  internal const int RunLength = 4;
  internal const int RunDiskSector = 8;
  internal const int RunBytes = 12;

  /// <summary>How many runs an fnode holds before it needs a tree of them.</summary>
  internal const int LeafSlots = 8;

  /// <summary>The file's length in bytes.</summary>
  internal const int FnFileSize = 0xA0;

  /// <summary>Where the first extended attribute would be, if there were any.</summary>
  internal const int FnEaOffset = 0xB8;

  /// <summary>
  /// Fills an fnode's allocation header for a single run of sectors.
  /// </summary>
  /// <remarks>
  /// The free count is what makes the sum come out at eight. Leaving it zero —
  /// as this once did, along with the used count — gives a header a driver
  /// refuses before it reads a single run.
  /// </remarks>
  internal static void WriteLeafHeader(Span<byte> fnode, int usedRuns) {
    fnode[FnBtree + BtFlags] = 0;                                   // a leaf, and not a subtree
    fnode[FnBtree + BtFreeNodes] = (byte)(LeafSlots - usedRuns);
    fnode[FnBtree + BtUsedNodes] = (byte)usedRuns;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
      fnode[(FnBtree + BtFirstFree)..], (ushort)(FnAlloc - FnBtree + usedRuns * RunBytes));
  }
}
