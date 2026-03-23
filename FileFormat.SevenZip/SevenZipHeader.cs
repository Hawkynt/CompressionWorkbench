using Compression.Core.Checksums;

namespace FileFormat.SevenZip;

/// <summary>
/// Represents the 32-byte signature header of a 7z archive.
/// </summary>
/// <remarks>
/// Layout (32 bytes total):
/// <list type="bullet">
/// <item>6 bytes: signature <c>37 7A BC AF 27 1C</c></item>
/// <item>2 bytes: version (major, minor)</item>
/// <item>4 bytes: StartHeaderCRC (CRC-32 of the next 20 bytes)</item>
/// <item>8 bytes: NextHeaderOffset (offset from end of signature header)</item>
/// <item>8 bytes: NextHeaderSize</item>
/// <item>4 bytes: NextHeaderCRC (CRC-32 of the next header data)</item>
/// </list>
/// </remarks>
internal sealed class SevenZipHeader {
  /// <summary>Gets or sets the major format version.</summary>
  public byte MajorVersion { get; set; } = SevenZipConstants.FormatMajorVersion;

  /// <summary>Gets or sets the minor format version.</summary>
  public byte MinorVersion { get; set; } = SevenZipConstants.FormatMinorVersion;

  /// <summary>Gets or sets the offset of the next header from the end of the signature header.</summary>
  public long NextHeaderOffset { get; set; }

  /// <summary>Gets or sets the size of the next header in bytes.</summary>
  public long NextHeaderSize { get; set; }

  /// <summary>Gets or sets the CRC-32 of the next header data.</summary>
  public uint NextHeaderCrc { get; set; }

  /// <summary>
  /// Reads a 7z signature header from a stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <returns>The parsed header.</returns>
  public static SevenZipHeader Read(Stream stream) {
    var headerBytes = new byte[SevenZipConstants.SignatureHeaderSize];
    ReadExact(stream, headerBytes, 0, headerBytes.Length);

    // Verify signature
    if (!headerBytes.AsSpan().StartsWith(SevenZipConstants.Signature))
      throw new InvalidDataException("Invalid 7z signature.");

    var majorVersion = headerBytes[6];
    var minorVersion = headerBytes[7];

    // Verify start header CRC (covers bytes 12..31, which is 20 bytes)
    var storedStartCrc = BitConverter.ToUInt32(headerBytes, 8);
    var computedStartCrc = Crc32.Compute(headerBytes.AsSpan(12, 20));
    if (storedStartCrc != computedStartCrc)
      throw new InvalidDataException("7z start header CRC mismatch.");

    var nextHeaderOffset = BitConverter.ToInt64(headerBytes, 12);
    var nextHeaderSize = BitConverter.ToInt64(headerBytes, 20);
    var nextHeaderCrc = BitConverter.ToUInt32(headerBytes, 28);

    return new SevenZipHeader {
      MajorVersion = majorVersion,
      MinorVersion = minorVersion,
      NextHeaderOffset = nextHeaderOffset,
      NextHeaderSize = nextHeaderSize,
      NextHeaderCrc = nextHeaderCrc,
    };
  }

  /// <summary>
  /// Writes this signature header to a stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public void Write(Stream stream) {
    var headerBytes = new byte[SevenZipConstants.SignatureHeaderSize];

    // Signature
    SevenZipConstants.Signature.CopyTo(headerBytes.AsSpan());

    // Version
    headerBytes[6] = MajorVersion;
    headerBytes[7] = MinorVersion;

    // NextHeaderOffset, NextHeaderSize, NextHeaderCRC at offsets 12, 20, 28
    BitConverter.TryWriteBytes(headerBytes.AsSpan(12), NextHeaderOffset);
    BitConverter.TryWriteBytes(headerBytes.AsSpan(20), NextHeaderSize);
    BitConverter.TryWriteBytes(headerBytes.AsSpan(28), NextHeaderCrc);

    // StartHeaderCRC at offset 8: CRC of bytes 12..31
    var startCrc = Crc32.Compute(headerBytes.AsSpan(12, 20));
    BitConverter.TryWriteBytes(headerBytes.AsSpan(8), startCrc);

    stream.Write(headerBytes, 0, headerBytes.Length);
  }

  private static void ReadExact(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of 7z stream.");
      totalRead += read;
    }
  }
}
