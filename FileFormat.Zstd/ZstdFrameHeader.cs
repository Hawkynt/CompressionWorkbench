using System.Buffers.Binary;

namespace FileFormat.Zstd;

/// <summary>
/// Represents a Zstandard frame header as defined in RFC 8878 section 3.1.1.
/// </summary>
/// <param name="WindowSize">The window size for back-references.</param>
/// <param name="ContentSize">The uncompressed content size, or -1 if unknown.</param>
/// <param name="DictionaryId">The dictionary ID, or 0 if none.</param>
/// <param name="ContentChecksum">Whether a content checksum (XXH64 lower 32 bits) follows the last block.</param>
/// <param name="SingleSegment">Whether the content fits in a single segment (no window descriptor).</param>
internal readonly record struct ZstdFrameHeader(
  int WindowSize,
  long ContentSize,
  uint DictionaryId,
  bool ContentChecksum,
  bool SingleSegment) {
  /// <summary>
  /// Reads a Zstandard frame header from the stream.
  /// </summary>
  /// <param name="stream">The stream to read from, positioned after the magic number.</param>
  /// <param name="bytesRead">The number of bytes consumed (not including the magic).</param>
  /// <returns>The parsed frame header.</returns>
  /// <exception cref="InvalidDataException">The header data is malformed.</exception>
  public static ZstdFrameHeader Read(Stream stream, out int bytesRead) {
    bytesRead = 0;

    // Frame_Header_Descriptor
    var descriptor = stream.ReadByte();
    if (descriptor < 0)
      throw new InvalidDataException("Truncated Zstandard frame header descriptor.");
    ++bytesRead;

    var fcsFlag = (descriptor >> 6) & 3;
    var singleSegment = ((descriptor >> 5) & 1) != 0;
    // Bit 4 is reserved (unused)
    var contentChecksum = ((descriptor >> 2) & 1) != 0;
    var dictIdFlag = descriptor & 3;

    // Window_Descriptor (absent when singleSegment)
    int windowSize;
    if (!singleSegment) {
      var windowDescriptor = stream.ReadByte();
      if (windowDescriptor < 0)
        throw new InvalidDataException("Truncated Zstandard window descriptor.");
      ++bytesRead;

      var exponent = (windowDescriptor >> 3) & 0x1F;
      var mantissa = windowDescriptor & 7;
      var windowLog = 10 + exponent;
      windowSize = (1 << windowLog) + ((1 << windowLog) >> 3) * mantissa;
    }
    else {
      windowSize = 0; // will be set from content size
    }

    // Dictionary_ID (0, 1, 2, or 4 bytes)
    var dictionaryId = 0u;
    var dictIdBytes = dictIdFlag switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 0 };
    if (dictIdBytes > 0) {
      Span<byte> dictBuf = stackalloc byte[4];
      dictBuf.Clear();
      for (var i = 0; i < dictIdBytes; ++i) {
        var b = stream.ReadByte();
        if (b < 0)
          throw new InvalidDataException("Truncated Zstandard dictionary ID.");
        dictBuf[i] = (byte)b;
      }

      bytesRead += dictIdBytes;
      dictionaryId = BinaryPrimitives.ReadUInt32LittleEndian(dictBuf);
    }

    // Frame_Content_Size (0, 1, 2, 4, or 8 bytes)
    long contentSize = -1;
    var fcsBytes = fcsFlag switch {
      0 => singleSegment ? 1 : 0,
      1 => 2,
      2 => 4,
      3 => 8,
      _ => 0
    };

    if (fcsBytes > 0) {
      Span<byte> fcsBuf = stackalloc byte[8];
      fcsBuf.Clear();
      for (var i = 0; i < fcsBytes; ++i) {
        var b = stream.ReadByte();
        if (b < 0)
          throw new InvalidDataException("Truncated Zstandard frame content size.");
        fcsBuf[i] = (byte)b;
      }

      bytesRead += fcsBytes;

      contentSize = fcsBytes switch {
        1 => fcsBuf[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(fcsBuf) + 256,
        4 => BinaryPrimitives.ReadUInt32LittleEndian(fcsBuf),
        8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(fcsBuf),
        _ => -1
      };
    }

    // If single segment, window size = content size
    if (singleSegment && contentSize >= 0)
      windowSize = (int)Math.Min(contentSize, int.MaxValue);

    return new ZstdFrameHeader(windowSize, contentSize, dictionaryId, contentChecksum, singleSegment);
  }

  /// <summary>
  /// Writes the frame magic and header to the stream.
  /// Uses single-segment mode with known content size and content checksum enabled.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public void Write(Stream stream) {
    // Magic number (little-endian)
    Span<byte> magic = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(magic, ZstdConstants.FrameMagic);
    stream.Write(magic);

    // Determine FCS field size based on content size
    int fcsFlag;
    int fcsBytes;
    if (ContentSize < 0) {
      fcsFlag = 0;
      fcsBytes = 0;
    }
    else if (ContentSize <= 255) {
      fcsFlag = 0; // with single-segment, fcsFlag=0 means 1 byte
      fcsBytes = 1;
    }
    else if (ContentSize <= 65535 + 256) {
      fcsFlag = 1; // 2 bytes
      fcsBytes = 2;
    }
    else if (ContentSize <= uint.MaxValue) {
      fcsFlag = 2; // 4 bytes
      fcsBytes = 4;
    }
    else {
      fcsFlag = 3; // 8 bytes
      fcsBytes = 8;
    }

    // Build descriptor byte
    var descriptor = 0;
    descriptor |= (fcsFlag & 3) << 6;
    if (SingleSegment)
      descriptor |= 1 << 5;
    if (ContentChecksum)
      descriptor |= 1 << 2;
    // DictionaryId = 0 => dictIdFlag = 0

    stream.WriteByte((byte)descriptor);

    // Window descriptor (only if not single segment)
    if (!SingleSegment) {
      // Compute window descriptor from WindowSize
      var windowLog = 10;
      while ((1 << windowLog) < WindowSize && windowLog < ZstdConstants.MaxWindowLog)
        ++windowLog;

      var exponent = windowLog - 10;
      stream.WriteByte((byte)(exponent << 3));
    }

    // No dictionary ID

    // Frame content size
    if (fcsBytes > 0) {
      Span<byte> fcsBuf = stackalloc byte[8];
      fcsBuf.Clear();

      switch (fcsBytes) {
        case 1:
          fcsBuf[0] = (byte)ContentSize;
          break;
        case 2:
          BinaryPrimitives.WriteUInt16LittleEndian(fcsBuf, (ushort)(ContentSize - 256));
          break;
        case 4:
          BinaryPrimitives.WriteUInt32LittleEndian(fcsBuf, (uint)ContentSize);
          break;
        case 8:
          BinaryPrimitives.WriteUInt64LittleEndian(fcsBuf, (ulong)ContentSize);
          break;
      }

      stream.Write(fcsBuf[..fcsBytes]);
    }
  }
}
