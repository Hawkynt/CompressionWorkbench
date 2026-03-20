using System.Text;

namespace FileFormat.Tar;

/// <summary>
/// Handles reading and writing of 512-byte TAR header blocks.
/// </summary>
internal static class TarHeader {
  // Header field offsets
  private const int NameOffset = 0;
  private const int ModeOffset = 100;
  private const int UidOffset = 108;
  private const int GidOffset = 116;
  private const int SizeOffset = 124;
  private const int MtimeOffset = 136;
  private const int ChecksumOffset = 148;
  private const int TypeFlagOffset = 156;
  private const int LinkNameOffset = 157;
  private const int MagicOffset = 257;
  private const int VersionOffset = 263;
  private const int UnameOffset = 265;
  private const int GnameOffset = 297;
  private const int PrefixOffset = 345;

  // GNU multi-volume extension: offset of this chunk within original file
  // Stored in the "atime" field area in the GNU header variant (offset 369)
  private const int GnuOffsetOffset = 369;
  private const int GnuOffsetLength = 12;
  // Real size of the original file (stored in "realsize" area, offset 483 in GNU headers)
  private const int GnuRealSizeOffset = 483;
  private const int GnuRealSizeLength = 12;

  /// <summary>
  /// Reads a 512-byte header block from the stream and parses it into a <see cref="TarEntry"/>.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <param name="isEndOfArchive">Set to <see langword="true"/> if the block is all zeros (end-of-archive marker).</param>
  /// <returns>The parsed entry, or <see langword="null"/> if the block is all zeros.</returns>
  internal static TarEntry? ReadHeader(Stream stream, out bool isEndOfArchive) {
    byte[] header = new byte[TarConstants.BlockSize];
    int bytesRead = ReadExact(stream, header, TarConstants.BlockSize);

    if (bytesRead < TarConstants.BlockSize) {
      isEndOfArchive = true;
      return null;
    }

    // Check if the block is all zeros (end-of-archive marker)
    if (IsAllZeros(header)) {
      isEndOfArchive = true;
      return null;
    }

    isEndOfArchive = false;

    // Verify checksum
    int storedChecksum = (int)ParseOctalLong(header.AsSpan(ChecksumOffset, TarConstants.ChecksumLength));
    int computedChecksum = ComputeChecksum(header);

    if (storedChecksum != computedChecksum)
      throw new InvalidDataException(
        $"TAR header checksum mismatch: stored {storedChecksum}, computed {computedChecksum}.");

    // Parse the prefix and name
    string prefix = ParseString(header.AsSpan(PrefixOffset, TarConstants.PrefixLength));
    string name = ParseString(header.AsSpan(NameOffset, TarConstants.NameLength));

    if (prefix.Length > 0)
      name = prefix + "/" + name;

    byte typeFlag = header[TypeFlagOffset];

    var entry = new TarEntry {
      Name = name,
      Mode = (int)ParseOctalLong(header.AsSpan(ModeOffset, TarConstants.ModeLength)),
      Uid = (int)ParseOctalLong(header.AsSpan(UidOffset, TarConstants.UidLength)),
      Gid = (int)ParseOctalLong(header.AsSpan(GidOffset, TarConstants.GidLength)),
      Size = ParseOctalLong(header.AsSpan(SizeOffset, TarConstants.SizeLength)),
      ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(
        ParseOctalLong(header.AsSpan(MtimeOffset, TarConstants.MtimeLength))),
      TypeFlag = typeFlag,
      LinkName = ParseString(header.AsSpan(LinkNameOffset, TarConstants.LinkNameLength)),
      UserName = ParseString(header.AsSpan(UnameOffset, TarConstants.UnameLength)),
      GroupName = ParseString(header.AsSpan(GnameOffset, TarConstants.GnameLength)),
    };

    // Parse GNU multi-volume fields if present
    if (typeFlag == TarConstants.TypeGnuMultiVolume) {
      entry.Offset = ParseOctalLong(header.AsSpan(GnuOffsetOffset, GnuOffsetLength));
      entry.RealSize = ParseOctalLong(header.AsSpan(GnuRealSizeOffset, GnuRealSizeLength));
    }

    return entry;
  }

  /// <summary>
  /// Writes a 512-byte UStar header block for the given entry.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="entry">The entry to write a header for.</param>
  internal static void WriteHeader(Stream stream, TarEntry entry) {
    byte[] header = new byte[TarConstants.BlockSize];

    // Split name into prefix + name if needed
    string name = entry.Name;
    string prefix = string.Empty;

    if (name.Length > TarConstants.NameLength) {
      // Try to split at a '/' where prefix <= 155 and name <= 100
      SplitName(name, out prefix, out name);
    }

    // Name
    WriteString(header.AsSpan(NameOffset, TarConstants.NameLength), name);

    // Mode
    WriteOctal(header.AsSpan(ModeOffset, TarConstants.ModeLength), entry.Mode, TarConstants.ModeLength);

    // UID
    WriteOctal(header.AsSpan(UidOffset, TarConstants.UidLength), entry.Uid, TarConstants.UidLength);

    // GID
    WriteOctal(header.AsSpan(GidOffset, TarConstants.GidLength), entry.Gid, TarConstants.GidLength);

    // Size
    WriteOctal(header.AsSpan(SizeOffset, TarConstants.SizeLength), entry.Size, TarConstants.SizeLength);

    // Mtime
    var unixTime = entry.ModifiedTime.ToUnixTimeSeconds();
    WriteOctal(header.AsSpan(MtimeOffset, TarConstants.MtimeLength), unixTime, TarConstants.MtimeLength);

    // Type flag
    header[TypeFlagOffset] = entry.TypeFlag;

    // Link name
    WriteString(header.AsSpan(LinkNameOffset, TarConstants.LinkNameLength), entry.LinkName);

    // Magic and version (UStar)
    Encoding.ASCII.GetBytes(TarConstants.UstarMagic, header.AsSpan(MagicOffset, TarConstants.MagicLength));
    header[MagicOffset + 5] = 0; // null terminator after "ustar"
    header[VersionOffset] = (byte)'0';
    header[VersionOffset + 1] = (byte)'0';

    // User name
    WriteString(header.AsSpan(UnameOffset, TarConstants.UnameLength), entry.UserName);

    // Group name
    WriteString(header.AsSpan(GnameOffset, TarConstants.GnameLength), entry.GroupName);

    // Prefix
    WriteString(header.AsSpan(PrefixOffset, TarConstants.PrefixLength), prefix);

    // Write GNU multi-volume offset/realsize if this is a continuation entry
    if (entry.TypeFlag == TarConstants.TypeGnuMultiVolume) {
      WriteOctal(header.AsSpan(GnuOffsetOffset, GnuOffsetLength), entry.Offset, GnuOffsetLength);
      WriteOctal(header.AsSpan(GnuRealSizeOffset, GnuRealSizeLength), entry.RealSize, GnuRealSizeLength);
    }

    // Compute and write checksum (set checksum field to spaces first)
    for (int i = 0; i < TarConstants.ChecksumLength; ++i)
      header[ChecksumOffset + i] = (byte)' ';

    int checksum = ComputeChecksum(header);
    WriteOctal(header.AsSpan(ChecksumOffset, TarConstants.ChecksumLength), checksum, TarConstants.ChecksumLength);

    stream.Write(header, 0, TarConstants.BlockSize);
  }

  /// <summary>
  /// Parses an octal string from the given data, stopping at a null byte or space.
  /// </summary>
  /// <param name="data">The raw bytes containing the octal string.</param>
  /// <returns>The parsed octal string.</returns>
  internal static string ParseOctal(ReadOnlySpan<byte> data) {
    var end = data.Length;
    for (int i = 0; i < data.Length; ++i) {
      if (data[i] == 0 || data[i] == (byte)' ') {
        end = i;
        break;
      }
    }

    return Encoding.ASCII.GetString(data[..end]);
  }

  /// <summary>
  /// Parses an octal string from the given data and returns it as a <see cref="long"/>.
  /// </summary>
  /// <param name="data">The raw bytes containing the octal string.</param>
  /// <returns>The parsed value.</returns>
  internal static long ParseOctalLong(ReadOnlySpan<byte> data) {
    string octalStr = ParseOctal(data);
    if (octalStr.Length == 0)
      return 0;
    return Convert.ToInt64(octalStr, 8);
  }

  /// <summary>
  /// Writes an octal value into the destination span with leading zeros and a null terminator.
  /// </summary>
  /// <param name="dest">The destination span.</param>
  /// <param name="value">The value to encode.</param>
  /// <param name="fieldLen">The total field length including null terminator.</param>
  internal static void WriteOctal(Span<byte> dest, long value, int fieldLen) {
    // Format: leading-zero-padded octal digits followed by NUL
    // Available digit positions = fieldLen - 1 (last byte is NUL)
    int digitLen = fieldLen - 1;
    string octal = Convert.ToString(value, 8);

    if (octal.Length > digitLen)
      octal = octal[^digitLen..]; // truncate from the left if too long

    // Pad with leading zeros
    octal = octal.PadLeft(digitLen, '0');

    for (int i = 0; i < digitLen; ++i)
      dest[i] = (byte)octal[i];

    dest[digitLen] = 0; // null terminator
  }

  /// <summary>
  /// Computes the checksum for a 512-byte header block.
  /// The checksum field (offset 148, 8 bytes) is treated as spaces (0x20) during computation.
  /// </summary>
  /// <param name="header">The 512-byte header block.</param>
  /// <returns>The computed checksum.</returns>
  internal static int ComputeChecksum(ReadOnlySpan<byte> header) {
    var sum = 0;
    for (int i = 0; i < TarConstants.BlockSize; ++i) {
      if (i >= ChecksumOffset && i < ChecksumOffset + TarConstants.ChecksumLength)
        sum += (byte)' ';
      else
        sum += header[i];
    }

    return sum;
  }

  private static string ParseString(ReadOnlySpan<byte> data) {
    var end = data.Length;
    for (int i = 0; i < data.Length; ++i) {
      if (data[i] == 0) {
        end = i;
        break;
      }
    }

    return Encoding.UTF8.GetString(data[..end]);
  }

  private static void WriteString(Span<byte> dest, string value) {
    if (string.IsNullOrEmpty(value))
      return;

    int byteCount = Encoding.UTF8.GetByteCount(value);
    int len = Math.Min(byteCount, dest.Length);
    Encoding.UTF8.GetBytes(value.AsSpan(), dest[..len]);
  }

  private static void SplitName(string fullName, out string prefix, out string name) {
    // Try to split at a '/' boundary where prefix <= 155 and name <= 100
    for (int i = fullName.Length - 1; i >= 0; --i) {
      if (fullName[i] == '/') {
        string candidatePrefix = fullName[..i];
        string candidateName = fullName[(i + 1)..];

        if (candidatePrefix.Length <= TarConstants.PrefixLength &&
          candidateName.Length <= TarConstants.NameLength) {
          prefix = candidatePrefix;
          name = candidateName;
          return;
        }
      }
    }

    // Cannot split; name will be truncated (caller should use GNU long name extension)
    prefix = string.Empty;
    name = fullName;
  }

  private static bool IsAllZeros(byte[] data) {
    for (int i = 0; i < data.Length; ++i) {
      if (data[i] != 0)
        return false;
    }

    return true;
  }

  private static int ReadExact(Stream stream, byte[] buffer, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      int read = stream.Read(buffer, totalRead, count - totalRead);
      if (read == 0)
        break;
      totalRead += read;
    }

    return totalRead;
  }
}
