namespace FileFormat.Zip;

/// <summary>
/// Reads and writes ZIP end of central directory records.
/// </summary>
internal static class ZipEndOfCentralDirectory {
  /// <summary>
  /// Reads the end of central directory, returning the offset, size, and count.
  /// </summary>
  public static (long Offset, long Size, int Count, string? Comment) Read(Stream stream) {
    // Scan backwards for EOCD signature
    var eocdPos = FindEocd(stream);
    stream.Position = eocdPos;

    var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);
    var sig = reader.ReadUInt32();
    if (sig != ZipConstants.EndOfCentralDirectorySignature)
      throw new InvalidDataException("Cannot find end of central directory.");

    var diskNumber = reader.ReadUInt16();
    var cdDisk = reader.ReadUInt16();
    var cdCountOnDisk = reader.ReadUInt16();
    var cdCountTotal = reader.ReadUInt16();
    var cdSize = reader.ReadUInt32();
    var cdOffset = reader.ReadUInt32();
    var commentLen = reader.ReadUInt16();
    var comment = commentLen > 0 ? System.Text.Encoding.UTF8.GetString(reader.ReadBytes(commentLen)) : null;

    long actualOffset = cdOffset;
    long actualSize = cdSize;
    int actualCount = cdCountTotal;

    // Check for ZIP64 EOCD
    if (cdOffset == ZipConstants.Zip64Sentinel32 || cdCountTotal == ZipConstants.Zip64Sentinel16)
      ReadZip64Eocd(stream, eocdPos, ref actualOffset, ref actualSize, ref actualCount);

    return (actualOffset, actualSize, actualCount, comment);
  }

  /// <summary>
  /// Writes the end of central directory (and ZIP64 if needed).
  /// </summary>
  public static void Write(BinaryWriter writer, long cdOffset, long cdSize, int count, string? comment) {
    var commentBytes = comment != null ? System.Text.Encoding.UTF8.GetBytes(comment) : null;

    var needZip64 = cdOffset > uint.MaxValue || cdSize > uint.MaxValue || count > ushort.MaxValue;

    if (needZip64) {
      // ZIP64 EOCD
      var zip64EocdOffset = writer.BaseStream.Position;
      writer.Write(ZipConstants.Zip64EndOfCentralDirectorySignature);
      writer.Write((long)44); // size of remaining record
      writer.Write(ZipConstants.VersionMadeBy20);
      writer.Write(ZipConstants.VersionNeeded45);
      writer.Write(0u); // disk number
      writer.Write(0u); // cd disk
      writer.Write((long)count); // cd count on disk
      writer.Write((long)count); // cd count total
      writer.Write(cdSize);
      writer.Write(cdOffset);

      // ZIP64 EOCD Locator
      writer.Write(ZipConstants.Zip64EndOfCentralDirectoryLocatorSignature);
      writer.Write(0u); // disk with ZIP64 EOCD
      writer.Write(zip64EocdOffset);
      writer.Write(1u); // total disks
    }

    // Standard EOCD
    writer.Write(ZipConstants.EndOfCentralDirectorySignature);
    writer.Write((ushort)0); // disk number
    writer.Write((ushort)0); // cd disk
    writer.Write(needZip64 ? ZipConstants.Zip64Sentinel16 : (ushort)count);
    writer.Write(needZip64 ? ZipConstants.Zip64Sentinel16 : (ushort)count);
    writer.Write(needZip64 ? ZipConstants.Zip64Sentinel32 : (uint)cdSize);
    writer.Write(needZip64 ? ZipConstants.Zip64Sentinel32 : (uint)cdOffset);
    writer.Write((ushort)(commentBytes?.Length ?? 0));
    if (commentBytes != null)
      writer.Write(commentBytes);
  }

  private static long FindEocd(Stream stream) {
    // EOCD is at most 65535 + 22 bytes from the end
    var searchLen = Math.Min(stream.Length, 65557);
    var searchStart = stream.Length - searchLen;

    var buffer = new byte[searchLen];
    stream.Position = searchStart;
    var bytesRead = 0;
    while (bytesRead < buffer.Length) {
      var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
      if (read == 0) break;
      bytesRead += read;
    }

    // Scan from the end backwards for the signature
    for (var i = bytesRead - 22; i >= 0; --i) {
      if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B &&
        buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06) {
        return searchStart + i;
      }
    }

    throw new InvalidDataException("Cannot find end of central directory record.");
  }

  private static void ReadZip64Eocd(Stream stream, long eocdPos, ref long offset, ref long size, ref int count) {
    // ZIP64 EOCD locator is at eocdPos - 20
    if (eocdPos < 20)
      return;

    stream.Position = eocdPos - 20;
    var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);

    var locSig = reader.ReadUInt32();
    if (locSig != ZipConstants.Zip64EndOfCentralDirectoryLocatorSignature)
      return;

    var locDisk = reader.ReadUInt32();
    var zip64EocdOffset = reader.ReadInt64();
    var totalDisks = reader.ReadUInt32();

    // Read ZIP64 EOCD
    stream.Position = zip64EocdOffset;
    var z64Sig = reader.ReadUInt32();
    if (z64Sig != ZipConstants.Zip64EndOfCentralDirectorySignature)
      throw new InvalidDataException("Invalid ZIP64 end of central directory.");

    var z64RecSize = reader.ReadInt64();
    var z64VersionMade = reader.ReadUInt16();
    var z64VersionNeeded = reader.ReadUInt16();
    var z64DiskNumber = reader.ReadUInt32();
    var z64CdDisk = reader.ReadUInt32();
    var z64CountOnDisk = reader.ReadInt64();
    var z64CountTotal = reader.ReadInt64();
    var z64CdSize = reader.ReadInt64();
    var z64CdOffset = reader.ReadInt64();

    offset = z64CdOffset;
    size = z64CdSize;
    count = (int)z64CountTotal;
  }
}
