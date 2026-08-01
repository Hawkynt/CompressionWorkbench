#pragma warning disable CS1591
namespace FileSystem.SmartFs;

/// <summary>
/// The on-disk shape of a SmartFS volume, as NuttX lays it out.
/// </summary>
/// <remarks>
/// <para>SmartFS divides the flash into equal sectors. Every sector opens with
/// a five-byte header naming the logical sector it currently holds, which is
/// what lets the wear-levelling layer move a logical sector to a different
/// physical one without anything above noticing. A freshly formatted volume —
/// which is what this writer emits — maps logical to physical one to one.</para>
///
/// <para>Past that header, a sector that carries a chain (a directory or a
/// file) opens with a five-byte chain header: the next logical sector, how many
/// bytes of this one are used, and what kind of chain it is. A directory's
/// payload is a run of fixed-size entries; a file's payload is its bytes.</para>
///
/// <para>References: <c>fs/smartfs/smartfs.h</c> and
/// <c>drivers/mtd/smart.c</c> in Apache NuttX.</para>
/// </remarks>
internal static class SmartFsLayout {

  /// <summary>Bytes of per-sector header: logical sector, sequence, CRC, status.</summary>
  public const int SectorHeaderSize = 5;

  /// <summary>Bytes of chain header: next sector, used count, chain type.</summary>
  public const int ChainHeaderSize = 5;

  /// <summary>Offset of the "SMRT" signature inside the format sector's data.</summary>
  public const int SignatureOffset = 10;

  /// <summary>Signature NuttX stamps into a formatted volume.</summary>
  public static readonly byte[] Signature = "SMRT"u8.ToArray();

  /// <summary>The format version this writer emits and the reader understands.</summary>
  public const byte FormatVersion = 1;

  /// <summary>Logical sector the root directory always occupies.</summary>
  public const ushort RootDirSector = 3;

  /// <summary>First logical sector free for directory and file data.</summary>
  public const ushort FirstDataSector = 4;

  /// <summary>Value in a next-sector field meaning "the chain ends here".</summary>
  public const ushort EndOfChain = 0xFFFF;

  /// <summary>Sector status byte of a sector that has been written and committed.</summary>
  public const byte StatusCommitted = 0x00;

  /// <summary>Chain type of a directory sector.</summary>
  public const byte ChainTypeDirectory = 1;

  /// <summary>Chain type of a file sector.</summary>
  public const byte ChainTypeFile = 2;

  /// <summary>Bytes of one directory entry: flags, first sector, timestamp, name.</summary>
  public const int EntryHeaderSize = 8;

  /// <summary>Characters a name may have — NuttX's CONFIG_SMARTFS_MAXNAMLEN default.</summary>
  public const int MaxNameLength = 16;

  /// <summary>Bytes one directory entry occupies in a directory sector.</summary>
  public const int EntrySize = EntryHeaderSize + MaxNameLength;

  /// <summary>Set on an entry that names something.</summary>
  public const ushort EntryActive = 0x4000;

  /// <summary>Set on an entry that names a directory rather than a file.</summary>
  public const ushort EntryDirectory = 0x2000;

  /// <summary>Permission bits an entry carries; 0644 for the files this writer emits.</summary>
  public const ushort EntryModeMask = 0x01FF;

  /// <summary>Sector-size code for the size NuttX records in the format sector.</summary>
  public static byte SizeCode(int sectorSize) => sectorSize switch {
    256 => 0, 512 => 1, 1024 => 2, 2048 => 3, 4096 => 4,
    _ => throw new ArgumentOutOfRangeException(nameof(sectorSize),
      $"SmartFS sectors are 256, 512, 1024, 2048 or 4096 bytes; got {sectorSize}."),
  };

  /// <summary>The sector size a code stands for, or zero when it stands for none.</summary>
  public static int SizeFromCode(byte code) => code switch {
    0 => 256, 1 => 512, 2 => 1024, 3 => 2048, 4 => 4096,
    _ => 0,
  };
}
