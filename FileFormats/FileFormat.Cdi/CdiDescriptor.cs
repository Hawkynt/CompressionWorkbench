using System.Buffers.Binary;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI trailer and session/track-table helpers. Padus never
/// published the format; the layout here follows the independently documented
/// on-disk contract used by No$cash/psx-spx and is cross-checked against CDIrip,
/// Aaru and the MIT-licensed mkdcdisc writer.
/// </summary>
internal static class CdiDescriptor {
  internal const uint Version2 = 0x80000004;
  internal const uint Version3 = 0x80000005;
  internal const uint Version35 = 0x80000006;

  private const int SessionBlockSize = 15;
  private const int PhysicalSessionPreambleSize = 7;
  private const int LogicalTrackHeaderSize = 0x30;
  private const int FixedTrackTailSize = 0xAE;
  private const int FirstTrackPregapSectors = 150;
  private const int MaximumTrackCount = 99;
  private const int MaximumIndexCount = 1024;
  private const uint MaximumCdTextBlocks = 4096;

  // Physical writers use two 10-byte markers. The first eight bytes of the
  // first marker complete the preceding session block; its last two bytes plus
  // the second marker are the logical 12-byte Track/Disc Header signature.
  private static ReadOnlySpan<byte> PhysicalTrackMarker => [
    0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  private static ReadOnlySpan<byte> LogicalTrackMarker => [
    0xFF, 0xFF, 0x00, 0x00, 0x01, 0x00,
    0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  internal readonly record struct Footer(
    uint Version,
    long DescriptorOffset,
    long DescriptorLength,
    bool IsLegacyFooterOnly
  );

  internal static bool TryReadFooter(Stream stream, out Footer footer) {
    ArgumentNullException.ThrowIfNull(stream);
    footer = default;
    if (!stream.CanRead || !stream.CanSeek || stream.Length < 8)
      return false;

    var originalPosition = stream.Position;
    try {
      stream.Position = stream.Length - 8;
      Span<byte> bytes = stackalloc byte[8];
      stream.ReadExactly(bytes);

      var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
      if (version is not (Version2 or Version3 or Version35))
        return false;

      var locator = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);

      // CompressionWorkbench used a version+zero trailer before it emitted a
      // session descriptor. Keep those images readable/editable, but never
      // confuse them with a genuine DiscJuggler descriptor.
      if (locator == 0) {
        footer = new(version, stream.Length - 8, 8, IsLegacyFooterOnly: true);
        return true;
      }

      var descriptorOffset = version == Version35
        ? stream.Length - locator
        : locator;
      if (descriptorOffset < 0 || descriptorOffset > stream.Length - 8)
        return false;

      var descriptorLength = stream.Length - descriptorOffset;
      if (descriptorLength < 8 || descriptorLength > uint.MaxValue)
        return false;

      footer = new(version, descriptorOffset, descriptorLength, IsLegacyFooterOnly: false);
      return true;
    } catch (EndOfStreamException) {
      return false;
    } finally {
      stream.Position = originalPosition;
    }
  }

  /// <summary>
  /// Parses the real trailing session/track table. The returned file offsets
  /// describe the sector body before <see cref="Footer.DescriptorOffset"/> and
  /// already account for pregaps and per-track stored sector sizes.
  /// </summary>
  internal static bool TryReadTrackTable(Stream stream, Footer footer, out IReadOnlyList<CdiTrackInfo> tracks) {
    ArgumentNullException.ThrowIfNull(stream);
    tracks = [];
    if (footer.IsLegacyFooterOnly || footer.DescriptorLength <= 8 || footer.DescriptorLength > int.MaxValue)
      return false;

    var originalPosition = stream.Position;
    try {
      var bytes = new byte[checked((int)footer.DescriptorLength)];
      stream.Position = footer.DescriptorOffset;
      stream.ReadExactly(bytes);
      if (!TryParseTrackTable(bytes, footer.DescriptorOffset, out var parsed))
        return false;
      tracks = parsed;
      return true;
    } catch (EndOfStreamException) {
      return false;
    } catch (OverflowException) {
      return false;
    } finally {
      stream.Position = originalPosition;
    }
  }

  private static bool TryParseTrackTable(ReadOnlySpan<byte> descriptor, long dataAreaLength, out List<CdiTrackInfo> tracks) {
    tracks = [];
    if (descriptor.Length < 1 + SessionBlockSize + 8)
      return false;

    var sessionCount = descriptor[0];
    if (sessionCount is 0 or > 99)
      return false;

    var position = 1;
    var globalTrackNumber = 1;
    long bodyOffset = 0;

    for (var sessionNumber = 1; sessionNumber <= sessionCount; ++sessionNumber) {
      if (!TryReadSessionBlock(descriptor, position, out var trackCount) || trackCount is 0 or > MaximumTrackCount)
        return false;
      position += SessionBlockSize;

      for (var trackInSession = 0; trackInSession < trackCount; ++trackInSession) {
        if (!TryReadTrack(
              descriptor,
              ref position,
              sessionNumber,
              globalTrackNumber,
              bodyOffset,
              dataAreaLength,
              out var track,
              out var occupiedBytes))
          return false;

        tracks.Add(track);
        bodyOffset = checked(bodyOffset + occupiedBytes);
        ++globalTrackNumber;
      }
    }

    // A normal descriptor has a final zero-track session block before the disc
    // info block. Do not make it mandatory: several old generators omit or
    // partially overwrite trailing metadata, while the track table is intact.
    if (TryReadSessionBlock(descriptor, position, out var terminalTrackCount) && terminalTrackCount == 0)
      position += SessionBlockSize;

    return tracks.Count > 0 && bodyOffset <= dataAreaLength;
  }

  private static bool TryReadSessionBlock(ReadOnlySpan<byte> descriptor, int position, out int trackCount) {
    trackCount = 0;
    if (position < 0 || position > descriptor.Length - SessionBlockSize)
      return false;

    var block = descriptor.Slice(position, SessionBlockSize);
    if (block[0] != 0 || block[2] != 0 ||
        block[3] != 0 || block[4] != 0 || block[5] != 0 || block[6] != 0 ||
        block[7] != 0 || block[8] != 0 || block[9] != 1 ||
        block[10] != 0 || block[11] != 0 || block[12] != 0 ||
        block[13] != 0xFF || block[14] != 0xFF)
      return false;

    trackCount = block[1];
    return true;
  }

  private static bool TryReadTrack(
    ReadOnlySpan<byte> descriptor,
    ref int position,
    int sessionNumber,
    int trackNumber,
    long bodyOffset,
    long dataAreaLength,
    out CdiTrackInfo track,
    out long occupiedBytes
  ) {
    track = null!;
    occupiedBytes = 0;

    var trackStart = position;
    if (trackStart < 0 || trackStart > descriptor.Length - LogicalTrackHeaderSize)
      return false;
    if (!descriptor.Slice(trackStart, LogicalTrackMarker.Length).SequenceEqual(LogicalTrackMarker))
      return false;

    var filenameLength = descriptor[trackStart + 0x10];
    var trackHeaderLength = LogicalTrackHeaderSize + filenameLength;
    if (trackHeaderLength < LogicalTrackHeaderSize || trackStart > descriptor.Length - trackHeaderLength)
      return false;

    position = trackStart + trackHeaderLength;
    if (!TryReadUInt16(descriptor, ref position, out var indexCount) || indexCount > MaximumIndexCount)
      return false;

    var indexLengths = new int[indexCount];
    long indexTotal = 0;
    for (var i = 0; i < indexCount; ++i) {
      if (!TryReadInt32(descriptor, ref position, out var length) || length < 0)
        return false;
      indexLengths[i] = length;
      indexTotal = checked(indexTotal + length);
    }

    if (!TryReadUInt32(descriptor, ref position, out var cdTextBlocks) || cdTextBlocks > MaximumCdTextBlocks)
      return false;
    if (!TrySkipCdText(descriptor, ref position, cdTextBlocks))
      return false;

    var tail = position;
    if (tail < 0 || tail > descriptor.Length - FixedTrackTailSize)
      return false;

    var modeValue = descriptor[tail + 0x02];
    if (modeValue > (byte)CdiTrackMode.Mode2)
      return false;

    var startLbaValue = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(tail + 0x12, 4));
    var trackLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(tail + 0x16, 4));
    var readModeValue = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(tail + 0x2A, 4));
    var controlValue = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(tail + 0x2E, 4));
    if (readModeValue > (uint)CdiReadMode.Raw2352_Pw96 ||
        startLbaValue > int.MaxValue || trackLengthValue > int.MaxValue || controlValue > int.MaxValue)
      return false;

    var storedSectorSize = (CdiReadMode)readModeValue switch {
      CdiReadMode.Mode1_2048 => 2048,
      CdiReadMode.Mode2_2336 => 2336,
      CdiReadMode.Raw2352 => 2352,
      CdiReadMode.Raw2352_Q16 => 2352 + 16,
      CdiReadMode.Raw2352_Pw96 => 2352 + 96,
      _ => 0,
    };
    if (storedSectorSize == 0)
      return false;

    var pregapSectors = indexLengths.Length >= 2 ? indexLengths[0] : 0;
    long dataSectorCount = indexLengths.Length >= 2
      ? indexLengths.Skip(1).Aggregate(0L, static (sum, value) => checked(sum + value))
      : 0;

    long sectorCount = indexTotal > 0 ? indexTotal : trackLengthValue;
    if (sectorCount <= 0 || sectorCount > int.MaxValue)
      return false;
    if (dataSectorCount <= 0)
      dataSectorCount = Math.Max(0, sectorCount - pregapSectors);
    if (dataSectorCount > int.MaxValue || pregapSectors > sectorCount)
      return false;

    occupiedBytes = checked(sectorCount * storedSectorSize);
    if (bodyOffset < 0 || bodyOffset > dataAreaLength || occupiedBytes > dataAreaLength - bodyOffset)
      return false;

    var dataOffset = checked(bodyOffset + (long)pregapSectors * storedSectorSize);
    track = new CdiTrackInfo(
      sessionNumber,
      trackNumber,
      (CdiTrackMode)modeValue,
      (CdiReadMode)readModeValue,
      pregapSectors,
      checked((int)sectorCount),
      checked((int)dataSectorCount),
      checked((int)startLbaValue),
      storedSectorSize,
      checked((int)controlValue),
      bodyOffset,
      dataOffset
    );

    position = tail + FixedTrackTailSize;
    return true;
  }

  private static bool TrySkipCdText(ReadOnlySpan<byte> descriptor, ref int position, uint blockCount) {
    for (uint block = 0; block < blockCount; ++block) {
      for (var field = 0; field < 18; ++field) {
        if ((uint)position >= (uint)descriptor.Length)
          return false;
        var length = descriptor[position++];
        if (position > descriptor.Length - length)
          return false;
        position += length;
      }
    }
    return true;
  }

  /// <summary>
  /// Builds a normal DiscJuggler v3.5 single-session/single-track descriptor.
  /// The caller writes 150 pregap sectors followed by <paramref name="dataSectorCount"/>
  /// cooked Mode-1 sectors before appending these bytes.
  /// </summary>
  internal static byte[] BuildSingleTrackV35(uint dataSectorCount) {
    if (dataSectorCount == 0)
      throw new ArgumentOutOfRangeException(nameof(dataSectorCount));

    var totalTrackSectors = checked(dataSectorCount + FirstTrackPregapSectors);
    using var descriptor = new MemoryStream(capacity: 512);

    descriptor.WriteByte(1); // number of sessions
    WriteSessionPreamble(descriptor, trackCount: 1);
    WritePhysicalTrackHeader(descriptor, totalTracks: 1);
    WriteSingleTrackBody(descriptor, dataSectorCount, totalTrackSectors);

    // Zero-track terminal session. Its final eight bytes are supplied by the
    // prefix of the Disc Info Track/Disc Header, exactly like a normal session.
    WriteSessionPreamble(descriptor, trackCount: 0);
    WritePhysicalTrackHeader(descriptor, totalTracks: 1);
    WriteDiscInfo(descriptor, totalTrackSectors);

    var descriptorLength = checked((uint)(descriptor.Length + sizeof(uint)));
    WriteUInt32(descriptor, descriptorLength);
    return descriptor.ToArray();
  }

  private static void WriteSessionPreamble(Stream stream, ushort trackCount) {
    stream.WriteByte(0);
    WriteUInt16(stream, trackCount);
    WriteUInt32(stream, 0);
  }

  /// <summary>
  /// Writes the physical Track/Disc Header representation. The first eight
  /// bytes complete the preceding 15-byte session block, so the logical header
  /// begins eight bytes into this sequence.
  /// </summary>
  private static void WritePhysicalTrackHeader(Stream stream, byte totalTracks) {
    stream.Write(PhysicalTrackMarker);
    stream.Write(PhysicalTrackMarker);
    WriteZeros(stream, 3);
    stream.WriteByte(totalTracks);
    stream.WriteByte(0); // source filename length
    WriteZeros(stream, 11);
    WriteUInt32(stream, 2);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0x80000000);
    WriteUInt32(stream, 360000);
    WriteUInt32(stream, 0x00980000); // 00 00 + medium type 0098h (CD-ROM)
  }

  private static void WriteSingleTrackBody(Stream stream, uint dataSectorCount, uint totalTrackSectors) {
    WriteUInt16(stream, 2); // index 0 + index 1
    WriteUInt32(stream, FirstTrackPregapSectors);
    WriteUInt32(stream, dataSectorCount);
    WriteUInt32(stream, 0); // CD-Text blocks
    WriteUInt16(stream, 0);

    WriteUInt32(stream, (uint)CdiTrackMode.Mode1); // low byte is the mode; remaining bytes are zero
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0); // session number, zero based
    WriteUInt32(stream, 0); // track number in session, zero based
    WriteUInt32(stream, 0); // start LBA
    WriteUInt32(stream, totalTrackSectors);
    WriteZeros(stream, 16);
    WriteUInt32(stream, (uint)CdiReadMode.Mode1_2048);
    WriteUInt32(stream, 4); // data-track control nibble
    stream.WriteByte(0);
    WriteUInt32(stream, totalTrackSectors);
    WriteUInt32(stream, 0);
    WriteZeros(stream, 12); // ISRC
    WriteUInt32(stream, 0);
    stream.WriteByte(0);
    WriteFill(stream, 8, 0xFF);
    WriteUInt32(stream, 1);
    WriteUInt32(stream, 0x80);
    WriteUInt32(stream, 2);
    WriteUInt32(stream, 0x10);
    WriteUInt32(stream, 44100);
    WriteZeros(stream, 42);
    WriteUInt32(stream, uint.MaxValue);
    WriteZeros(stream, 12);
    stream.WriteByte((byte)CdiTrackMode.Mode1); // session type on last track
    WriteZeros(stream, 5);
    stream.WriteByte(0); // no following track
    stream.WriteByte(0);
    WriteUInt32(stream, 0);
  }

  private static void WriteDiscInfo(Stream stream, uint totalTrackSectors) {
    WriteUInt32(stream, totalTrackSectors);
    stream.WriteByte(0); // volume-id length
    stream.WriteByte(0);
    WriteUInt32(stream, 1);
    WriteUInt32(stream, 1);
    WriteZeros(stream, 13); // EAN-13
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0); // lead-in CD-Text length
    WriteZeros(stream, 8);
    WriteUInt32(stream, Version35);
  }

  private static bool TryReadUInt16(ReadOnlySpan<byte> data, ref int position, out ushort value) {
    value = 0;
    if (position < 0 || position > data.Length - sizeof(ushort))
      return false;
    value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
    position += sizeof(ushort);
    return true;
  }

  private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int position, out uint value) {
    value = 0;
    if (position < 0 || position > data.Length - sizeof(uint))
      return false;
    value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
    position += sizeof(uint);
    return true;
  }

  private static bool TryReadInt32(ReadOnlySpan<byte> data, ref int position, out int value) {
    value = 0;
    if (position < 0 || position > data.Length - sizeof(int))
      return false;
    value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
    position += sizeof(int);
    return true;
  }

  private static void WriteUInt16(Stream stream, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteZeros(Stream stream, int count) => WriteFill(stream, count, 0);

  private static void WriteFill(Stream stream, int count, byte value) {
    Span<byte> bytes = stackalloc byte[64];
    bytes.Fill(value);
    while (count > 0) {
      var chunk = Math.Min(count, bytes.Length);
      stream.Write(bytes[..chunk]);
      count -= chunk;
    }
  }
}
