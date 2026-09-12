#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// Parsed OpenVMS Files-11 (ODS-2 / ODS-5) home block. The home block is found
/// at logical block 1 (offset 512 from the start of the volume) and is anchored
/// by the ASCII volume label "DECFILE11A " (ODS-2) or "DECFILE11B " (ODS-5)
/// at offset 488 inside the 512-byte home block.
///
/// Field layout (selected — per "Files-11 On-Disk Structure" / VMS I/O Reference):
///   0x000 (HM2$L_HOMELBN)   u32 LE — home block LBN (often 1)
///   0x004 (HM2$L_ALHOMELBN) u32 LE — alternate home LBN
///   0x008 (HM2$L_ALTIDXLBN) u32 LE — alternate index LBN
///   0x00C (HM2$W_STRUCLEV)  u16 LE — structure level (0x0202 = ODS-2, 0x0205 = ODS-5)
///   0x00E (HM2$W_CLUSTER)   u16 LE — cluster size in blocks
///   0x010 (HM2$W_HOMEVBN)   u16 LE — home virtual block number
///   0x028 (HM2$L_IBMAPLBN)  u32 LE — index file bitmap LBN
///   0x02C (HM2$L_MAXFILES)  u32 LE — maximum number of files
///   0x030 (HM2$W_IBMAPSIZE) u16 LE — index file bitmap size
///   0x036 (HM2$L_OWNUIC)    u32 LE — owner UIC (group:member)
///   0x1D8 ("DECFILE11x ")   12-byte ASCII format string
///   0x1F0 (HM2$T_VOLNAME)   12-byte ASCII volume label
/// </summary>
internal sealed class OpenVmsHomeBlock {
  // The format string field is 12 ASCII bytes wide on disk, padded with space or NUL
  // depending on producer. We only compare the 11-byte stable prefix "DECFILE11A " /
  // "DECFILE11B " (10 chars + trailing space) to stay robust to either padding.
  public static readonly byte[] Ods2FormatString = "DECFILE11A "u8.ToArray();
  public static readonly byte[] Ods5FormatString = "DECFILE11B "u8.ToArray();
  // Format string lives at offset 488 (0x1E8) inside the home block per the VMS spec.
  // We don't enforce the exact offset because some images have shifted home blocks;
  // we scan the candidate window.
  public const int FormatStringOffsetInHomeBlock = 0x1E8;
  public const int VolumeNameOffsetInHomeBlock = 0x1F4;

  public bool Valid { get; init; }
  public int HomeBlockOffset { get; init; }
  public string FormatString { get; init; } = "";
  public string VolumeLabel { get; init; } = "";
  public ushort StructureLevel { get; init; }
  public string StructureName { get; init; } = "";
  public ushort ClusterSize { get; init; }
  public uint MaxFiles { get; init; }
  public uint OwnerUic { get; init; }
  public uint IndexBitmapLbn { get; init; }
  public byte[] RawBytes { get; init; } = [];

  /// <summary>
  /// ODS-2/5 home blocks live at LBN 1 (offset 512) on a properly-formed image,
  /// but some on-disk dumps shift them by a partition map. We probe the canonical
  /// offsets (512, 1024, 0) and accept the first one whose format string matches.
  /// </summary>
  public static OpenVmsHomeBlock TryParse(ReadOnlySpan<byte> image) {
    foreach (var offset in (int[])[512, 1024, 2048, 0]) {
      if (offset + 512 > image.Length) continue;
      var fmtStart = offset + FormatStringOffsetInHomeBlock;
      if (fmtStart + Ods2FormatString.Length > image.Length) continue;
      var fmt = image.Slice(fmtStart, Ods2FormatString.Length);
      var isOds2 = fmt.SequenceEqual(Ods2FormatString.AsSpan());
      var isOds5 = fmt.SequenceEqual(Ods5FormatString.AsSpan());
      if (!isOds2 && !isOds5) continue;
      return Parse(image, offset, isOds5);
    }
    return new OpenVmsHomeBlock();
  }

  private static OpenVmsHomeBlock Parse(ReadOnlySpan<byte> image, int offset, bool isOds5) {
    var raw = image.Slice(offset, Math.Min(512, image.Length - offset)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    // The on-disk format-string field is 12 bytes; we read the full 12 for display.
    var fmtFieldEnd = Math.Min(offset + FormatStringOffsetInHomeBlock + 12, image.Length);
    var fmtFieldStart = offset + FormatStringOffsetInHomeBlock;
    var fmt = Encoding.ASCII.GetString(image.Slice(fmtFieldStart, fmtFieldEnd - fmtFieldStart)).TrimEnd('\0', ' ');
    var labelFieldEnd = Math.Min(offset + VolumeNameOffsetInHomeBlock + 12, image.Length);
    var labelFieldStart = offset + VolumeNameOffsetInHomeBlock;
    var label = Encoding.ASCII.GetString(image.Slice(labelFieldStart, labelFieldEnd - labelFieldStart)).TrimEnd('\0', ' ');
    var struc = ReadU16(image, offset + 0x00C);
    var cluster = ReadU16(image, offset + 0x00E);
    var idxBmp = ReadU32(image, offset + 0x028);
    var maxFiles = ReadU32(image, offset + 0x02C);
    var ownerUic = ReadU32(image, offset + 0x036);

    return new OpenVmsHomeBlock {
      Valid = true,
      HomeBlockOffset = offset,
      FormatString = fmt,
      VolumeLabel = label,
      StructureLevel = struc,
      StructureName = isOds5 ? "ODS-5" : "ODS-2",
      ClusterSize = cluster,
      MaxFiles = maxFiles,
      OwnerUic = ownerUic,
      IndexBitmapLbn = idxBmp,
      RawBytes = raw,
    };
  }

  private static ushort ReadU16(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(off, 2)) : (ushort)0;

  private static uint ReadU32(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4)) : 0u;
}
