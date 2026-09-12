namespace FileFormat.BinHex;

/// <summary>
/// Decodes BinHex 4.0 (.hqx) encoded streams back to their original Mac binary form.
/// </summary>
public sealed class BinHexReader {
  /// <summary>
  /// Decodes a BinHex 4.0 encoded stream and returns the file components.
  /// </summary>
  /// <param name="input">The stream containing BinHex-encoded text.</param>
  /// <returns>A tuple containing the file name, data fork, resource fork, file type, and file creator.</returns>
  public static (string FileName, byte[] DataFork, byte[] ResourceFork, string FileType, string FileCreator) Decode(Stream input) {
    using var reader = new StreamReader(input, leaveOpen: true);
    var encoded = ExtractEncodedData(reader);
    var raw = DecodeBase64(encoded);
    var decompressed = RleDecode(raw);
    return ParseBinaryStructure(decompressed);
  }

  private static string ExtractEncodedData(StreamReader reader) {
    // Skip lines until we find the header
    string? line;
    var foundHeader = false;
    while ((line = reader.ReadLine()) != null) {
      if (line.Contains("BinHex")) {
        foundHeader = true;
        break;
      }
    }

    if (!foundHeader)
      throw new InvalidDataException("BinHex header line not found.");

    // Collect all characters between the two ':' markers
    var sb = new System.Text.StringBuilder();
    var started = false;

    while ((line = reader.ReadLine()) != null) {
      foreach (var ch in line) {
        if (ch == BinHexConstants.StartChar) {
          if (!started) {
            started = true;
            continue;
          }
          // Second ':' — end of data
          return sb.ToString();
        }

        if (started && !char.IsWhiteSpace(ch))
          sb.Append(ch);
      }
    }

    // Also check if ':' was on the header line itself
    if (!started) {
      throw new InvalidDataException("BinHex start marker ':' not found.");
    }

    throw new InvalidDataException("BinHex end marker ':' not found.");
  }

  private static byte[] DecodeBase64(string encoded) {
    var decode = BinHexConstants.DecodeTable;
    var result = new List<byte>(encoded.Length * 3 / 4);
    var i = 0;
    var len = encoded.Length;

    while (i + 4 <= len) {
      var a = decode[encoded[i++]];
      var b = decode[encoded[i++]];
      var c = decode[encoded[i++]];
      var d = decode[encoded[i++]];

      if (a == 0xFF || b == 0xFF || c == 0xFF || d == 0xFF)
        throw new InvalidDataException("Invalid character in BinHex data.");

      var bits = (a << 18) | (b << 12) | (c << 6) | d;
      result.Add((byte)(bits >> 16));
      result.Add((byte)(bits >> 8));
      result.Add((byte)bits);
    }

    var remaining = len - i;
    if (remaining == 2) {
      var a = decode[encoded[i++]];
      var b = decode[encoded[i]];
      if (a == 0xFF || b == 0xFF)
        throw new InvalidDataException("Invalid character in BinHex data.");
      var bits = (a << 6) | b;
      result.Add((byte)(bits >> 4));
    } else if (remaining == 3) {
      var a = decode[encoded[i++]];
      var b = decode[encoded[i++]];
      var c = decode[encoded[i]];
      if (a == 0xFF || b == 0xFF || c == 0xFF)
        throw new InvalidDataException("Invalid character in BinHex data.");
      var bits = (a << 12) | (b << 6) | c;
      result.Add((byte)(bits >> 10));
      result.Add((byte)(bits >> 2));
    } else if (remaining == 1) {
      throw new InvalidDataException("Invalid BinHex data length.");
    }

    return result.ToArray();
  }

  private static byte[] RleDecode(byte[] data) {
    var result = new List<byte>(data.Length);
    var i = 0;

    while (i < data.Length) {
      var b = data[i++];

      if (b == BinHexConstants.RleEscapeByte) {
        if (i >= data.Length)
          throw new InvalidDataException("Unexpected end of RLE data.");

        var count = data[i++];
        if (count == 0) {
          // Literal 0x90
          result.Add(BinHexConstants.RleEscapeByte);
        } else {
          if (result.Count == 0)
            throw new InvalidDataException("RLE repeat with no previous byte.");
          var prev = result[^1];
          // count includes the byte already emitted, so repeat (count - 1) more times
          for (var j = 0; j < count - 1; j++)
            result.Add(prev);
        }
      } else {
        result.Add(b);
      }
    }

    return result.ToArray();
  }

  private static (string FileName, byte[] DataFork, byte[] ResourceFork, string FileType, string FileCreator) ParseBinaryStructure(byte[] data) {
    var pos = 0;

    // Filename length + filename
    var nameLen = data[pos++];
    if (pos + nameLen > data.Length)
      throw new InvalidDataException("BinHex data truncated at filename.");
    var fileName = System.Text.Encoding.ASCII.GetString(data, pos, nameLen);
    pos += nameLen;

    // Version byte
    if (pos >= data.Length)
      throw new InvalidDataException("BinHex data truncated at version.");
    pos++; // skip version

    // File type (4 bytes)
    if (pos + 4 > data.Length)
      throw new InvalidDataException("BinHex data truncated at file type.");
    var fileType = System.Text.Encoding.ASCII.GetString(data, pos, 4);
    pos += 4;

    // File creator (4 bytes)
    if (pos + 4 > data.Length)
      throw new InvalidDataException("BinHex data truncated at file creator.");
    var fileCreator = System.Text.Encoding.ASCII.GetString(data, pos, 4);
    pos += 4;

    // Finder flags (2 bytes)
    if (pos + 2 > data.Length)
      throw new InvalidDataException("BinHex data truncated at Finder flags.");
    pos += 2; // skip flags

    // Data fork length (4 bytes, big-endian)
    if (pos + 4 > data.Length)
      throw new InvalidDataException("BinHex data truncated at data fork length.");
    var dataForkLen = ReadBigEndianInt32(data, pos);
    pos += 4;

    // Resource fork length (4 bytes, big-endian)
    if (pos + 4 > data.Length)
      throw new InvalidDataException("BinHex data truncated at resource fork length.");
    var resForkLen = ReadBigEndianInt32(data, pos);
    pos += 4;

    // Header CRC (2 bytes)
    if (pos + 2 > data.Length)
      throw new InvalidDataException("BinHex data truncated at header CRC.");
    var headerCrc = ReadBigEndianUInt16(data, pos);
    var computedHeaderCrc = ComputeCrc(data.AsSpan(0, pos));
    if (headerCrc != computedHeaderCrc)
      throw new InvalidDataException($"Header CRC mismatch: expected 0x{headerCrc:X4}, computed 0x{computedHeaderCrc:X4}.");
    pos += 2;

    // Data fork
    if (pos + dataForkLen > data.Length)
      throw new InvalidDataException("BinHex data truncated at data fork.");
    var dataFork = new byte[dataForkLen];
    Array.Copy(data, pos, dataFork, 0, dataForkLen);
    pos += dataForkLen;

    // Data fork CRC (2 bytes)
    if (pos + 2 > data.Length)
      throw new InvalidDataException("BinHex data truncated at data fork CRC.");
    var dataForkCrc = ReadBigEndianUInt16(data, pos);
    var computedDataCrc = ComputeCrc(dataFork);
    if (dataForkCrc != computedDataCrc)
      throw new InvalidDataException($"Data fork CRC mismatch: expected 0x{dataForkCrc:X4}, computed 0x{computedDataCrc:X4}.");
    pos += 2;

    // Resource fork
    if (pos + resForkLen > data.Length)
      throw new InvalidDataException("BinHex data truncated at resource fork.");
    var resourceFork = new byte[resForkLen];
    Array.Copy(data, pos, resourceFork, 0, resForkLen);
    pos += resForkLen;

    // Resource fork CRC (2 bytes)
    if (pos + 2 > data.Length)
      throw new InvalidDataException("BinHex data truncated at resource fork CRC.");
    var resForkCrc = ReadBigEndianUInt16(data, pos);
    var computedResCrc = ComputeCrc(resourceFork);
    if (resForkCrc != computedResCrc)
      throw new InvalidDataException($"Resource fork CRC mismatch: expected 0x{resForkCrc:X4}, computed 0x{computedResCrc:X4}.");

    return (fileName, dataFork, resourceFork, fileType, fileCreator);
  }

  private static int ReadBigEndianInt32(byte[] data, int offset) =>
    (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

  private static ushort ReadBigEndianUInt16(byte[] data, int offset) =>
    (ushort)((data[offset] << 8) | data[offset + 1]);

  /// <summary>
  /// Computes the CRC-CCITT (polynomial 0x1021, init 0x0000) over the given data.
  /// </summary>
  internal static ushort ComputeCrc(ReadOnlySpan<byte> data) {
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
