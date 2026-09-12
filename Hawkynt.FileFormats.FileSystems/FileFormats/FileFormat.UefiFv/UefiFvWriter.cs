#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.UefiFv;

/// <summary>Writes standalone PI firmware volumes containing ordinary FFS2/FFS3 files.</summary>
internal static class UefiFvWriter {
  internal const int HeaderLength = 72;
  internal const int FfsHeaderLength = 24;
  internal const int FfsHeader2Length = 32;
  internal const int Alignment = 8;
  internal const int DefaultReserve = 64 * 1024;
  internal const uint FvAttributes = 0x0004FEFF;
  internal static readonly Guid Ffs2Guid = UefiFvConstants.Ffs2Guid;
  internal static readonly Guid Ffs3Guid = UefiFvConstants.Ffs3Guid;

  internal readonly record struct FileIdentity(Guid Guid, byte Type);

  public static byte[] Build(IEnumerable<(string Name, byte[] Data)> inputs) {
    var files = inputs.Select(i => (Identity: IdentityFromName(i.Name), i.Data)).ToList();
    var useFfs3 = files.Any(f => RequiresLargeFile(f.Data.Length));
    var fileSystemGuid = useFfs3 ? Ffs3Guid : Ffs2Guid;
    var used = HeaderLength + files.Sum(f => Align8(checked(HeaderSize(f.Data.Length) + f.Data.Length)));
    var capacity = Align4K(checked(used + DefaultReserve));
    var image = new byte[capacity];
    image.AsSpan().Fill(0xFF);
    WriteVolumeHeader(image, fileSystemGuid);

    var position = HeaderLength;
    foreach (var file in files) {
      var encoded = BuildFfsFile(file.Identity.Guid, file.Identity.Type, file.Data, fileSystemGuid, erasePolarity: true);
      encoded.CopyTo(image, position);
      position += Align8(encoded.Length);
    }
    return image;
  }

  internal static FileIdentity IdentityFromName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var leaf = Path.GetFileName(name);
    if (leaf.Length >= 36 && Guid.TryParse(leaf.AsSpan(0, 36), out var guid)) {
      var type = ParseTypeTag(leaf.Length > 37 ? leaf[37..] : "");
      return new FileIdentity(guid, type);
    }

    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Replace('\\', '/')));
    return new FileIdentity(new Guid(hash.AsSpan(0, 16)), 0x01);
  }

  internal static byte[] BuildFfsFile(Guid guid, byte type, ReadOnlySpan<byte> contents)
    => BuildFfsFile(guid, type, contents, Ffs2Guid, erasePolarity: true);

  internal static byte[] BuildFfsFile(Guid guid, byte type, ReadOnlySpan<byte> contents,
    Guid fileSystemGuid, bool erasePolarity) {
    var large = RequiresLargeFile(contents.Length);
    if (large && fileSystemGuid != Ffs3Guid)
      throw new NotSupportedException("FFS files larger than 16 MiB require an EFI_FIRMWARE_FILE_SYSTEM3_GUID volume.");

    var headerSize = large ? FfsHeader2Length : FfsHeaderLength;
    var size = checked(headerSize + contents.Length);
    var file = new byte[size];
    guid.TryWriteBytes(file.AsSpan(0, 16));
    file[17] = 0xAA;
    file[18] = type;
    file[19] = large ? UefiFvConstants.LargeFile : (byte)0;
    if (large)
      BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), (ulong)size);
    else {
      file[20] = (byte)size;
      file[21] = (byte)(size >> 8);
      file[22] = (byte)(size >> 16);
    }
    file[23] = UefiFvConstants.EncodeState(
      UefiFvConstants.HeaderConstruction | UefiFvConstants.HeaderValid | UefiFvConstants.DataValid,
      erasePolarity);
    contents.CopyTo(file.AsSpan(headerSize));
    file[16] = HeaderChecksum(file.AsSpan(0, headerSize));
    return file;
  }

  internal static void WriteVolumeHeader(Span<byte> image, Guid? fileSystemGuid = null) {
    if (image.Length < HeaderLength) throw new ArgumentException("FV buffer is too small.", nameof(image));
    image[..HeaderLength].Clear();
    (fileSystemGuid ?? Ffs2Guid).TryWriteBytes(image.Slice(16, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(image[32..], (ulong)image.Length);
    "_FVH"u8.CopyTo(image[40..]);
    BinaryPrimitives.WriteUInt32LittleEndian(image[44..], FvAttributes);
    BinaryPrimitives.WriteUInt16LittleEndian(image[48..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(image[52..], 0);
    image[54] = 0;
    image[55] = 2;
    BinaryPrimitives.WriteUInt32LittleEndian(image[56..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image[60..], (uint)image.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image[64..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image[68..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image[50..], VolumeHeaderChecksum(image[..HeaderLength]));
  }

  private static byte HeaderChecksum(ReadOnlySpan<byte> header) {
    var sum = 0;
    for (var i = 0; i < header.Length; i++) {
      if (i is 16 or 17 or 23) continue;
      sum = (sum + header[i]) & 0xFF;
    }
    return unchecked((byte)(0 - sum));
  }

  private static ushort VolumeHeaderChecksum(ReadOnlySpan<byte> header) {
    uint sum = 0;
    for (var i = 0; i + 1 < header.Length; i += 2)
      sum += BinaryPrimitives.ReadUInt16LittleEndian(header[i..]);
    return unchecked((ushort)(0 - sum));
  }

  internal static int Align8(int value) => UefiFvConstants.Align8(value);
  private static int Align4K(int value) => checked((value + 4095) & ~4095);
  private static bool RequiresLargeFile(int dataLength) => (long)FfsHeaderLength + dataLength > 0xFFFFFF;
  private static int HeaderSize(int dataLength) => RequiresLargeFile(dataLength) ? FfsHeader2Length : FfsHeaderLength;

  internal static string EntryName(Guid guid, byte type)
    => $"{guid:D}_{UefiFvReader.ShortTypeTag(type)}.bin";

  private static byte ParseTypeTag(string tail) {
    var tag = Path.GetFileNameWithoutExtension(tail).TrimStart('_').ToUpperInvariant();
    return tag switch {
      "RAW" => 0x01, "FREEFORM" => 0x02, "SECURITY_CORE" => 0x03, "PEI_CORE" => 0x04,
      "DXE_CORE" => 0x05, "PEIM" => 0x06, "DRIVER" => 0x07, "COMBINED_PEIM_DRIVER" => 0x08,
      "APPLICATION" => 0x09, "MM" => 0x0A, "FIRMWARE_VOLUME_IMAGE" => 0x0B,
      "COMBINED_MM_DXE" => 0x0C, "MM_CORE" => 0x0D, "MM_STANDALONE" => 0x0E,
      "MM_CORE_STANDALONE" => 0x0F, _ => 0x01,
    };
  }
}
