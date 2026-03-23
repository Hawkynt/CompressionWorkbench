namespace FileFormat.Gzip;

/// <summary>
/// Represents the header of a GZIP member (RFC 1952).
/// </summary>
public sealed class GzipHeader {
  /// <summary>Gets or sets the compression method (always 8 for Deflate).</summary>
  public byte Method { get; set; } = GzipConstants.MethodDeflate;

  /// <summary>Gets or sets the header flags.</summary>
  public byte Flags { get; set; }

  /// <summary>Gets or sets the modification time as Unix timestamp.</summary>
  public uint ModificationTime { get; set; }

  /// <summary>Gets or sets the extra flags (compression level hint).</summary>
  public byte ExtraFlags { get; set; }

  /// <summary>Gets or sets the operating system code.</summary>
  public byte OperatingSystem { get; set; } = GzipConstants.OsUnknown;

  /// <summary>Gets or sets the extra field data (if FEXTRA flag is set).</summary>
  public byte[]? ExtraField { get; set; }

  /// <summary>Gets or sets the original file name (if FNAME flag is set).</summary>
  public string? FileName { get; set; }

  /// <summary>Gets or sets the comment (if FCOMMENT flag is set).</summary>
  public string? Comment { get; set; }

  /// <summary>Gets or sets the header CRC16 (if FHCRC flag is set).</summary>
  public ushort? HeaderCrc { get; set; }

  /// <summary>
  /// Reads a GZIP header from the stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <returns>The parsed header.</returns>
  public static GzipHeader Read(Stream stream) {
    var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);

    var magic1 = reader.ReadByte();
    var magic2 = reader.ReadByte();
    if (magic1 != GzipConstants.Magic1 || magic2 != GzipConstants.Magic2)
      throw new InvalidDataException($"Invalid GZIP magic: 0x{magic1:X2} 0x{magic2:X2}");

    var header = new GzipHeader {
      Method = reader.ReadByte(),
      Flags = reader.ReadByte(),
      ModificationTime = reader.ReadUInt32(),
      ExtraFlags = reader.ReadByte(),
      OperatingSystem = reader.ReadByte()
    };

    if (header.Method != GzipConstants.MethodDeflate)
      throw new InvalidDataException($"Unsupported GZIP compression method: {header.Method}");

    // FEXTRA
    if ((header.Flags & GzipConstants.FlagExtra) != 0) {
      var extraLen = reader.ReadUInt16();
      header.ExtraField = reader.ReadBytes(extraLen);
    }

    // FNAME
    if ((header.Flags & GzipConstants.FlagName) != 0)
      header.FileName = ReadNullTerminatedString(stream);

    // FCOMMENT
    if ((header.Flags & GzipConstants.FlagComment) != 0)
      header.Comment = ReadNullTerminatedString(stream);

    // FHCRC
    if ((header.Flags & GzipConstants.FlagHcrc) != 0)
      header.HeaderCrc = reader.ReadUInt16();

    return header;
  }

  /// <summary>
  /// Writes this GZIP header to the stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public void Write(Stream stream) {
    var writer = new BinaryWriter(stream, System.Text.Encoding.Latin1, leaveOpen: true);

    writer.Write(GzipConstants.Magic1);
    writer.Write(GzipConstants.Magic2);
    writer.Write(Method);

    // Compute flags from properties
    var flags = Flags;
    if (ExtraField != null) flags |= GzipConstants.FlagExtra;
    if (FileName != null) flags |= GzipConstants.FlagName;
    if (Comment != null) flags |= GzipConstants.FlagComment;
    if (HeaderCrc.HasValue) flags |= GzipConstants.FlagHcrc;

    writer.Write(flags);
    writer.Write(ModificationTime);
    writer.Write(ExtraFlags);
    writer.Write(OperatingSystem);

    // FEXTRA
    if (ExtraField != null) {
      writer.Write((ushort)ExtraField.Length);
      writer.Write(ExtraField);
    }

    // FNAME
    if (FileName != null)
      WriteNullTerminatedString(stream, FileName);

    // FCOMMENT
    if (Comment != null)
      WriteNullTerminatedString(stream, Comment);

    // FHCRC
    if (HeaderCrc.HasValue)
      writer.Write(HeaderCrc.Value);

    writer.Flush();
  }

  private static string ReadNullTerminatedString(Stream stream) {
    var bytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) > 0)
      bytes.Add((byte)b);
    if (b < 0)
      throw new EndOfStreamException("Unexpected end of stream reading null-terminated string.");
    return System.Text.Encoding.Latin1.GetString([.. bytes]);
  }

  private static void WriteNullTerminatedString(Stream stream, string value) {
    var bytes = System.Text.Encoding.Latin1.GetBytes(value);
    stream.Write(bytes);
    stream.WriteByte(0);
  }
}
