using System.Text;

namespace FileFormat.MacBinary;

/// <summary>
/// Writes MacBinary I/II/III encoded files.
/// </summary>
public sealed class MacBinaryWriter {
  /// <summary>Mac epoch: January 1, 1904, 00:00:00 UTC.</summary>
  private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Writes a MacBinary encoded file to the output stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="fileName">The Mac filename (1-63 characters).</param>
  /// <param name="dataFork">The data fork contents.</param>
  /// <param name="resourceFork">The resource fork contents, or <c>null</c> if none.</param>
  /// <param name="fileType">4-character file type (e.g., "TEXT"). Defaults to four zero bytes.</param>
  /// <param name="fileCreator">4-character creator code (e.g., "ttxt"). Defaults to four zero bytes.</param>
  /// <param name="modified">Modification date. Defaults to <see cref="DateTime.UtcNow"/>.</param>
  /// <param name="version">MacBinary version: 0 = I, 129 = II, 130 = III. Defaults to III.</param>
  public static void Write(
    Stream output,
    string fileName,
    byte[] dataFork,
    byte[]? resourceFork = null,
    string? fileType = null,
    string? fileCreator = null,
    DateTime? modified = null,
    int version = MacBinaryConstants.Version3) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(dataFork);

    if (fileName.Length < 1 || fileName.Length > 63)
      throw new ArgumentException("Filename must be 1-63 characters.", nameof(fileName));

    byte[] header = new byte[MacBinaryConstants.HeaderSize];

    // Byte 0: old version number (always 0).
    header[0] = 0;

    // Bytes 1-64: filename length + filename.
    byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
    int nameLen = Math.Min(nameBytes.Length, 63);
    header[1] = (byte)nameLen;
    Buffer.BlockCopy(nameBytes, 0, header, 2, nameLen);

    // Bytes 65-68: file type.
    if (fileType != null) {
      byte[] typeBytes = Encoding.ASCII.GetBytes(fileType);
      int len = Math.Min(typeBytes.Length, 4);
      Buffer.BlockCopy(typeBytes, 0, header, 65, len);
    }

    // Bytes 69-72: file creator.
    if (fileCreator != null) {
      byte[] creatorBytes = Encoding.ASCII.GetBytes(fileCreator);
      int len = Math.Min(creatorBytes.Length, 4);
      Buffer.BlockCopy(creatorBytes, 0, header, 69, len);
    }

    // Byte 73: Finder flags high byte (0 by default).
    // Byte 74: zero (required).
    // Bytes 75-80: icon position, window/folder ID (zeros).
    // Byte 81: protected flag (0).
    // Byte 82: zero.

    // Bytes 83-86: data fork length (big-endian).
    uint dataLen = (uint)dataFork.Length;
    WriteUInt32BigEndian(header, 83, dataLen);

    // Bytes 87-90: resource fork length (big-endian).
    uint resLen = resourceFork != null ? (uint)resourceFork.Length : 0;
    WriteUInt32BigEndian(header, 87, resLen);

    // Bytes 91-94: creation date (Mac seconds since 1904-01-01).
    DateTime mod = modified ?? DateTime.UtcNow;
    uint modSecs = DateTimeToMacSeconds(mod);
    WriteUInt32BigEndian(header, 91, modSecs); // creation = modification for simplicity
    WriteUInt32BigEndian(header, 95, modSecs);

    // Bytes 99-100: get info comment length (0).
    // Byte 101: Finder flags low byte (0).

    // Byte 122: minimum version needed to read.
    header[122] = (byte)version;
    // Byte 123: version of MacBinary that created this (same as minimum).
    header[123] = (byte)version;

    // MacBinary III: write "mBIN" signature at offset 102.
    if (version >= MacBinaryConstants.Version3) {
      WriteUInt32BigEndian(header, MacBinaryConstants.SignatureOffset, MacBinaryConstants.Signature);
    }

    // MacBinary II+: compute and write CRC-16 of bytes 0-123.
    if (version >= MacBinaryConstants.Version2) {
      ushort crc = MacBinaryReader.ComputeCrcCcitt(header.AsSpan(0, 124));
      WriteUInt16BigEndian(header, MacBinaryConstants.CrcOffset, crc);
    }

    // Bytes 126-127: reserved (zero).

    // Write header.
    output.Write(header, 0, MacBinaryConstants.HeaderSize);

    // Write data fork.
    if (dataLen > 0) {
      output.Write(dataFork, 0, dataFork.Length);
      WritePadding(output, dataLen);
    }

    // Write resource fork.
    if (resLen > 0) {
      output.Write(resourceFork!, 0, resourceFork!.Length);
      WritePadding(output, resLen);
    }
  }

  /// <summary>
  /// Writes zero-padding to bring the current fork up to a 128-byte boundary.
  /// </summary>
  private static void WritePadding(Stream output, uint forkLength) {
    int remainder = (int)(forkLength % MacBinaryConstants.PaddingAlignment);
    if (remainder != 0) {
      byte[] padding = new byte[MacBinaryConstants.PaddingAlignment - remainder];
      output.Write(padding, 0, padding.Length);
    }
  }

  /// <summary>
  /// Converts a <see cref="DateTime"/> to Mac seconds since January 1, 1904.
  /// </summary>
  private static uint DateTimeToMacSeconds(DateTime dt) {
    if (dt < MacEpoch)
      return 0;
    double totalSeconds = (dt.ToUniversalTime() - MacEpoch).TotalSeconds;
    if (totalSeconds > uint.MaxValue)
      return uint.MaxValue;
    return (uint)totalSeconds;
  }

  /// <summary>
  /// Writes a big-endian 32-bit unsigned integer to a byte array.
  /// </summary>
  private static void WriteUInt32BigEndian(byte[] data, int offset, uint value) {
    data[offset] = (byte)(value >> 24);
    data[offset + 1] = (byte)(value >> 16);
    data[offset + 2] = (byte)(value >> 8);
    data[offset + 3] = (byte)value;
  }

  /// <summary>
  /// Writes a big-endian 16-bit unsigned integer to a byte array.
  /// </summary>
  private static void WriteUInt16BigEndian(byte[] data, int offset, ushort value) {
    data[offset] = (byte)(value >> 8);
    data[offset + 1] = (byte)value;
  }
}
