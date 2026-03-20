using System.Text;

namespace FileFormat.Zip;

/// <summary>
/// Reads and writes ZIP central directory entries.
/// </summary>
internal static class ZipCentralDirectoryEntry {
  /// <summary>
  /// Reads a central directory entry.
  /// </summary>
  public static ZipEntry Read(BinaryReader reader) {
    var sig = reader.ReadUInt32();
    if (sig != ZipConstants.CentralDirectorySignature)
      throw new InvalidDataException($"Invalid central directory signature: 0x{sig:X8}");

    var versionMadeBy = reader.ReadUInt16();
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
    var commentLen = reader.ReadUInt16();
    var diskStart = reader.ReadUInt16();
    var internalAttrs = reader.ReadUInt16();
    var externalAttrs = reader.ReadUInt32();
    var localHeaderOffset = reader.ReadUInt32();

    Encoding encoding = (flags & ZipConstants.FlagUtf8) != 0 ? Encoding.UTF8 : Encoding.Latin1;
    string fileName = encoding.GetString(reader.ReadBytes(fileNameLen));
    byte[]? extra = extraLen > 0 ? reader.ReadBytes(extraLen) : null;
    string? comment = commentLen > 0 ? encoding.GetString(reader.ReadBytes(commentLen)) : null;

    var entry = new ZipEntry {
      FileName = fileName,
      CompressionMethod = (ZipCompressionMethod)method,
      Crc32 = crc32,
      CompressedSize = compressedSize,
      UncompressedSize = uncompressedSize,
      LastModified = ZipEntry.FromMsDosDateTime(lastModDate, lastModTime),
      ExtraField = extra,
      Comment = comment,
      ExternalAttributes = externalAttrs,
      LocalHeaderOffset = localHeaderOffset,
      IsEncrypted = (flags & ZipConstants.FlagEncrypted) != 0,
      GeneralPurposeFlags = flags,
    };

    // Handle ZIP64 extra field
    if (extra != null)
      ReadZip64ExtraField(entry, extra, compressedSize, uncompressedSize, localHeaderOffset);

    return entry;
  }

  /// <summary>
  /// Writes a central directory entry.
  /// </summary>
  public static void Write(BinaryWriter writer, ZipEntry entry) {
    var (date, time) = ZipEntry.ToMsDosDateTime(entry.LastModified);
    byte[] fileNameBytes = Encoding.UTF8.GetBytes(entry.FileName);
    byte[]? commentBytes = entry.Comment != null ? Encoding.UTF8.GetBytes(entry.Comment) : null;
    ushort flags = ZipConstants.FlagUtf8;

    // Set encrypted flag
    if (entry.IsEncrypted)
      flags |= ZipConstants.FlagEncrypted;
    // Implode: preserve bits 1 (8K dict) and 2 (literal tree)
    if (entry.CompressionMethod == ZipCompressionMethod.Implode)
      flags |= (ushort)(entry.GeneralPurposeFlags & 0x0006);

    byte[]? zip64Extra = null;
    uint compSize = (uint)Math.Min(entry.CompressedSize, uint.MaxValue);
    uint uncompSize = (uint)Math.Min(entry.UncompressedSize, uint.MaxValue);
    uint localOffset = (uint)Math.Min(entry.LocalHeaderOffset, uint.MaxValue);

    if (entry.IsZip64) {
      compSize = ZipConstants.Zip64Sentinel32;
      uncompSize = ZipConstants.Zip64Sentinel32;
      localOffset = ZipConstants.Zip64Sentinel32;
      zip64Extra = BuildZip64ExtraField(entry.UncompressedSize, entry.CompressedSize, entry.LocalHeaderOffset);
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

    ushort commentLen = (ushort)(commentBytes?.Length ?? 0);

    ushort versionNeeded = entry.IsZip64 ? ZipConstants.VersionNeeded45
      : entry.CompressionMethod == ZipCompressionMethod.WinZipAes ? ZipConstants.VersionNeeded51
      : ZipConstants.VersionNeeded20;

    writer.Write(ZipConstants.CentralDirectorySignature);
    writer.Write(ZipConstants.VersionMadeBy20);
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
    writer.Write(commentLen);
    writer.Write((ushort)0); // disk number start
    writer.Write((ushort)0); // internal attributes
    writer.Write(entry.ExternalAttributes);
    writer.Write(localOffset);
    writer.Write(fileNameBytes);
    if (combinedExtra != null)
      writer.Write(combinedExtra);
    if (commentBytes != null)
      writer.Write(commentBytes);
  }

  private static void ReadZip64ExtraField(ZipEntry entry, byte[] extra, uint compSize32, uint uncompSize32, uint offset32) {
    var pos = 0;
    while (pos + 4 <= extra.Length) {
      ushort tag = BitConverter.ToUInt16(extra, pos);
      ushort size = BitConverter.ToUInt16(extra, pos + 2);
      pos += 4;

      if (tag == ZipConstants.Zip64ExtraFieldTag && pos + size <= extra.Length) {
        int fieldPos = pos;
        if (uncompSize32 == ZipConstants.Zip64Sentinel32 && fieldPos + 8 <= pos + size) {
          entry.UncompressedSize = BitConverter.ToInt64(extra, fieldPos);
          fieldPos += 8;
        }
        if (compSize32 == ZipConstants.Zip64Sentinel32 && fieldPos + 8 <= pos + size) {
          entry.CompressedSize = BitConverter.ToInt64(extra, fieldPos);
          fieldPos += 8;
        }
        if (offset32 == ZipConstants.Zip64Sentinel32 && fieldPos + 8 <= pos + size)
          entry.LocalHeaderOffset = BitConverter.ToInt64(extra, fieldPos);
        return;
      }
      pos += size;
    }
  }

  private static byte[] BuildZip64ExtraField(long uncompressedSize, long compressedSize, long localHeaderOffset) {
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);
    writer.Write(ZipConstants.Zip64ExtraFieldTag);
    writer.Write((ushort)24); // size of data
    writer.Write(uncompressedSize);
    writer.Write(compressedSize);
    writer.Write(localHeaderOffset);
    return ms.ToArray();
  }
}
