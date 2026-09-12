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

    var encoding = (flags & ZipConstants.FlagUtf8) != 0 ? Encoding.UTF8 : Encoding.Latin1;
    var fileName = encoding.GetString(reader.ReadBytes(fileNameLen));
    var extra = extraLen > 0 ? reader.ReadBytes(extraLen) : null;
    var comment = commentLen > 0 ? encoding.GetString(reader.ReadBytes(commentLen)) : null;

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
    var fileNameBytes = Encoding.UTF8.GetBytes(entry.FileName);
    var commentBytes = entry.Comment != null ? Encoding.UTF8.GetBytes(entry.Comment) : null;
    var flags = ZipConstants.FlagUtf8;

    if (entry.IsEncrypted)
      flags |= ZipConstants.FlagEncrypted;
    if (entry.CompressionMethod == ZipCompressionMethod.Implode)
      flags |= (ushort)(entry.GeneralPurposeFlags & 0x0006);

    var needUncompressed64 = entry.UncompressedSize > uint.MaxValue;
    var needCompressed64 = entry.CompressedSize > uint.MaxValue;
    var needOffset64 = entry.LocalHeaderOffset > uint.MaxValue;

    var compSize = needCompressed64 ? ZipConstants.Zip64Sentinel32 : (uint)entry.CompressedSize;
    var uncompSize = needUncompressed64 ? ZipConstants.Zip64Sentinel32 : (uint)entry.UncompressedSize;
    var localOffset = needOffset64 ? ZipConstants.Zip64Sentinel32 : (uint)entry.LocalHeaderOffset;

    var zip64Extra = entry.IsZip64
      ? BuildZip64ExtraField(entry, needUncompressed64, needCompressed64, needOffset64)
      : null;
    var combinedExtra = MergeExtraFields(zip64Extra, entry.ExtraField);

    if (fileNameBytes.Length > ushort.MaxValue)
      throw new InvalidDataException("ZIP file name exceeds the 65535-byte header limit.");
    if ((commentBytes?.Length ?? 0) > ushort.MaxValue)
      throw new InvalidDataException("ZIP file comment exceeds the 65535-byte header limit.");

    var versionNeeded = ZipCompatibility.GetVersionNeeded(entry);

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
    writer.Write((ushort)(commentBytes?.Length ?? 0));
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
      var tag = BitConverter.ToUInt16(extra, pos);
      var size = BitConverter.ToUInt16(extra, pos + 2);
      pos += 4;

      if (tag == ZipConstants.Zip64ExtraFieldTag && pos + size <= extra.Length) {
        var fieldPos = pos;
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

  private static byte[] BuildZip64ExtraField(
      ZipEntry entry,
      bool includeUncompressedSize,
      bool includeCompressedSize,
      bool includeLocalHeaderOffset) {
    using var payload = new MemoryStream();
    using (var payloadWriter = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true)) {
      if (includeUncompressedSize)
        payloadWriter.Write(entry.UncompressedSize);
      if (includeCompressedSize)
        payloadWriter.Write(entry.CompressedSize);
      if (includeLocalHeaderOffset)
        payloadWriter.Write(entry.LocalHeaderOffset);
    }

    using var result = new MemoryStream();
    using var writer = new BinaryWriter(result);
    writer.Write(ZipConstants.Zip64ExtraFieldTag);
    writer.Write((ushort)payload.Length);
    writer.Write(payload.GetBuffer(), 0, (int)payload.Length);
    return result.ToArray();
  }

  private static byte[]? MergeExtraFields(byte[]? first, byte[]? second) {
    var totalLength = (first?.Length ?? 0) + (second?.Length ?? 0);
    if (totalLength == 0)
      return null;
    if (totalLength > ushort.MaxValue)
      throw new InvalidDataException("ZIP extra fields exceed the 65535-byte header limit.");

    var result = new byte[totalLength];
    var position = 0;
    if (first != null) {
      first.CopyTo(result, position);
      position += first.Length;
    }
    if (second != null)
      second.CopyTo(result, position);
    return result;
  }
}
