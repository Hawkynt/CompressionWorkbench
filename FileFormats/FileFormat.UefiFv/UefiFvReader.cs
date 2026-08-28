#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.UefiFv;

/// <summary>
/// Reader for UEFI Platform Initialization (PI) Firmware Volumes. Locates the
/// FV header by scanning for the <c>_FVH</c> signature at offset 40 from the
/// start of each 16-byte-aligned candidate (UEFI PI Volume 3). Walks the FFS
/// file list and returns one <see cref="FfsFile"/> record per live file.
/// </summary>
public sealed class UefiFvReader {
  public static readonly byte[] Signature = [(byte)'_', (byte)'F', (byte)'V', (byte)'H'];
  public const int SignatureOffset = 40;

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

  public sealed record FfsFile(
    Guid Name,
    byte Type,
    byte Attributes,
    byte State,
    uint Size,
    byte[] Contents
  );

  public sealed record FirmwareVolume(
    int StartOffset,
    FvHeader Header,
    IReadOnlyList<FfsFile> Files
  );

  public static FirmwareVolume Read(ReadOnlySpan<byte> data, int fvStart = 0) {
    if (data.Length < fvStart + 56)
      throw new InvalidDataException("UefiFv: file shorter than minimum FV header.");

    var sigSpan = data.Slice(fvStart + SignatureOffset, 4);
    if (!sigSpan.SequenceEqual(Signature))
      throw new InvalidDataException(
        $"UefiFv: '_FVH' signature not found at offset {fvStart + SignatureOffset}.");

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
    var ffsStart = fvStart + headerLength;
    var ffsEnd = checked((int)Math.Min((long)data.Length, fvStart + (long)fvLength));
    var files = ReadFfsFiles(data, ffsStart, ffsEnd);
    return new FirmwareVolume(fvStart, header, files);
  }

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
      var header = data.Slice(pos, 24);
      if (IsErased(header)) {
        // Free/deleted regions may occur between live files after offline
        // mutation. Advance one alignment quantum until the next header.
        pos += 8;
        continue;
      }

      var name = new Guid(header[..16]);
      var type = header[18];
      var attrs = header[19];
      var size = (uint)(header[20] | (header[21] << 8) | (header[22] << 16));
      var state = header[23];
      if (size < 24 || pos + (long)size > end) break;

      var contents = data.Slice(pos + 24, checked((int)size - 24)).ToArray();
      files.Add(new FfsFile(name, type, attrs, state, size, contents));
      pos = Align8(pos + checked((int)size));
    }
    return files;

    static bool IsErased(ReadOnlySpan<byte> bytes) {
      foreach (var b in bytes)
        if (b != 0xFF) return false;
      return true;
    }

    static int Align8(int v) => (v + 7) & ~7;
  }

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

  public static string ShortTypeTag(byte t) {
    var n = FileTypeName(t);
    const string prefix = "EFI_FV_FILETYPE_";
    return n.StartsWith(prefix, StringComparison.Ordinal) ? n[prefix.Length..] : n;
  }
}
