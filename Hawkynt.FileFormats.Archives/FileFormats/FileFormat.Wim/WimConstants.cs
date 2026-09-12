namespace FileFormat.Wim;

/// <summary>
/// Constants for the Windows Imaging (WIM) file format.
/// </summary>
public static class WimConstants {
  /// <summary>
  /// The WIM file magic bytes: "MSWIM\0\0\0" (8 bytes).
  /// </summary>
  public static ReadOnlySpan<byte> Magic => "MSWIM\0\0\0"u8;

  /// <summary>
  /// Length of the magic signature in bytes.
  /// </summary>
  public const int MagicLength = 8;

  /// <summary>
  /// Total size of the WIM file header in bytes (version 1.13).
  /// </summary>
  public const int HeaderSize = 208;

  /// <summary>
  /// WIM format version field value for version 1.13 (0x00010D00, little-endian).
  /// </summary>
  public const uint Version = 0x00010D00u;

  /// <summary>
  /// The version an LZMS image carries instead, with 128 KB chunks rather than
  /// 32 KB. An LZMS resource is only ever found in one of these, so writing the
  /// ordinary version alongside LZMS would mark the container as ours.
  /// </summary>
  public const uint VersionSolid = 0x00000E00u;

  /// <summary>Chunk size an LZMS image uses.</summary>
  public const int SolidChunkSize = 128 * 1024;

  /// <summary>
  /// Default chunk size for compressed resources: 32 768 bytes (32 KB).
  /// </summary>
  public const int DefaultChunkSize = 32 * 1024;

  /// <summary>
  /// Size of a RESHDR_DISK_SHORT structure (packed size+flags, offset, original size).
  /// </summary>
  public const int ReshdrDiskShortSize = 24;

  /// <summary>
  /// Size of a single lookup table entry in the resource table, in bytes.
  /// Each entry holds: RESHDR_DISK_SHORT (24 bytes) + part number (2) + ref count (4) + SHA-1 hash (20).
  /// </summary>
  public const int LookupTableEntrySize = 50;

  // -------------------------------------------------------------------------
  // Compression type codes (stored in the header flags field)
  // -------------------------------------------------------------------------

  /// <summary>No compression — resources are stored verbatim.</summary>
  public const uint CompressionNone = 0;

  /// <summary>XPRESS (LZ Xpress plain) compression.</summary>
  public const uint CompressionXpress = 1;

  /// <summary>LZX compression.</summary>
  public const uint CompressionLzx = 2;

  /// <summary>LZMS compression.</summary>
  public const uint CompressionLzms = 3;

  /// <summary>XPRESS Huffman compression.</summary>
  public const uint CompressionXpressHuffman = 4;

  // -------------------------------------------------------------------------
  // Header flag bits (WimFlags field in the header)
  // -------------------------------------------------------------------------

  /// <summary>
  /// Flag bit indicating resources may be compressed. A reader that does not see
  /// this bit treats the WIM as uncompressed however the algorithm bits read, so
  /// it has to be set alongside them.
  /// </summary>
  public const uint FlagCompression = 0x00000002u;

  /// <summary>
  /// Flag bit indicating reparse-point path fixups have been applied. Set on
  /// every image written here: with no reparse points there is nothing left to
  /// fix, which is the state the bit describes.
  /// </summary>
  public const uint FlagRpFix = 0x00000080u;

  /// <summary>Flag bit indicating the WIM uses XPRESS compression.</summary>
  public const uint FlagXpressCompression = 0x00020000u;

  /// <summary>Flag bit indicating the WIM uses LZX compression.</summary>
  public const uint FlagLzxCompression = 0x00040000u;

  /// <summary>Flag bit indicating the WIM uses LZMS compression.</summary>
  public const uint FlagLzmsCompression = 0x00080000u;

  /// <summary>
  /// Flag bit indicating the WIM uses the second XPRESS arrangement, which
  /// differs from the first in chunk size rather than in encoding.
  /// </summary>
  /// <remarks>
  /// The compression a WIM calls XPRESS is already the Huffman one; there is no
  /// separate type for it, and the value written here used to be one this
  /// project had invented, which no reader recognised.
  /// </remarks>
  public const uint FlagXpressHuffmanCompression = 0x00200000u;

  // -------------------------------------------------------------------------
  // Resource flags
  // -------------------------------------------------------------------------

  /// <summary>Resource flag: resource is stored uncompressed.</summary>
  public const uint ResourceFlagUncompressed = 0u;

  /// <summary>Resource flag bit 0: the entry describes free space, not a resource.</summary>
  public const uint ResourceFlagFree = 1u;

  /// <summary>Resource flag bit 1: resource contains image metadata.</summary>
  public const uint ResourceFlagMetadata = 2u;

  /// <summary>Resource flag bit 2: resource data is compressed.</summary>
  public const uint ResourceFlagCompressed = 4u;

  /// <summary>Resource flag bit 3: resource data continues in another part.</summary>
  public const uint ResourceFlagSpanned = 8u;

  // -------------------------------------------------------------------------
  // Image metadata resource
  // -------------------------------------------------------------------------

  /// <summary>
  /// Size of the fixed part of a directory entry, ahead of the file name.
  /// </summary>
  public const int DirEntryFixedSize = 102;

  /// <summary>
  /// Size of an empty security-descriptor block: a total length and an entry
  /// count, both of which an image without descriptors still has to carry.
  /// </summary>
  public const int EmptySecurityDataSize = 8;

  /// <summary>Length of the SHA-1 hash identifying a resource.</summary>
  public const int HashLength = 20;

  /// <summary>Directory-entry attribute bit marking a directory.</summary>
  public const uint AttributeDirectory = 0x00000010u;

  /// <summary>Directory-entry attribute bit marking an ordinary file.</summary>
  public const uint AttributeArchive = 0x00000020u;

  /// <summary>
  /// The security-descriptor index meaning "none". Written as -1 rather than 0,
  /// which would name the first descriptor of a table we do not write.
  /// </summary>
  public const int NoSecurityDescriptor = -1;

  // -------------------------------------------------------------------------
  // LZX parameters
  // -------------------------------------------------------------------------

  /// <summary>
  /// Default LZX window size exponent used by WIM (window = 2^15 = 32 768 bytes).
  /// </summary>
  public const int LzxWindowBits = 15;
}
