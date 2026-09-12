#pragma warning disable CS1591
namespace FileSystem.Rt11;

/// <summary>
/// Geometry of the RT-11 reference image used by this writer — a single-density
/// 8" floppy in DEC's original RX01 format: 77 tracks × 26 sectors × 128 bytes
/// = 256 256 bytes. The layout is documented in DEC's "RT-11 Volume and File
/// Formats Manual" (AA-PD6PA-TC). On-disk values are PDP-11 little-endian.
/// </summary>
internal static class Rt11Layout {
  // Block size always 512 bytes regardless of physical sector size; RT-11
  // virtualises 256 128-byte sectors into 64064 bytes of usable storage.
  public const int BlockSize = 512;

  // RX01 reference geometry.
  public const int Tracks = 77;
  public const int SectorsPerTrack = 26;
  public const int SectorSize = 128;
  public const int TotalBytes = Tracks * SectorsPerTrack * SectorSize; // 256 256
  public const int TotalBlocks = TotalBytes / BlockSize;               // 500.5 → see below

  // Round image to whole blocks (RT-11's volume image is always block-aligned).
  // 256 256 / 512 = 500.5 → bump to 501 blocks (256 512 bytes). Real RX01 disks
  // use 256 256 bytes; tools tolerate the 256-byte tail and our reader doesn't
  // care because it works off explicit block addresses.
  public const int ImageBlocks = (TotalBytes + BlockSize - 1) / BlockSize; // 501
  public const int ImageBytes = ImageBlocks * BlockSize;                   // 256 512

  // Reserved blocks.
  public const int BootBlock = 0;          // not used by RT-11 itself
  public const int HomeBlock = 1;          // pack header
  public const int FirstDirSegment = 6;    // dir starts at block 6 (canonical)

  // Home block layout.
  public const int HomeBlockSignatureOffset = 0x1F0;
  public const string HomeBlockSignature = "DECRT11A    "; // exactly 12 chars, space-padded

  // Directory segments are 2 blocks each (1024 bytes).
  public const int DirSegmentBlocks = 2;
  public const int DirSegmentBytes = DirSegmentBlocks * BlockSize; // 1024
  public const int DirSegmentHeaderBytes = 10;                     // 5 PDP-11 words
  public const int DirEntryBytes = 14;                             // 7 words when extra=0

  // Status word bits (DEC manual definitions, decimal). E_EOS terminates a
  // directory segment.
  public const ushort E_PRE = 0x0001;   // permanent (some sources)
  public const ushort E_TENT = 0x0002;  // tentative file
  public const ushort E_MPTY = 0x0004;  // empty / unused slot
  public const ushort E_PERM = 0x0008;  // permanent valid file
  public const ushort E_EOS = 0x0010;   // end-of-segment marker (stop scanning)

  /// <summary>
  /// Default RT-11 directory entries per single 2-block segment, no extra bytes,
  /// terminator inclusive: (1024 - 10) / 14 = 72 entries; we use 72 but reserve
  /// the last for the EOS marker, so 71 usable file slots per segment.
  /// </summary>
  public const int EntriesPerSegment = (DirSegmentBytes - DirSegmentHeaderBytes) / DirEntryBytes;
}
