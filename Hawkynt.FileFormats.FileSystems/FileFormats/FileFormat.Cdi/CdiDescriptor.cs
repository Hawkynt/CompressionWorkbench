using System.Buffers.Binary;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI trailer and session/track-table helpers. Padus never
/// published the format; the layouts here are clean-room descriptions of the
/// observable on-disk contracts cross-checked against CDIrip, Aaru, libMirage
/// and the MIT-licensed mkdcdisc writer.
/// </summary>
internal static class CdiDescriptor {
  internal const uint Version2 = 0x80000004;
  internal const uint Version3 = 0x80000005;
  internal const uint Version35 = 0x80000006;
  internal const int StandardPregapSectors = 150;

  private const int ModernSessionBlockSize = 15;
  private const int ModernLogicalTrackHeaderSize = 0x30;
  private const int ModernFixedTrackTailSize = 0xAE;
  private const int MaximumTrackCount = 99;
  private const int MaximumIndexCount = 1024;
  private const uint MaximumCdTextBlocks = 4096;

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
      if (locator == 0) {
        footer = new(version, stream.Length - 8, 8, IsLegacyFooterOnly: true);
        return true;
      }

      var descriptorOffset = version == Version35 ? stream.Length - locator : locator;
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
  /// Parses either the older v2/v3 descriptor dialect understood by CDIrip or
  /// the modern variable-index descriptor. v2/v3 gets legacy priority because
  /// its overlapping marker bytes can look like a valid modern session header;
  /// accepting that false positive would lose the stored pregap semantics.
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

      if (footer.Version is Version2 or Version3 &&
          TryParseLegacyTrackTable(bytes, footer, out var legacy)) {
        tracks = legacy;
        return true;
      }

      if (TryParseModernTrackTable(bytes, footer.DescriptorOffset, out var modern)) {
        tracks = modern;
        return true;
      }

      return false;
    } catch (EndOfStreamException) {
      return false;
    } catch (OverflowException) {
      return false;
    } finally {
      stream.Position = originalPosition;
    }
  }

  private static bool TryParseModernTrackTable(ReadOnlySpan<byte> descriptor, long dataAreaLength, out List<CdiTrackInfo> tracks) {
    tracks = [];
    if (descriptor.Length < 1 + ModernSessionBlockSize + 8)
      return false;

    var sessionCount = descriptor[0];
    if (sessionCount is 0 or > 99)
      return false;

    var position = 1;
    var globalTrackNumber = 1;
    long bodyOffset = 0;

    for (var sessionNumber = 1; sessionNumber <= sessionCount; ++sessionNumber) {
      if (!TryReadModernSessionBlock(descriptor, position, out var trackCount) || trackCount is 0 or > MaximumTrackCount)
        return false;
      position += ModernSessionBlockSize;

      for (var trackInSession = 0; trackInSession < trackCount; ++trackInSession) {
        if (!TryReadModernTrack(
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

    if (TryReadModernSessionBlock(descriptor, position, out var terminalTrackCount) && terminalTrackCount == 0)
      position += ModernSessionBlockSize;

    return tracks.Count > 0 && bodyOffset <= dataAreaLength;
  }

  private static bool TryReadModernSessionBlock(ReadOnlySpan<byte> descriptor, int position, out int trackCount) {
    trackCount = 0;
    if (position < 0 || position > descriptor.Length - ModernSessionBlockSize)
      return false;

    var block = descriptor.Slice(position, ModernSessionBlockSize);
    if (block[0] != 0 || block[2] != 0 ||
        block[3] != 0 || block[4] != 0 || block[5] != 0 || block[6] != 0 ||
        block[7] != 0 || block[8] != 0 || block[9] != 1 ||
        block[10] != 0 || block[11] != 0 || block[12] != 0 ||
        block[13] != 0xFF || block[14] != 0xFF)
      return false;

    trackCount = block[1];
    return true;
  }

  private static bool TryReadModernTrack(
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
    if (trackStart < 0 || trackStart > descriptor.Length - ModernLogicalTrackHeaderSize)
      return false;
    if (!descriptor.Slice(trackStart, LogicalTrackMarker.Length).SequenceEqual(LogicalTrackMarker))
      return false;

    var filenameLength = descriptor[trackStart + 0x10];
    var trackHeaderLength = ModernLogicalTrackHeaderSize + filenameLength;
    if (trackHeaderLength < ModernLogicalTrackHeaderSize || trackStart > descriptor.Length - trackHeaderLength)
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
    if (tail < 0 || tail > descriptor.Length - ModernFixedTrackTailSize)
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

    var storedSectorSize = StoredSectorSize((CdiReadMode)readModeValue);
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

    position = tail + ModernFixedTrackTailSize;
    return true;
  }

  private static bool TryParseLegacyTrackTable(
    ReadOnlySpan<byte> descriptor,
    Footer footer,
    out List<CdiTrackInfo> tracks
  ) {
    tracks = [];
    var position = 0;
    if (!TryReadUInt16(descriptor, ref position, out var sessionCount) || sessionCount is 0 or > 99)
      return false;

    var globalTrackNumber = 1;
    long bodyOffset = 0;
    for (var sessionNumber = 1; sessionNumber <= sessionCount; ++sessionNumber) {
      if (!TryReadUInt16(descriptor, ref position, out var trackCount) || trackCount is 0 or > MaximumTrackCount)
        return false;

      for (var trackInSession = 0; trackInSession < trackCount; ++trackInSession) {
        if (!TryReadLegacyTrack(
              descriptor,
              footer.Version,
              ref position,
              sessionNumber,
              globalTrackNumber,
              bodyOffset,
              footer.DescriptorOffset,
              out var track,
              out var occupiedBytes))
          return false;

        tracks.Add(track);
        bodyOffset = checked(bodyOffset + occupiedBytes);
        ++globalTrackNumber;
      }

      var sessionTrailer = footer.Version == Version2 ? 12 : 13;
      if (!TrySkip(descriptor, ref position, sessionTrailer))
        return false;
    }

    return tracks.Count > 0 && bodyOffset <= footer.DescriptorOffset;
  }

  private static bool TryReadLegacyTrack(
    ReadOnlySpan<byte> descriptor,
    uint version,
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

    if (!TryReadUInt32(descriptor, ref position, out var extendedPreamble))
      return false;
    if (extendedPreamble != 0 && !TrySkip(descriptor, ref position, 8))
      return false;

    if (!TryReadMarker(descriptor, ref position) || !TryReadMarker(descriptor, ref position))
      return false;
    if (!TrySkip(descriptor, ref position, 4) || (uint)position >= (uint)descriptor.Length)
      return false;

    var filenameLength = descriptor[position++];
    if (!TrySkip(descriptor, ref position, filenameLength) ||
        !TrySkip(descriptor, ref position, 11 + 4 + 4) ||
        !TryReadUInt32(descriptor, ref position, out var dj4Selector))
      return false;
    if (dj4Selector == 0x80000000 && !TrySkip(descriptor, ref position, 8))
      return false;
    if (!TrySkip(descriptor, ref position, 2) ||
        !TryReadUInt32(descriptor, ref position, out var pregapValue) ||
        !TryReadUInt32(descriptor, ref position, out var dataLengthValue) ||
        !TrySkip(descriptor, ref position, 6) ||
        !TryReadUInt32(descriptor, ref position, out var modeValue) ||
        !TrySkip(descriptor, ref position, 12) ||
        !TryReadUInt32(descriptor, ref position, out var startLbaValue) ||
        !TryReadUInt32(descriptor, ref position, out var totalLengthValue) ||
        !TrySkip(descriptor, ref position, 16) ||
        !TryReadUInt32(descriptor, ref position, out var sectorSizeSelector) ||
        !TrySkip(descriptor, ref position, 29))
      return false;

    if (version != Version2) {
      if (!TrySkip(descriptor, ref position, 5) ||
          !TryReadUInt32(descriptor, ref position, out var extensionSelector))
        return false;
      if (extensionSelector == uint.MaxValue && !TrySkip(descriptor, ref position, 78))
        return false;
    }

    if (modeValue > (uint)CdiTrackMode.Mode2 ||
        pregapValue > int.MaxValue || dataLengthValue > int.MaxValue ||
        totalLengthValue > int.MaxValue || startLbaValue > int.MaxValue)
      return false;

    var readMode = sectorSizeSelector switch {
      0 => CdiReadMode.Mode1_2048,
      1 => CdiReadMode.Mode2_2336,
      2 => CdiReadMode.Raw2352,
      _ => (CdiReadMode)uint.MaxValue,
    };
    var storedSectorSize = StoredSectorSize(readMode);
    if (storedSectorSize == 0)
      return false;

    var pregap = checked((int)pregapValue);
    var dataSectors = checked((int)dataLengthValue);
    var totalSectors = checked((int)totalLengthValue);
    if (totalSectors <= 0 || totalSectors < (long)pregap + dataSectors)
      return false;

    occupiedBytes = checked((long)totalSectors * storedSectorSize);
    if (bodyOffset < 0 || bodyOffset > dataAreaLength || occupiedBytes > dataAreaLength - bodyOffset)
      return false;

    var mode = (CdiTrackMode)modeValue;
    track = new CdiTrackInfo(
      sessionNumber,
      trackNumber,
      mode,
      readMode,
      pregap,
      totalSectors,
      dataSectors,
      checked((int)startLbaValue),
      storedSectorSize,
      mode == CdiTrackMode.Audio ? 0 : 4,
      bodyOffset,
      checked(bodyOffset + (long)pregap * storedSectorSize)
    );
    return true;
  }

  private static int StoredSectorSize(CdiReadMode readMode) => readMode switch {
    CdiReadMode.Mode1_2048 => 2048,
    CdiReadMode.Mode2_2336 => 2336,
    CdiReadMode.Raw2352 => 2352,
    CdiReadMode.Raw2352_Q16 => 2368,
    CdiReadMode.Raw2352_Pw96 => 2448,
    _ => 0,
  };

  private static bool TryReadMarker(ReadOnlySpan<byte> descriptor, ref int position) {
    if (position < 0 || position > descriptor.Length - PhysicalTrackMarker.Length)
      return false;
    if (!descriptor.Slice(position, PhysicalTrackMarker.Length).SequenceEqual(PhysicalTrackMarker))
      return false;
    position += PhysicalTrackMarker.Length;
    return true;
  }

  private static bool TrySkipCdText(ReadOnlySpan<byte> descriptor, ref int position, uint blockCount) {
    for (uint block = 0; block < blockCount; ++block) {
      for (var field = 0; field < 18; ++field) {
        if ((uint)position >= (uint)descriptor.Length)
          return false;
        var length = descriptor[position++];
        if (!TrySkip(descriptor, ref position, length))
          return false;
      }
    }
    return true;
  }

  internal static byte[] BuildSingleTrackV35(uint dataSectorCount) {
    if (dataSectorCount == 0)
      throw new ArgumentOutOfRangeException(nameof(dataSectorCount));

    var totalTrackSectors = checked(dataSectorCount + StandardPregapSectors);
    using var descriptor = new MemoryStream(capacity: 512);

    descriptor.WriteByte(1);
    WriteModernSessionPreamble(descriptor, trackCount: 1);
    WriteModernPhysicalTrackHeader(descriptor, totalTracks: 1);
    WriteModernSingleTrackBody(descriptor, dataSectorCount, totalTrackSectors);

    WriteModernSessionPreamble(descriptor, trackCount: 0);
    WriteModernPhysicalTrackHeader(descriptor, totalTracks: 1);
    WriteModernDiscInfo(descriptor, totalTrackSectors);

    var descriptorLength = checked((uint)(descriptor.Length + sizeof(uint)));
    WriteUInt32(descriptor, descriptorLength);
    return descriptor.ToArray();
  }

  internal static byte[] BuildSingleTrackLegacy(uint version, uint dataSectorCount, uint descriptorOffset) {
    if (version is not (Version2 or Version3))
      throw new ArgumentOutOfRangeException(nameof(version));
    if (dataSectorCount == 0)
      throw new ArgumentOutOfRangeException(nameof(dataSectorCount));

    var totalTrackSectors = checked(dataSectorCount + StandardPregapSectors);
    using var descriptor = new MemoryStream(capacity: 256);
    WriteUInt16(descriptor, 1);
    WriteUInt16(descriptor, 1);

    WriteUInt32(descriptor, 0);
    descriptor.Write(PhysicalTrackMarker);
    descriptor.Write(PhysicalTrackMarker);
    WriteZeros(descriptor, 4);
    descriptor.WriteByte(0);
    WriteZeros(descriptor, 11 + 4 + 4);
    WriteUInt32(descriptor, 0);
    WriteZeros(descriptor, 2);
    WriteUInt32(descriptor, StandardPregapSectors);
    WriteUInt32(descriptor, dataSectorCount);
    WriteZeros(descriptor, 6);
    WriteUInt32(descriptor, (uint)CdiTrackMode.Mode1);
    WriteZeros(descriptor, 12);
    WriteUInt32(descriptor, 0);
    WriteUInt32(descriptor, totalTrackSectors);
    WriteZeros(descriptor, 16);
    WriteUInt32(descriptor, 0);
    WriteZeros(descriptor, 29);

    if (version == Version3) {
      WriteZeros(descriptor, 5);
      WriteUInt32(descriptor, 0);
    }

    WriteZeros(descriptor, version == Version2 ? 12 : 13);
    WriteUInt32(descriptor, version);
    WriteUInt32(descriptor, descriptorOffset);
    return descriptor.ToArray();
  }

  private static void WriteModernSessionPreamble(Stream stream, ushort trackCount) {
    stream.WriteByte(0);
    WriteUInt16(stream, trackCount);
    WriteUInt32(stream, 0);
  }

  private static void WriteModernPhysicalTrackHeader(Stream stream, byte totalTracks) {
    stream.Write(PhysicalTrackMarker);
    stream.Write(PhysicalTrackMarker);
    WriteZeros(stream, 3);
    stream.WriteByte(totalTracks);
    stream.WriteByte(0);
    WriteZeros(stream, 11);
    WriteUInt32(stream, 2);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0x80000000);
    WriteUInt32(stream, 360000);
    WriteUInt32(stream, 0x00980000);
  }

  private static void WriteModernSingleTrackBody(Stream stream, uint dataSectorCount, uint totalTrackSectors) {
    WriteUInt16(stream, 2);
    WriteUInt32(stream, StandardPregapSectors);
    WriteUInt32(stream, dataSectorCount);
    WriteUInt32(stream, 0);
    WriteUInt16(stream, 0);

    WriteUInt32(stream, (uint)CdiTrackMode.Mode1);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, totalTrackSectors);
    WriteZeros(stream, 16);
    WriteUInt32(stream, (uint)CdiReadMode.Mode1_2048);
    WriteUInt32(stream, 4);
    stream.WriteByte(0);
    WriteUInt32(stream, totalTrackSectors);
    WriteUInt32(stream, 0);
    WriteZeros(stream, 12);
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
    stream.WriteByte((byte)CdiTrackMode.Mode1);
    WriteZeros(stream, 5);
    stream.WriteByte(0);
    stream.WriteByte(0);
    WriteUInt32(stream, 0);
  }

  private static void WriteModernDiscInfo(Stream stream, uint totalTrackSectors) {
    WriteUInt32(stream, totalTrackSectors);
    stream.WriteByte(0);
    stream.WriteByte(0);
    WriteUInt32(stream, 1);
    WriteUInt32(stream, 1);
    WriteZeros(stream, 13);
    WriteUInt32(stream, 0);
    WriteUInt32(stream, 0);
    WriteZeros(stream, 8);
    WriteUInt32(stream, Version35);
  }

  private static bool TrySkip(ReadOnlySpan<byte> data, ref int position, int count) {
    if (count < 0 || position < 0 || position > data.Length - count)
      return false;
    position += count;
    return true;
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
