namespace FileFormat.BinHex;

/// <summary>
/// Encodes files into BinHex 4.0 (.hqx) text format.
/// </summary>
public sealed class BinHexWriter {
  /// <summary>
  /// Writes a BinHex 4.0 encoded file to the output stream.
  /// </summary>
  /// <param name="output">The stream to write the encoded text to.</param>
  /// <param name="fileName">The original Mac filename.</param>
  /// <param name="dataFork">The data fork content.</param>
  /// <param name="resourceFork">The resource fork content, or null for an empty resource fork.</param>
  /// <param name="fileType">The 4-character Mac file type. Defaults to "TEXT".</param>
  /// <param name="fileCreator">The 4-character Mac file creator. Defaults to "ttxt".</param>
  public static void Write(Stream output, string fileName, byte[] dataFork, byte[]? resourceFork = null, string fileType = "TEXT", string fileCreator = "ttxt") {
    resourceFork ??= [];

    if (fileName.Length > 255)
      throw new ArgumentException("File name must be 255 characters or fewer.", nameof(fileName));
    if (fileType.Length != 4)
      throw new ArgumentException("File type must be exactly 4 characters.", nameof(fileType));
    if (fileCreator.Length != 4)
      throw new ArgumentException("File creator must be exactly 4 characters.", nameof(fileCreator));

    var binary = BuildBinaryStructure(fileName, dataFork, resourceFork, fileType, fileCreator);
    var rleCompressed = RleEncode(binary);
    var encoded = EncodeBase64(rleCompressed);
    WriteOutput(output, encoded);
  }

  private static byte[] BuildBinaryStructure(string fileName, byte[] dataFork, byte[] resourceFork, string fileType, string fileCreator) {
    using var ms = new MemoryStream();

    // Header section (everything before header CRC)
    var headerStart = (int)ms.Position;

    // Filename length + filename
    ms.WriteByte((byte)fileName.Length);
    ms.Write(System.Text.Encoding.ASCII.GetBytes(fileName));

    // Version
    ms.WriteByte(0);

    // File type (4 bytes)
    ms.Write(System.Text.Encoding.ASCII.GetBytes(fileType));

    // File creator (4 bytes)
    ms.Write(System.Text.Encoding.ASCII.GetBytes(fileCreator));

    // Finder flags (2 bytes, zeroed)
    ms.WriteByte(0);
    ms.WriteByte(0);

    // Data fork length (4 bytes, big-endian)
    WriteBigEndianInt32(ms, dataFork.Length);

    // Resource fork length (4 bytes, big-endian)
    WriteBigEndianInt32(ms, resourceFork.Length);

    // Header CRC
    var headerBytes = ms.ToArray();
    var headerCrc = BinHexReader.ComputeCrc(headerBytes.AsSpan(headerStart));
    WriteBigEndianUInt16(ms, headerCrc);

    // Data fork
    ms.Write(dataFork);

    // Data fork CRC
    var dataCrc = BinHexReader.ComputeCrc(dataFork);
    WriteBigEndianUInt16(ms, dataCrc);

    // Resource fork
    ms.Write(resourceFork);

    // Resource fork CRC
    var resCrc = BinHexReader.ComputeCrc(resourceFork);
    WriteBigEndianUInt16(ms, resCrc);

    return ms.ToArray();
  }

  private static byte[] RleEncode(byte[] data) {
    var result = new List<byte>(data.Length);
    var i = 0;

    while (i < data.Length) {
      var b = data[i];

      if (b == BinHexConstants.RleEscapeByte) {
        // Literal 0x90: emit 0x90, 0x00
        result.Add(BinHexConstants.RleEscapeByte);
        result.Add(0x00);
        i++;
        continue;
      }

      // Count consecutive identical bytes
      var runStart = i;
      while (i < data.Length && data[i] == b && (i - runStart) < 255)
        i++;
      var runLen = i - runStart;

      if (runLen >= 3) {
        // Emit byte + RLE marker + count
        result.Add(b);
        result.Add(BinHexConstants.RleEscapeByte);
        result.Add((byte)runLen);
      } else {
        // Emit bytes individually
        for (var j = 0; j < runLen; j++)
          result.Add(b);
      }
    }

    return result.ToArray();
  }

  private static string EncodeBase64(byte[] data) {
    var encode = BinHexConstants.EncodeTable;
    var sb = new System.Text.StringBuilder(data.Length * 4 / 3 + 4);
    var i = 0;

    while (i + 3 <= data.Length) {
      var bits = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
      sb.Append(encode[(bits >> 18) & 0x3F]);
      sb.Append(encode[(bits >> 12) & 0x3F]);
      sb.Append(encode[(bits >> 6) & 0x3F]);
      sb.Append(encode[bits & 0x3F]);
      i += 3;
    }

    var remaining = data.Length - i;
    if (remaining == 1) {
      var bits = data[i] << 16;
      sb.Append(encode[(bits >> 18) & 0x3F]);
      sb.Append(encode[(bits >> 12) & 0x3F]);
    } else if (remaining == 2) {
      var bits = (data[i] << 16) | (data[i + 1] << 8);
      sb.Append(encode[(bits >> 18) & 0x3F]);
      sb.Append(encode[(bits >> 12) & 0x3F]);
      sb.Append(encode[(bits >> 6) & 0x3F]);
    }

    return sb.ToString();
  }

  private static void WriteOutput(Stream output, string encoded) {
    using var writer = new StreamWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.NewLine = "\r\n";

    // Header line
    writer.WriteLine(BinHexConstants.HeaderLine);
    // Blank line
    writer.WriteLine();

    // ':' + encoded data wrapped at LineWidth + ':'
    var lineWidth = BinHexConstants.LineWidth;
    writer.Write(':');

    var pos = 0;
    var charsOnLine = 1; // the ':' counts

    while (pos < encoded.Length) {
      var charsRemaining = lineWidth - charsOnLine;
      if (charsRemaining <= 0) {
        writer.WriteLine();
        charsOnLine = 0;
        charsRemaining = lineWidth;
      }

      var charsToWrite = Math.Min(charsRemaining, encoded.Length - pos);
      writer.Write(encoded.AsSpan(pos, charsToWrite));
      pos += charsToWrite;
      charsOnLine += charsToWrite;
    }

    writer.Write(':');
    writer.WriteLine();
  }

  private static void WriteBigEndianInt32(MemoryStream ms, int value) {
    ms.WriteByte((byte)(value >> 24));
    ms.WriteByte((byte)(value >> 16));
    ms.WriteByte((byte)(value >> 8));
    ms.WriteByte((byte)value);
  }

  private static void WriteBigEndianUInt16(MemoryStream ms, ushort value) {
    ms.WriteByte((byte)(value >> 8));
    ms.WriteByte((byte)value);
  }
}
