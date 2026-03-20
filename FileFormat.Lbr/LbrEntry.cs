namespace FileFormat.Lbr;

/// <summary>
/// Represents a single entry in a CP/M LBR archive directory.
/// </summary>
public sealed class LbrEntry {

  /// <summary>Filename in "NAME.EXT" format (CP/M 8.3, uppercase).</summary>
  public string FileName { get; init; } = string.Empty;

  /// <summary>Status byte: 0x00 = active, 0xFE = deleted/unused.</summary>
  public byte Status { get; init; }

  /// <summary>Offset in sectors from the start of the LBR file.</summary>
  public ushort SectorOffset { get; init; }

  /// <summary>Number of sectors occupied by this entry's data.</summary>
  public ushort SectorCount { get; init; }

  /// <summary>CRC-16 of the file data (may be 0 if not computed).</summary>
  public ushort Crc16 { get; init; }

  /// <summary>Optional creation date.</summary>
  public DateTime? CreatedDate { get; init; }

  /// <summary>Optional last-modified date.</summary>
  public DateTime? ModifiedDate { get; init; }

  /// <summary>Number of padding bytes in the last sector (0 means full sector used).</summary>
  public byte PadCount { get; init; }

  /// <summary>Whether this entry is active (not deleted).</summary>
  public bool IsActive => Status == LbrConstants.StatusActive;

  /// <summary>Byte offset of the file data from the start of the LBR file.</summary>
  public long DataOffset => (long)SectorOffset * LbrConstants.SectorSize;

  /// <summary>Total byte length of the file data (including any trailing padding).</summary>
  public long DataLength => (long)SectorCount * LbrConstants.SectorSize;

  /// <summary>
  /// Parses a 32-byte directory entry from a buffer.
  /// </summary>
  internal static LbrEntry Parse(ReadOnlySpan<byte> data) {
    var status = data[0];

    // Read filename (bytes 1-8) and extension (bytes 9-11), trimming pad chars
    var name = ReadPaddedString(data.Slice(1, LbrConstants.MaxFileNameLength));
    var ext = ReadPaddedString(data.Slice(9, LbrConstants.MaxExtensionLength));
    var fileName = ext.Length > 0 ? $"{name}.{ext}" : name;

    var sectorOffset = (ushort)(data[12] | (data[13] << 8));
    var sectorCount = (ushort)(data[14] | (data[15] << 8));
    var crc16 = (ushort)(data[16] | (data[17] << 8));

    var creationDate = ParseCpmDate(data[18] | (data[19] << 8));
    var modifiedDate = ParseCpmDate(data[20] | (data[21] << 8));

    // Bytes 22-23: creation time, 24-25: change time (ignored for now)
    var padCount = data[26];

    return new LbrEntry {
      FileName = fileName,
      Status = status,
      SectorOffset = sectorOffset,
      SectorCount = sectorCount,
      Crc16 = crc16,
      CreatedDate = creationDate,
      ModifiedDate = modifiedDate,
      PadCount = padCount,
    };
  }

  /// <summary>
  /// Serializes this entry into a 32-byte directory record.
  /// </summary>
  internal void WriteTo(Span<byte> buffer) {
    buffer.Clear();
    buffer[0] = Status;

    // Split filename into name and extension parts
    SplitFileName(FileName, out var name, out var ext);

    // Write name padded to 8 bytes
    WritePaddedString(buffer.Slice(1, LbrConstants.MaxFileNameLength), name, LbrConstants.MaxFileNameLength);

    // Write extension padded to 3 bytes
    WritePaddedString(buffer.Slice(9, LbrConstants.MaxExtensionLength), ext, LbrConstants.MaxExtensionLength);

    // Little-endian 16-bit values
    buffer[12] = (byte)SectorOffset;
    buffer[13] = (byte)(SectorOffset >> 8);
    buffer[14] = (byte)SectorCount;
    buffer[15] = (byte)(SectorCount >> 8);
    buffer[16] = (byte)Crc16;
    buffer[17] = (byte)(Crc16 >> 8);

    if (CreatedDate is { } cd) {
      var days = (ushort)(cd - LbrConstants.CpmEpoch).TotalDays;
      buffer[18] = (byte)days;
      buffer[19] = (byte)(days >> 8);
    }

    if (ModifiedDate is { } md) {
      var days = (ushort)(md - LbrConstants.CpmEpoch).TotalDays;
      buffer[20] = (byte)days;
      buffer[21] = (byte)(days >> 8);
    }

    buffer[26] = PadCount;
    // Bytes 27-31: reserved (already zeroed)
  }

  private static string ReadPaddedString(ReadOnlySpan<byte> data) {
    var end = data.Length;
    while (end > 0 && data[end - 1] == LbrConstants.PadChar)
      --end;

    return end == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(data[..end]);
  }

  private static void WritePaddedString(Span<byte> dest, string value, int maxLen) {
    dest.Fill(LbrConstants.PadChar);
    var bytes = System.Text.Encoding.ASCII.GetBytes(value.ToUpperInvariant());
    var len = Math.Min(bytes.Length, maxLen);
    bytes.AsSpan(0, len).CopyTo(dest);
  }

  internal static void SplitFileName(string fileName, out string name, out string ext) {
    var dot = fileName.LastIndexOf('.');
    if (dot >= 0) {
      name = fileName[..dot];
      ext = fileName[(dot + 1)..];
    } else {
      name = fileName;
      ext = string.Empty;
    }
  }

  private static DateTime? ParseCpmDate(int days) =>
    days == 0 ? null : LbrConstants.CpmEpoch.AddDays(days);

}
