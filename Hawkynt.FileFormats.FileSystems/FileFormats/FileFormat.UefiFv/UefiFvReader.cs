#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.UefiFv;

/// <summary>Reader for UEFI PI firmware volumes and standard FFS2/FFS3 file records.</summary>
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
  ) {
    public bool ErasePolarity => (this.Attributes & UefiFvConstants.ErasePolarityMask) != 0;
    public byte EraseByte => this.ErasePolarity ? (byte)0xFF : (byte)0x00;
  }

  public sealed record FfsFile(Guid Name, byte Type, byte Attributes, byte State, uint Size, byte[] Contents);
  public sealed record FirmwareVolume(int StartOffset, FvHeader Header, IReadOnlyList<FfsFile> Files);

  public static FirmwareVolume Read(ReadOnlySpan<byte> data, int fvStart = 0) {
    var layout = UefiFvParser.Parse(data, fvStart);
    var fvLength = BinaryPrimitives.ReadUInt64LittleEndian(data[(fvStart + 32)..]);
    var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[(fvStart + 44)..]);
    var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 48)..]);
    var checksum = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 50)..]);
    var extOff = BinaryPrimitives.ReadUInt16LittleEndian(data[(fvStart + 52)..]);
    var revision = data[fvStart + 55];

    var blockMap = new List<(uint, uint)>();
    var headerEnd = checked(fvStart + headerLength);
    var terminated = false;
    for (var p = fvStart + 56; p + 8 <= headerEnd; p += 8) {
      var blocks = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
      if (blocks == 0 && length == 0) { terminated = true; break; }
      blockMap.Add((blocks, length));
    }
    if (!terminated) throw new InvalidDataException("UefiFv: firmware-volume block map is not terminated inside HeaderLength.");

    // `data` is a ReadOnlySpan, which a lambda cannot capture, so the slices are materialised
    // here rather than inside the projection.
    var files = new List<FfsFile>();
    foreach (var slot in UefiFvParser.LiveSlots(layout))
      files.Add(new FfsFile(
        slot.Name, slot.Type, slot.Attributes, slot.RawState, checked((uint)slot.Size),
        data.Slice(slot.DataOffset, slot.DataLength).ToArray()));
    var header = new FvHeader(new Guid(data.Slice(fvStart + 16, 16)), fvLength, attributes,
      headerLength, checksum, extOff, revision, blockMap);
    return new FirmwareVolume(fvStart, header, files);
  }

  public static int? FindFirst(ReadOnlySpan<byte> data) {
    for (var i = 0; i + SignatureOffset + 4 <= data.Length; i += 16)
      if (data.Slice(i + SignatureOffset, 4).SequenceEqual(Signature)) return i;
    return null;
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
    var name = FileTypeName(t);
    const string prefix = "EFI_FV_FILETYPE_";
    return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
  }
}
