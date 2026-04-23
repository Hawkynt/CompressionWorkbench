#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.UefiFv;

/// <summary>
/// Reader for UEFI Platform Initialization (PI) Firmware Volumes. Locates the
/// FV header by scanning for the <c>_FVH</c> signature at offset 40 from the
/// start of each 16-byte-aligned candidate (UEFI PI Volume 3). Walks the FFS
/// file list and returns one <see cref="FfsFile"/> record per file.
/// </summary>
/// <remarks>
/// The FV header layout per UEFI PI Spec Vol. 3, §3.2:
/// <code>
///   ZeroVector         16 bytes
///   FileSystemGuid     16 bytes (EFI_FIRMWARE_FILE_SYSTEM2/3_GUID)
///   FvLength            8 bytes (u64 LE)
///   Signature           4 bytes ("_FVH")
///   Attributes          4 bytes (u32 LE)
///   HeaderLength        2 bytes (u16 LE)
///   Checksum            2 bytes (u16 LE)
///   ExtHeaderOffset     2 bytes (u16 LE)  — v2 only
///   Reserved            1 byte
///   Revision            1 byte
///   BlockMap[] { u32 NumBlocks, u32 Length } terminated by {0,0}
/// </code>
/// </remarks>
public sealed class UefiFvReader {

  /// <summary>FV signature bytes (<c>_FVH</c>) at FV offset 40.</summary>
  public static readonly byte[] Signature = [(byte)'_', (byte)'F', (byte)'V', (byte)'H'];

  /// <summary>Signature offset from FV start.</summary>
  public const int SignatureOffset = 40;

  /// <summary>FV header (excluding block map + extended header body).</summary>
  public sealed record FvHeader(
    Guid FileSystemGuid,
    ulong FvLength,
    uint Attributes,
    ushort HeaderLength,
    ushort Checksum,
    ushort ExtHeaderOffset,
    byte Revision,
    IReadOnlyList<(uint NumBlocks, uint Length)> BlockMap
  );

  /// <summary>A single FFS (Firmware File System) file inside the FV.</summary>
  /// <param name="Name">File GUID (<c>EFI_FFS_FILE_HEADER.Name</c>).</param>
  /// <param name="Type">Raw FFS type byte (see <see cref="FileTypeName"/>).</param>
  /// <param name="Attributes">FFS file attributes byte.</param>
  /// <param name="State">FFS file state byte.</param>
  /// <param name="Size">Declared file size including the 24-byte header.</param>
  /// <param name="Contents">File contents (size minus the 24-byte header).</param>
  public sealed record FfsFile(
    Guid Name,
    byte Type,
    byte Attributes,
    byte State,
    uint Size,
    byte[] Contents
  );

  /// <summary>Parsed firmware volume.</summary>
  public sealed record FirmwareVolume(
    int StartOffset,
    FvHeader Header,
    IReadOnlyList<FfsFile> Files
  );

  /// <summary>Parses a firmware volume located at the given file offset.</summary>
  public static FirmwareVolume Read(ReadOnlySpan<byte> data, int fvStart = 0) {
    if (data.Length < fvStart + 56)
      throw new InvalidDataException("UefiFv: file shorter than minimum FV header.");

    var sigSpan = data.Slice(fvStart + SignatureOffset, 4);
    if (!sigSpan.SequenceEqual(Signature))
      throw new InvalidDataException(
        $"UefiFv: '_FVH' signature not found at offset {fvStart + SignatureOffset}.");

    // GUID is stored in EFI format: Data1/Data2/Data3 LE + Data4 BE (.NET's
    // little-endian constructor matches).
    var fsGuid = new Guid(data.Slice(fvStart + 16, 16));
    var fvLength = BinaryPrimitives.ReadUInt64LittleEndian(data[(fvStart + 32)..]);
    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[(fvStart + 44)..]);
    var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 48)..]);
    var checksum = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 50)..]);
    var extOff = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 52)..]);
    var revision = data[fvStart + 55];

    var blockMap = new List<(uint, uint)>();
    var p = fvStart + 56;
    while (p + 8 <= data.Length) {
      var nb = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
      var bl = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
      p += 8;
      if (nb == 0 && bl == 0) break;
      blockMap.Add((nb, bl));
    }

    var header = new FvHeader(fsGuid, fvLength, attributes, headerLength, checksum, extOff, revision, blockMap);

    // FFS files begin at the end of the FV header, 8-byte aligned. fvLength bounds the FV payload.
    var ffsStart = fvStart + headerLength;
    var ffsEnd = (int)Math.Min((long)data.Length, fvStart + (long)fvLength);
    var files = ReadFfsFiles(data, ffsStart, ffsEnd);

    return new FirmwareVolume(fvStart, header, files);
  }

  /// <summary>Scans <paramref name="data"/> for the first <c>_FVH</c> signature and returns the FV start.</summary>
  public static int? FindFirst(ReadOnlySpan<byte> data) {
    for (var i = 0; i + SignatureOffset + 4 <= data.Length; i += 16) {
      if (data.Slice(i + SignatureOffset, 4).SequenceEqual(Signature))
        return i;
    }
    return null;
  }

  private static List<FfsFile> ReadFfsFiles(ReadOnlySpan<byte> data, int start, int end) {
    var files = new List<FfsFile>();
    var pos = Align8(start);
    while (pos + 24 <= end) {
      var name = new Guid(data.Slice(pos, 16));
      var type = data[pos + 18];
      var attrs = data[pos + 19];
      var size = (uint)(data[pos + 20] | (data[pos + 21] << 8) | (data[pos + 22] << 16));
      var state = data[pos + 23];

      // An all-0xFF header region marks the end of the file list (unallocated space).
      if (type == 0xFF && size == 0xFFFFFFu) break;
      if (size < 24 || pos + (int)size > end) break;

      var contents = data.Slice(pos + 24, (int)size - 24).ToArray();
      files.Add(new FfsFile(name, type, attrs, state, size, contents));
      pos = Align8(pos + (int)size);
    }
    return files;

    static int Align8(int v) => (v + 7) & ~7;
  }

  /// <summary>Decodes the FFS type byte to the UEFI PI spec name.</summary>
  public static string FileTypeName(byte t) => t switch {
    0x00 => "EFI_FV_FILETYPE_ALL",
    0x01 => "EFI_FV_FILETYPE_RAW",
    0x02 => "EFI_FV_FILETYPE_FREEFORM",
    0x03 => "EFI_FV_FILETYPE_SECURITY_CORE",
    0x04 => "EFI_FV_FILETYPE_PEI_CORE",
    0x05 => "EFI_FV_FILETYPE_DXE_CORE",
    0x06 => "EFI_FV_FILETYPE_PEIM",
    0x07 => "EFI_FV_FILETYPE_DRIVER",
    0x08 => "EFI_FV_FILETYPE_COMBINED_PEIM_DRIVER",
    0x09 => "EFI_FV_FILETYPE_APPLICATION",
    0x0A => "EFI_FV_FILETYPE_MM",
    0x0B => "EFI_FV_FILETYPE_FIRMWARE_VOLUME_IMAGE",
    0x0C => "EFI_FV_FILETYPE_COMBINED_MM_DXE",
    0x0D => "EFI_FV_FILETYPE_MM_CORE",
    0x0E => "EFI_FV_FILETYPE_MM_STANDALONE",
    0x0F => "EFI_FV_FILETYPE_MM_CORE_STANDALONE",
    0xF0 => "EFI_FV_FILETYPE_FFS_PAD",
    _ => $"EFI_FV_FILETYPE_UNKNOWN_0x{t:X2}",
  };

  /// <summary>Returns a short type tag for use in entry names (e.g. <c>RAW</c>, <c>DRIVER</c>).</summary>
  public static string ShortTypeTag(byte t) {
    var n = FileTypeName(t);
    const string prefix = "EFI_FV_FILETYPE_";
    return n.StartsWith(prefix, StringComparison.Ordinal) ? n[prefix.Length..] : n;
  }
}
