using System.Text;

namespace FileFormat.Rar;

/// <summary>
/// Represents a parsed RAR5 file header (header type 2).
/// </summary>
internal sealed class RarFileHeader {
  /// <summary>Gets or sets the file-specific flags.</summary>
  public int FileFlags { get; set; }

  /// <summary>Gets or sets the unpacked (uncompressed) size.</summary>
  public long UnpackedSize { get; set; }

  /// <summary>Gets or sets the file attributes.</summary>
  public uint Attributes { get; set; }

  /// <summary>Gets or sets the modification time as a Unix timestamp (seconds since epoch).</summary>
  public uint Mtime { get; set; }

  /// <summary>Gets or sets the CRC-32 of the unpacked data.</summary>
  public uint DataCrc { get; set; }

  /// <summary>Gets or sets the compression info field.</summary>
  public int CompressionInfo { get; set; }

  /// <summary>Gets or sets the host operating system identifier.</summary>
  public int HostOs { get; set; }

  /// <summary>Gets or sets the file name.</summary>
  public string FileName { get; set; } = "";

  /// <summary>Gets the compression algorithm version from the compression info field.</summary>
  public int CompressionVersion => CompressionInfo & 0x3F;

  /// <summary>Gets a value indicating whether the entry uses solid compression.</summary>
  public bool IsSolid => (CompressionInfo & 0x40) != 0;

  /// <summary>Gets the compression method (0=Store, 1-5=compressed).</summary>
  public int CompressionMethod => (CompressionInfo >> 7) & 0x07;

  /// <summary>Gets the dictionary size as a log2 value.</summary>
  public int DictionarySizeLog => ((CompressionInfo >> 10) & 0x0F) + 17;

  /// <summary>Gets the dictionary size in bytes.</summary>
  public int DictionarySize => 1 << DictionarySizeLog;

  /// <summary>Gets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory => (FileFlags & RarConstants.FileFlagDirectory) != 0;

  /// <summary>Gets a value indicating whether a modification time is present.</summary>
  public bool HasMtime => (FileFlags & RarConstants.FileFlagTimeMtime) != 0;

  /// <summary>Gets a value indicating whether a CRC-32 is present.</summary>
  public bool HasCrc => (FileFlags & RarConstants.FileFlagCrc32) != 0;

  /// <summary>Gets a value indicating whether the unpacked size is unknown.</summary>
  public bool IsUnpackedSizeUnknown => (FileFlags & RarConstants.FileFlagUnpackedSizeUnknown) != 0;

  /// <summary>Gets or sets a value indicating whether this file is encrypted.</summary>
  public bool IsEncrypted { get; set; }

  /// <summary>Gets or sets the AES-256 IV for this file's encryption (16 bytes), or null if not encrypted.</summary>
  public byte[]? EncryptionIv { get; set; }

  /// <summary>Gets or sets the encryption version (0 = AES-256).</summary>
  public int EncryptionVersion { get; set; }

  /// <summary>Gets or sets the encryption flags for this file entry.</summary>
  public int EncryptionFlags { get; set; }

  /// <summary>
  /// Reads a file header from the raw header body bytes.
  /// </summary>
  /// <param name="headerData">The full header body bytes (from Type field onward).</param>
  /// <param name="header">The parsed common header (for flags like extra/data area).</param>
  /// <returns>The parsed file header.</returns>
  public static RarFileHeader Read(ReadOnlySpan<byte> headerData, RarHeader header) {
    var fileHeader = new RarFileHeader();
    var offset = 0;

    // Skip past Type and Flags vints (already parsed by RarHeader)
    _ = RarVint.Read(headerData[offset..], out var consumed); // type
    offset += consumed;
    _ = RarVint.Read(headerData[offset..], out consumed); // flags
    offset += consumed;

    // Skip extra area size if present
    if (header.HasExtraArea) {
      _ = RarVint.Read(headerData[offset..], out consumed);
      offset += consumed;
    }

    // Skip data area size if present
    if (header.HasDataArea) {
      _ = RarVint.Read(headerData[offset..], out consumed);
      offset += consumed;
    }

    // File flags
    fileHeader.FileFlags = (int)RarVint.Read(headerData[offset..], out consumed);
    offset += consumed;

    // Unpacked size (unless unknown)
    if (!fileHeader.IsUnpackedSizeUnknown) {
      fileHeader.UnpackedSize = (long)RarVint.Read(headerData[offset..], out consumed);
      offset += consumed;
    }

    // Attributes
    fileHeader.Attributes = (uint)RarVint.Read(headerData[offset..], out consumed);
    offset += consumed;

    // Mtime (4 bytes, uint32, Unix timestamp) if flagged
    if (fileHeader.HasMtime) {
      if (offset + 4 > headerData.Length)
        throw new InvalidDataException("RAR file header truncated at mtime.");
      fileHeader.Mtime = BitConverter.ToUInt32(headerData.Slice(offset, 4));
      offset += 4;
    }

    // Data CRC (4 bytes, uint32) if flagged
    if (fileHeader.HasCrc) {
      if (offset + 4 > headerData.Length)
        throw new InvalidDataException("RAR file header truncated at CRC.");
      fileHeader.DataCrc = BitConverter.ToUInt32(headerData.Slice(offset, 4));
      offset += 4;
    }

    // Compression info
    fileHeader.CompressionInfo = (int)RarVint.Read(headerData[offset..], out consumed);
    offset += consumed;

    // Host OS
    fileHeader.HostOs = (int)RarVint.Read(headerData[offset..], out consumed);
    offset += consumed;

    // Name length + filename bytes (UTF-8)
    var nameLength = (int)RarVint.Read(headerData[offset..], out consumed);
    offset += consumed;

    if (offset + nameLength > headerData.Length)
      throw new InvalidDataException("RAR file header truncated at filename.");
    fileHeader.FileName = Encoding.UTF8.GetString(headerData.Slice(offset, nameLength));
    offset += nameLength;

    // Parse extra area for encryption record
    if (header.HasExtraArea && header.ExtraSize > 0) {
      var extraEnd = offset + (int)header.ExtraSize;
      if (extraEnd > headerData.Length)
        extraEnd = headerData.Length;
      ParseExtraArea(fileHeader, headerData, offset, extraEnd);
    }

    return fileHeader;
  }

  private static void ParseExtraArea(RarFileHeader fileHeader,
      ReadOnlySpan<byte> headerData, int offset, int end) {
    while (offset < end) {
      var recordSize = (int)RarVint.Read(headerData[offset..], out var consumed);
      offset += consumed;
      var recordEnd = offset + recordSize;
      if (recordEnd > end) break;

      var recordType = (int)RarVint.Read(headerData[offset..], out consumed);
      offset += consumed;

      if (recordType == RarConstants.FileExtraEncryption) {
        // Encryption record: version(vint) + flags(vint) + kdfCount(1) + salt(16) + iv(16)
        fileHeader.EncryptionVersion = (int)RarVint.Read(headerData[offset..], out consumed);
        offset += consumed;
        fileHeader.EncryptionFlags = (int)RarVint.Read(headerData[offset..], out consumed);
        offset += consumed;
        // Skip kdfCount(1) + salt(16) — those are in the archive encryption header
        offset += 1 + 16;
        // IV (16 bytes)
        if (offset + 16 <= recordEnd) {
          fileHeader.EncryptionIv = headerData.Slice(offset, 16).ToArray();
          fileHeader.IsEncrypted = true;
        }
      }

      offset = recordEnd;
    }
  }

  /// <summary>
  /// Writes this file header's fields to a stream. Used for test archive creation.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public void WriteTo(Stream stream) {
    var nameBytes = Encoding.UTF8.GetBytes(FileName);

    RarVint.Write(stream, (ulong)FileFlags);
    RarVint.Write(stream, (ulong)UnpackedSize);
    RarVint.Write(stream, Attributes);

    if (HasMtime)
      stream.Write(BitConverter.GetBytes(Mtime));

    if (HasCrc)
      stream.Write(BitConverter.GetBytes(DataCrc));

    RarVint.Write(stream, (ulong)CompressionInfo);
    RarVint.Write(stream, (ulong)HostOs);
    RarVint.Write(stream, (ulong)nameBytes.Length);
    stream.Write(nameBytes);
  }
}
