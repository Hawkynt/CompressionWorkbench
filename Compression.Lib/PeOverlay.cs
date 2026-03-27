using System.Buffers.Binary;

namespace Compression.Lib;

/// <summary>
/// Finds the overlay (data appended after the last PE section) in a PE executable.
/// Used to detect self-extracting archives from third-party tools (7-Zip, WinRAR, WinZip, etc.)
/// which prepend a native executable stub before the archive data.
/// </summary>
public static class PeOverlay {

  /// <summary>
  /// Returns the file offset where the PE overlay begins, or -1 if not a PE or no overlay exists.
  /// The overlay is the data after the last PE section — where SFX tools place the archive.
  /// </summary>
  public static long FindOverlayOffset(Stream stream) {
    try {
      stream.Position = 0;
      Span<byte> buf = stackalloc byte[64];

      // DOS header: must start with "MZ"
      if (stream.Read(buf[..2]) < 2 || buf[0] != 0x4D || buf[1] != 0x5A)
        return -1;

      // e_lfanew at offset 0x3C — pointer to PE header
      stream.Position = 0x3C;
      if (stream.Read(buf[..4]) < 4)
        return -1;
      var peOffset = BinaryPrimitives.ReadInt32LittleEndian(buf[..4]);
      if (peOffset <= 0 || peOffset + 24 > stream.Length)
        return -1;

      // PE signature: "PE\0\0"
      stream.Position = peOffset;
      if (stream.Read(buf[..4]) < 4)
        return -1;
      if (buf[0] != 'P' || buf[1] != 'E' || buf[2] != 0 || buf[3] != 0)
        return -1;

      // COFF header: skip Machine(2), read NumberOfSections(2), skip 12, read SizeOfOptionalHeader(2)
      if (stream.Read(buf[..20]) < 20)
        return -1;
      var numSections = BinaryPrimitives.ReadUInt16LittleEndian(buf[2..4]);
      var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(buf[16..18]);

      // Section table starts after optional header
      long sectionTableOffset = peOffset + 24 + sizeOfOptionalHeader;
      if (sectionTableOffset + numSections * 40L > stream.Length)
        return -1;

      // Find the maximum (PointerToRawData + SizeOfRawData) across all sections
      long maxEnd = 0;
      stream.Position = sectionTableOffset;
      var secBuf = new byte[40];
      for (var i = 0; i < numSections; i++) {
        if (stream.Read(secBuf) < 40)
          return -1;
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(secBuf.AsSpan(16));
        var rawPtr = BinaryPrimitives.ReadUInt32LittleEndian(secBuf.AsSpan(20));
        long end = rawPtr + rawSize;
        if (end > maxEnd)
          maxEnd = end;
      }

      if (maxEnd <= 0 || maxEnd >= stream.Length)
        return -1;

      return maxEnd;
    }
    catch {
      return -1;
    }
  }

  /// <summary>
  /// Scans from the given start offset for known archive signatures.
  /// Returns the (format, offset) of the first match, or null if none found.
  /// Scans up to maxScan bytes (default 1MB) to avoid scanning entire large executables.
  /// </summary>
  public static (FormatDetector.Format Format, long Offset)? ScanForArchive(Stream stream, long startOffset, long maxScan = 1024 * 1024) {
    if (startOffset < 0 || startOffset >= stream.Length)
      return null;

    var end = Math.Min(stream.Length - 4, startOffset + maxScan);
    stream.Position = startOffset;

    // Read a chunk and scan for signatures
    var chunkSize = (int)Math.Min(end - startOffset + 16, 4 * 1024 * 1024);
    var chunk = new byte[chunkSize];
    var read = stream.Read(chunk, 0, chunkSize);
    if (read < 4) return null;

    var span = chunk.AsSpan(0, read);

    for (var i = 0; i <= read - 4; i++) {
      // ZIP: PK\x03\x04
      if (span[i] == 0x50 && span[i + 1] == 0x4B && span[i + 2] == 0x03 && span[i + 3] == 0x04)
        return (FormatDetector.Format.Zip, startOffset + i);

      // 7z: 7z\xBC\xAF\x27\x1C
      if (i + 5 < read && span[i] == 0x37 && span[i + 1] == 0x7A && span[i + 2] == 0xBC &&
          span[i + 3] == 0xAF && span[i + 4] == 0x27 && span[i + 5] == 0x1C)
        return (FormatDetector.Format.SevenZip, startOffset + i);

      // RAR5: Rar!\x1A\x07\x01\x00
      // RAR4: Rar!\x1A\x07\x00
      if (i + 6 < read && span[i] == 0x52 && span[i + 1] == 0x61 && span[i + 2] == 0x72 &&
          span[i + 3] == 0x21 && span[i + 4] == 0x1A && span[i + 5] == 0x07)
        return (FormatDetector.Format.Rar, startOffset + i);

      // CAB: MSCF
      if (span[i] == 0x4D && span[i + 1] == 0x53 && span[i + 2] == 0x43 && span[i + 3] == 0x46)
        return (FormatDetector.Format.Cab, startOffset + i);

      // ARJ: 0x60 0xEA (first two bytes of ARJ archive header)
      if (span[i] == 0x60 && span[i + 1] == 0xEA)
        return (FormatDetector.Format.Arj, startOffset + i);

      // ACE: **ACE** at offset +7 from header start (header starts 7 bytes before **)
      if (i + 6 < read && span[i] == 0x2A && span[i + 1] == 0x2A && span[i + 2] == 0x41 &&
          span[i + 3] == 0x43 && span[i + 4] == 0x45 && span[i + 5] == 0x2A && span[i + 6] == 0x2A) {
        // ACE header starts 7 bytes before the **ACE** signature
        var aceOffset = startOffset + i - 7;
        if (aceOffset >= startOffset)
          return (FormatDetector.Format.Ace, aceOffset);
      }

      // LHA: look for -lh or -lz at offset 2 within an LHA header
      // LHA header: [headerSize][checksum][method(5)]...
      // Method is at offset 2, format: "-lhN-" or "-lzN-"
      if (i + 6 < read && span[i] == '-' && span[i + 1] == 'l' &&
          (span[i + 2] == 'h' || span[i + 2] == 'z') &&
          span[i + 4] == '-') {
        // Method string is at offset 2 of LHA entry header
        var lhaOffset = startOffset + i - 2;
        if (lhaOffset >= startOffset)
          return (FormatDetector.Format.Lzh, lhaOffset);
      }
    }

    return null;
  }

  /// <summary>
  /// Detects an embedded archive in a PE executable.
  /// First parses PE sections to find the overlay, then scans for archive signatures.
  /// Returns (format, archiveOffset) or null if no embedded archive found.
  /// </summary>
  public static (FormatDetector.Format Format, long Offset)? DetectEmbeddedArchive(string path) {
    try {
      using var fs = File.OpenRead(path);
      var overlayOffset = FindOverlayOffset(fs);
      if (overlayOffset < 0) return null;
      return ScanForArchive(fs, overlayOffset);
    }
    catch {
      return null;
    }
  }
}
