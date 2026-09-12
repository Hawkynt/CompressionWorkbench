using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Zoo;

/// <summary>Canonical Zoo type-1/type-2 directory entry serialization.</summary>
internal static class ZooDirectoryCodec {
  private const int NextOffsetOffset = 6;
  private const int DataOffsetOffset = 10;
  private const int FileNameOffset = 38;
  private const int FileNameSize = 13;

  internal readonly record struct ParsedEntry(ZooEntry Entry, uint NextOffset, byte Type);

  public static int GetHeaderSize(ZooEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (string.IsNullOrEmpty(entry.LongFileName))
      return ZooConstants.DirectoryEntryType1Size;

    var (longName, directory) = GetPortableNameParts(entry.LongFileName!);
    var longNameLength = Encoding.Latin1.GetByteCount(longName);
    var directoryLength = Encoding.Latin1.GetByteCount(directory);
    ValidatePortableNameLengths(longNameLength, directoryLength);
    return checked(ZooConstants.DirectoryEntryType2FixedSize + 10 + longNameLength + directoryLength);
  }

  public static byte[] BuildHeader(ZooEntry entry, uint nextOffset, uint dataOffset) {
    ArgumentNullException.ThrowIfNull(entry);

    var isType2 = !string.IsNullOrEmpty(entry.LongFileName);
    byte[] longNameBytes = [];
    byte[] directoryBytes = [];
    var variableLength = 0;

    if (isType2) {
      var (longName, directory) = GetPortableNameParts(entry.LongFileName!);
      longNameBytes = Encoding.Latin1.GetBytes(longName);
      directoryBytes = Encoding.Latin1.GetBytes(directory);
      ValidatePortableNameLengths(longNameBytes.Length, directoryBytes.Length);
      variableLength = checked(10 + longNameBytes.Length + directoryBytes.Length);
    }

    var headerSize = isType2
      ? checked(ZooConstants.DirectoryEntryType2FixedSize + variableLength)
      : ZooConstants.DirectoryEntryType1Size;
    var header = new byte[headerSize];
    var (dosDate, dosTime) = ZooEntry.ToMsDosDateTime(entry.LastModified);

    BinaryPrimitives.WriteUInt32LittleEndian(header, ZooConstants.Magic);
    header[4] = isType2 ? ZooConstants.TypeLongName : ZooConstants.TypeFile;
    header[5] = (byte)entry.CompressionMethod;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(NextOffsetOffset), nextOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(DataOffsetOffset), dataOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), dosDate);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), dosTime);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), entry.Crc16);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), entry.OriginalSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), entry.CompressedSize);
    header[28] = entry.MajorVersion;
    header[29] = entry.MinorVersion;
    header[30] = entry.IsDeleted ? (byte)1 : (byte)0;
    header[31] = 0; // file structure
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 0); // comment offset
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), 0); // comment length

    WriteShortName(header.AsSpan(FileNameOffset, FileNameSize), entry.FileName);

    if (!isType2)
      return header;

    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryVariableLengthOffset), checked((ushort)variableLength));
    header[53] = ZooConstants.NoTimezone;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset), 0);

    var position = ZooConstants.DirectoryEntryType2FixedSize;
    header[position++] = (byte)longNameBytes.Length;
    header[position++] = (byte)directoryBytes.Length;
    longNameBytes.CopyTo(header, position);
    position += longNameBytes.Length;
    directoryBytes.CopyTo(header, position);
    position += directoryBytes.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(position), ZooConstants.SystemIdPortable);
    position += 2;
    header.AsSpan(position, 3).Clear(); // no portable file attributes
    position += 3;
    header[position++] = 0; // no version-generation flags
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(position), 0); // no version number

    var directoryCrc = Crc16.Compute(header);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset), directoryCrc);
    return header;
  }

  public static ParsedEntry ReadHeader(Stream stream, long headerOffset) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Zoo directory parsing requires a seekable stream.", nameof(stream));
    if (headerOffset < 0 || headerOffset > stream.Length - ZooConstants.DirectoryEntryType1Size)
      throw new InvalidDataException($"Zoo directory entry offset {headerOffset} is outside the archive.");

    var fixedType1 = ReadBytesAt(stream, headerOffset, ZooConstants.DirectoryEntryType1Size);
    var tag = BinaryPrimitives.ReadUInt32LittleEndian(fixedType1);
    if (tag != ZooConstants.Magic)
      throw new InvalidDataException($"Invalid Zoo entry tag at offset {headerOffset}: 0x{tag:X8}.");

    var type = fixedType1[4];
    if (type is not (ZooConstants.TypeFile or ZooConstants.TypeLongName))
      throw new InvalidDataException($"Unsupported Zoo directory entry type {type} at offset {headerOffset}.");

    byte[] header;
    if (type == ZooConstants.TypeFile) {
      header = fixedType1;
    } else {
      if (headerOffset > stream.Length - ZooConstants.DirectoryEntryType2FixedSize)
        throw new InvalidDataException("Truncated Zoo type-2 directory entry.");

      var fixedType2 = ReadBytesAt(stream, headerOffset, ZooConstants.DirectoryEntryType2FixedSize);
      var variableLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedType2.AsSpan(ZooConstants.DirectoryVariableLengthOffset));
      var totalSize = checked(ZooConstants.DirectoryEntryType2FixedSize + variableLength);
      if (headerOffset > stream.Length - totalSize)
        throw new InvalidDataException("Zoo type-2 variable directory section extends beyond the archive.");

      header = ReadBytesAt(stream, headerOffset, totalSize);
      var storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset));
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset), 0);
      var computedCrc = Crc16.Compute(header);
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset), storedCrc);
      if (computedCrc != storedCrc)
        throw new InvalidDataException(
          $"Zoo directory CRC mismatch at offset {headerOffset}: expected 0x{storedCrc:X4}, computed 0x{computedCrc:X4}.");
    }

    var shortName = ReadShortName(header.AsSpan(FileNameOffset, FileNameSize));
    string? portableName = null;
    if (type == ZooConstants.TypeLongName) {
      var variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryVariableLengthOffset));
      if (variableLength > 0) {
        if (variableLength < 2)
          throw new InvalidDataException("Zoo type-2 variable directory section is too short for name lengths.");

        var longNameLength = header[ZooConstants.DirectoryEntryType2FixedSize];
        var directoryLength = header[ZooConstants.DirectoryEntryType2FixedSize + 1];
        var namesLength = checked(2 + longNameLength + directoryLength);
        if (namesLength > variableLength)
          throw new InvalidDataException("Zoo type-2 name lengths exceed the declared variable directory section.");

        var nameOffset = ZooConstants.DirectoryEntryType2FixedSize + 2;
        var longName = Encoding.Latin1.GetString(header, nameOffset, longNameLength);
        var directory = Encoding.Latin1.GetString(header, nameOffset + longNameLength, directoryLength);
        if (longName.Length == 0)
          longName = shortName;
        portableName = directory.Length == 0 ? longName : $"{directory.TrimEnd('/', '\\')}/{longName}";
      }
    }

    var entry = new ZooEntry {
      FileName = shortName,
      LongFileName = portableName,
      CompressionMethod = (ZooCompressionMethod)header[5],
      Crc16 = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)),
      OriginalSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)),
      CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)),
      LastModified = ZooEntry.FromMsDosDateTime(
        BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)),
        BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16))),
      MajorVersion = header[28],
      MinorVersion = header[29],
      IsDeleted = header[30] != 0,
      HeaderOffset = headerOffset,
      DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(DataOffsetOffset)),
      DirectorySize = header.Length,
    };

    return new ParsedEntry(
      entry,
      BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(NextOffsetOffset)),
      type);
  }

  public static void RefreshDirectoryCrc(Stream stream, long headerOffset) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek || !stream.CanWrite)
      throw new ArgumentException("Zoo directory CRC refresh requires a seekable writable stream.", nameof(stream));

    stream.Position = checked(headerOffset + 4);
    var type = stream.ReadByte();
    if (type < 0)
      throw new EndOfStreamException();
    if (type != ZooConstants.TypeLongName)
      return;

    var fixedType2 = ReadBytesAt(stream, headerOffset, ZooConstants.DirectoryEntryType2FixedSize);
    var variableLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedType2.AsSpan(ZooConstants.DirectoryVariableLengthOffset));
    var totalSize = checked(ZooConstants.DirectoryEntryType2FixedSize + variableLength);
    var header = ReadBytesAt(stream, headerOffset, totalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ZooConstants.DirectoryCrcOffset), 0);
    var crc = Crc16.Compute(header);

    stream.Position = checked(headerOffset + ZooConstants.DirectoryCrcOffset);
    Span<byte> crcBytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(crcBytes, crc);
    stream.Write(crcBytes);
  }

  private static (string LongName, string Directory) GetPortableNameParts(string fullName) {
    var normalized = fullName.Replace('\\', '/');
    var slash = normalized.LastIndexOf('/');
    var directory = slash >= 0 ? normalized[..slash].TrimEnd('/') : string.Empty;
    var longName = slash >= 0 ? normalized[(slash + 1)..] : normalized;
    if (longName.Length == 0)
      throw new InvalidDataException("Zoo portable pathname must include a non-empty filename.");
    return (longName, directory);
  }

  private static void ValidatePortableNameLengths(int longNameLength, int directoryLength) {
    if (longNameLength > byte.MaxValue)
      throw new InvalidDataException("Zoo long filename exceeds the 255-byte type-2 limit.");
    if (directoryLength > byte.MaxValue)
      throw new InvalidDataException("Zoo directory path exceeds the 255-byte type-2 limit.");
  }

  private static void WriteShortName(Span<byte> destination, string shortName) {
    destination.Clear();
    var bytes = Encoding.Latin1.GetBytes(shortName);
    if (bytes.Length > ZooConstants.MaxShortNameLength)
      throw new InvalidDataException("Zoo short filename exceeds the 12-byte type-1 field.");
    bytes.CopyTo(destination);
  }

  private static string ReadShortName(ReadOnlySpan<byte> bytes) {
    var terminator = bytes.IndexOf((byte)0);
    if (terminator < 0)
      terminator = bytes.Length;
    return Encoding.Latin1.GetString(bytes[..terminator]);
  }

  private static byte[] ReadBytesAt(Stream stream, long offset, int count) {
    var result = new byte[count];
    stream.Position = offset;
    var read = 0;
    while (read < count) {
      var current = stream.Read(result, read, count - read);
      if (current == 0)
        throw new EndOfStreamException("Unexpected end of Zoo directory data.");
      read += current;
    }
    return result;
  }
}
