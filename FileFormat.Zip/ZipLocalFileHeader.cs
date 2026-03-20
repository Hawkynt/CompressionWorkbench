using System.Text;

namespace FileFormat.Zip;

/// <summary>
/// Reads and writes ZIP local file headers.
/// </summary>
internal static class ZipLocalFileHeader {
  /// <summary>
  /// Reads a local file header from the reader.
  /// </summary>
  public static ZipEntry Read(BinaryReader reader) {
    var sig = reader.ReadUInt32();
    if (sig != ZipConstants.LocalFileHeaderSignature)
      throw new InvalidDataException($"Invalid local file header signature: 0x{sig:X8}");

    var versionNeeded = reader.ReadUInt16();
    var flags = reader.ReadUInt16();
    var method = reader.ReadUInt16();
    var lastModTime = reader.ReadUInt16();
    var lastModDate = reader.ReadUInt16();
    var crc32 = reader.ReadUInt32();
    var compressedSize = reader.ReadUInt32();
    var uncompressedSize = reader.ReadUInt32();
    var fileNameLen = reader.ReadUInt16();
    var extraLen = reader.ReadUInt16();

    Encoding encoding = (flags & ZipConstants.FlagUtf8) != 0 ? Encoding.UTF8 : Encoding.Latin1;
    string fileName = encoding.GetString(reader.ReadBytes(fileNameLen));
    byte[]? extra = extraLen > 0 ? reader.ReadBytes(extraLen) : null;

    var entry = new ZipEntry {
      FileName = fileName,
      CompressionMethod = (ZipCompressionMethod)method,
      Crc32 = crc32,
      CompressedSize = compressedSize,
      UncompressedSize = uncompressedSize,
      LastModified = ZipEntry.FromMsDosDateTime(lastModDate, lastModTime),
      ExtraField = extra
    };

    // Handle ZIP64 extra field
    if (extra != null && (compressedSize == ZipConstants.Zip64Sentinel32 || uncompressedSize == ZipConstants.Zip64Sentinel32))
      ReadZip64ExtraField(entry, extra);

    return entry;
  }

  /// <summary>
  /// Writes a local file header.
  /// </summary>
  public static void Write(BinaryWriter writer, ZipEntry entry, bool encrypted = false) {
    var (date, time) = ZipEntry.ToMsDosDateTime(entry.LastModified);
    byte[] fileNameBytes = Encoding.UTF8.GetBytes(entry.FileName);
    ushort flags = ZipConstants.FlagUtf8;
    if (encrypted)
      flags |= ZipConstants.FlagEncrypted;
    // Implode: set bits 1 (8K dict) and 2 (literal tree) when method is Implode
    if (entry.CompressionMethod == ZipCompressionMethod.Implode)
      flags |= (ushort)(entry.GeneralPurposeFlags & 0x0006);

    // Combine ZIP64 and entry extra fields
    byte[]? zip64Extra = null;
    uint compSize = (uint)Math.Min(entry.CompressedSize, uint.MaxValue);
    uint uncompSize = (uint)Math.Min(entry.UncompressedSize, uint.MaxValue);

    if (entry.IsZip64) {
      compSize = ZipConstants.Zip64Sentinel32;
      uncompSize = ZipConstants.Zip64Sentinel32;
      zip64Extra = BuildZip64ExtraField(entry.UncompressedSize, entry.CompressedSize);
    }

    // Merge all extra field data
    int totalExtraLen = (zip64Extra?.Length ?? 0) + (entry.ExtraField?.Length ?? 0);
    byte[]? combinedExtra = null;
    if (totalExtraLen > 0) {
      combinedExtra = new byte[totalExtraLen];
      int pos = 0;
      if (zip64Extra != null) {
        zip64Extra.CopyTo(combinedExtra, pos);
        pos += zip64Extra.Length;
      }
      if (entry.ExtraField != null)
        entry.ExtraField.CopyTo(combinedExtra, pos);
    }

    ushort versionNeeded = entry.IsZip64 ? ZipConstants.VersionNeeded45
      : entry.CompressionMethod == ZipCompressionMethod.WinZipAes ? ZipConstants.VersionNeeded51
      : ZipConstants.VersionNeeded20;

    writer.Write(ZipConstants.LocalFileHeaderSignature);
    writer.Write(versionNeeded);
    writer.Write(flags);
    writer.Write((ushort)entry.CompressionMethod);
    writer.Write(time);
    writer.Write(date);
    writer.Write(entry.Crc32);
    writer.Write(compSize);
    writer.Write(uncompSize);
    writer.Write((ushort)fileNameBytes.Length);
    writer.Write((ushort)(combinedExtra?.Length ?? 0));
    writer.Write(fileNameBytes);
    if (combinedExtra != null)
      writer.Write(combinedExtra);
  }

  private static void ReadZip64ExtraField(ZipEntry entry, byte[] extra) {
    var pos = 0;
    while (pos + 4 <= extra.Length) {
      ushort tag = BitConverter.ToUInt16(extra, pos);
      ushort size = BitConverter.ToUInt16(extra, pos + 2);
      pos += 4;

      if (tag == ZipConstants.Zip64ExtraFieldTag && pos + size <= extra.Length) {
        int fieldPos = pos;
        if (entry.UncompressedSize == ZipConstants.Zip64Sentinel32 && fieldPos + 8 <= pos + size) {
          entry.UncompressedSize = BitConverter.ToInt64(extra, fieldPos);
          fieldPos += 8;
        }
        if (entry.CompressedSize == ZipConstants.Zip64Sentinel32 && fieldPos + 8 <= pos + size)
          entry.CompressedSize = BitConverter.ToInt64(extra, fieldPos);
        return;
      }
      pos += size;
    }
  }

  private static byte[] BuildZip64ExtraField(long uncompressedSize, long compressedSize) {
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);
    writer.Write(ZipConstants.Zip64ExtraFieldTag);
    writer.Write((ushort)16); // size of data
    writer.Write(uncompressedSize);
    writer.Write(compressedSize);
    return ms.ToArray();
  }
}
