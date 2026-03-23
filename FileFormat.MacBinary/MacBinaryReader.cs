using System.Text;

namespace FileFormat.MacBinary;

/// <summary>
/// Reads MacBinary I/II/III encoded files.
/// </summary>
public sealed class MacBinaryReader {
  /// <summary>Mac epoch: January 1, 1904, 00:00:00 UTC.</summary>
  private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Checks whether the stream contains a valid MacBinary header.
  /// </summary>
  /// <param name="input">The stream to check. Position is restored on return.</param>
  /// <returns><c>true</c> if the header appears to be valid MacBinary.</returns>
  public static bool IsMacBinary(Stream input) {
    var originalPosition = input.Position;
    try {
      if (input.Length - input.Position < MacBinaryConstants.HeaderSize)
        return false;

      var header = new byte[MacBinaryConstants.HeaderSize];
      if (ReadExact(input, header) < MacBinaryConstants.HeaderSize)
        return false;

      // Byte 0 must be 0 (old version number).
      if (header[0] != 0)
        return false;

      // Byte 74 must be 0.
      if (header[74] != 0)
        return false;

      // Filename length must be 1-63.
      int nameLen = header[1];
      if (nameLen < 1 || nameLen > 63)
        return false;

      // If MacBinary II or III, verify CRC.
      var minVersion = header[122];
      if (minVersion >= MacBinaryConstants.Version2) {
        var storedCrc = ReadUInt16BigEndian(header, MacBinaryConstants.CrcOffset);
        var computedCrc = ComputeCrcCcitt(header.AsSpan(0, 124));
        if (storedCrc != computedCrc)
          return false;
      }

      return true;
    } finally {
      input.Position = originalPosition;
    }
  }

  /// <summary>
  /// Reads and parses the 128-byte MacBinary header from the current stream position.
  /// </summary>
  /// <param name="input">The input stream positioned at the start of the header.</param>
  /// <returns>The parsed <see cref="MacBinaryHeader"/>.</returns>
  public static MacBinaryHeader ReadHeader(Stream input) {
    var header = new byte[MacBinaryConstants.HeaderSize];
    if (ReadExact(input, header) < MacBinaryConstants.HeaderSize)
      throw new InvalidDataException("Stream too short for MacBinary header.");

    if (header[0] != 0)
      throw new InvalidDataException("Invalid MacBinary header: byte 0 must be 0.");

    int nameLen = header[1];
    if (nameLen < 1 || nameLen > 63)
      throw new InvalidDataException($"Invalid MacBinary filename length: {nameLen}.");

    var fileName = Encoding.ASCII.GetString(header, 2, nameLen);

    var fileType = new byte[4];
    var fileCreator = new byte[4];
    Buffer.BlockCopy(header, 65, fileType, 0, 4);
    Buffer.BlockCopy(header, 69, fileCreator, 0, 4);

    var finderFlags = header[73];
    var dataForkLen = ReadUInt32BigEndian(header, 83);
    var resForkLen = ReadUInt32BigEndian(header, 87);
    var createdSecs = ReadUInt32BigEndian(header, 91);
    var modifiedSecs = ReadUInt32BigEndian(header, 95);

    var created = MacEpoch.AddSeconds(createdSecs);
    var modified = MacEpoch.AddSeconds(modifiedSecs);

    // Determine version from minimum version field and signature.
    var minVersion = header[122];
    byte version;
    if (minVersion >= MacBinaryConstants.Version3) {
      var sig = ReadUInt32BigEndian(header, MacBinaryConstants.SignatureOffset);
      version = sig == MacBinaryConstants.Signature
        ? MacBinaryConstants.Version3
        : MacBinaryConstants.Version2;
    } else if (minVersion >= MacBinaryConstants.Version2) {
      version = MacBinaryConstants.Version2;
    } else {
      version = MacBinaryConstants.Version1;
    }

    var headerCrc = ReadUInt16BigEndian(header, MacBinaryConstants.CrcOffset);

    // Verify CRC for MacBinary II+.
    if (version >= MacBinaryConstants.Version2) {
      var computedCrc = ComputeCrcCcitt(header.AsSpan(0, 124));
      if (headerCrc != computedCrc)
        throw new InvalidDataException(
          $"MacBinary header CRC mismatch: stored 0x{headerCrc:X4}, computed 0x{computedCrc:X4}.");
    }

    return new MacBinaryHeader {
      FileName = fileName,
      FileType = fileType,
      FileCreator = fileCreator,
      FinderFlags = finderFlags,
      DataForkLength = dataForkLen,
      ResourceForkLength = resForkLen,
      CreatedDate = created,
      ModifiedDate = modified,
      Version = version,
      HeaderCrc = headerCrc,
    };
  }

  /// <summary>
  /// Reads the data fork from the stream. The stream must be positioned at the start of the MacBinary file.
  /// </summary>
  /// <param name="input">The input stream positioned at offset 0 of the MacBinary file.</param>
  /// <returns>The data fork bytes.</returns>
  public static byte[] ReadDataFork(Stream input) {
    var header = ReadHeader(input);

    // Stream is now at offset 128 (right after the header).
    var data = new byte[header.DataForkLength];
    if (header.DataForkLength > 0 && ReadExact(input, data) < data.Length)
      throw new InvalidDataException("Stream too short for data fork.");

    return data;
  }

  /// <summary>
  /// Reads the resource fork from the stream. The stream must be positioned at the start of the MacBinary file.
  /// </summary>
  /// <param name="input">The input stream positioned at offset 0 of the MacBinary file.</param>
  /// <returns>The resource fork bytes.</returns>
  public static byte[] ReadResourceFork(Stream input) {
    var header = ReadHeader(input);

    // Skip past data fork (padded to 128-byte boundary).
    var dataForkPadded = RoundUp(header.DataForkLength, MacBinaryConstants.PaddingAlignment);
    input.Position = MacBinaryConstants.HeaderSize + dataForkPadded;

    var resource = new byte[header.ResourceForkLength];
    if (header.ResourceForkLength > 0 && ReadExact(input, resource) < resource.Length)
      throw new InvalidDataException("Stream too short for resource fork.");

    return resource;
  }

  /// <summary>
  /// Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>.
  /// </summary>
  private static long RoundUp(long value, int alignment) =>
    value == 0 ? 0 : ((value + alignment - 1) / alignment) * alignment;

  /// <summary>
  /// Reads a big-endian 32-bit unsigned integer from a byte array.
  /// </summary>
  private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
    (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

  /// <summary>
  /// Reads a big-endian 16-bit unsigned integer from a byte array.
  /// </summary>
  private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
    (ushort)(data[offset] << 8 | data[offset + 1]);

  /// <summary>
  /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream.
  /// </summary>
  private static int ReadExact(Stream stream, byte[] buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
      if (bytesRead == 0)
        break;
      totalRead += bytesRead;
    }
    return totalRead;
  }

  /// <summary>
  /// Computes CRC-CCITT (polynomial 0x1021, initial value 0) over the given data.
  /// This is the non-reflected (MSB-first) CRC-16 used by MacBinary.
  /// </summary>
  internal static ushort ComputeCrcCcitt(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data) {
      crc ^= (ushort)(b << 8);
      for (var i = 0; i < 8; i++) {
        if ((crc & 0x8000) != 0)
          crc = (ushort)((crc << 1) ^ 0x1021);
        else
          crc = (ushort)(crc << 1);
      }
    }
    return crc;
  }
}
