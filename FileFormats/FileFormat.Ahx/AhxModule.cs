using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ahx;

/// <summary>
/// Structural AHX0/AHX1 model implemented from the published format description.
/// HivelyTracker's BSD-3-Clause reader/writer is used only as an interoperability oracle,
/// notably for the timing-bit placement and track-zero storage convention.
/// </summary>
internal sealed class AhxModule {
  internal const int HeaderSize = 14;
  internal const int Channels = 4;
  internal const int InstrumentHeaderSize = 22;

  internal required byte Version { get; init; }
  internal required ushort DeclaredNamesOffset { get; init; }
  internal required bool TrackZeroStored { get; init; }
  internal required int SpeedMultiplier { get; init; }
  internal required int PositionCount { get; init; }
  internal required int RestartPosition { get; init; }
  internal required int TrackLength { get; init; }
  internal required int MaxTrack { get; init; }
  internal required int InstrumentCount { get; init; }
  internal required int SubsongCount { get; init; }
  internal required byte[] Subsongs { get; init; }
  internal required byte[] Positions { get; init; }
  internal required byte[] LogicalTracks { get; init; }
  internal required byte[] Instruments { get; init; }
  internal required byte[] Names { get; init; }
  internal required int ActualNamesOffset { get; init; }

  internal int StoredTrackCount => this.MaxTrack + (this.TrackZeroStored ? 1 : 0);
  internal int TickRateHz => checked(this.SpeedMultiplier * 50);

  internal string Title {
    get {
      var end = Array.IndexOf(this.Names, (byte)0);
      if (end < 0)
        end = this.Names.Length;
      return SanitizeMetadataText(Encoding.Latin1.GetString(this.Names, 0, end));
    }
  }

  internal static AhxModule Read(ReadOnlySpan<byte> source) {
    if (!TryRead(source, out var module, out var error))
      throw new InvalidDataException(error ?? "Invalid AHX module.");
    return module!;
  }

  internal static bool TryRead(ReadOnlySpan<byte> source, out AhxModule? module, out string? error) {
    module = null;
    error = null;

    if (source.Length < HeaderSize)
      return Fail("AHX header is truncated.", out error);
    if (!source[..3].SequenceEqual("THX"u8))
      return Fail("AHX magic must be THX.", out error);

    var version = source[3];
    if (version is not (0 or 1))
      return Fail($"AHX version {version} is not covered by the published AHX0/AHX1 format.", out error);

    var declaredNamesOffset = BinaryPrimitives.ReadUInt16BigEndian(source[4..6]);
    var headerWord = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
    var trackZeroStored = (headerWord & 0x8000) != 0;

    // The published prose describes three top-nybble bits, but the compatible AHX
    // exporter/replayer in HivelyTracker stores the defined 0..3 value in bits 6..5.
    // Bit 4 is therefore reserved in interoperable AHX0/AHX1 files.
    if ((source[6] & 0x10) != 0)
      return Fail("AHX header uses reserved timing bit 4.", out error);
    var speedCode = (source[6] >> 5) & 0x03;
    if (version == 0 && speedCode != 0)
      return Fail("AHX0 requires the 50 Hz speed code.", out error);

    var speedMultiplier = speedCode + 1;
    var positionCount = headerWord & 0x0fff;
    if (positionCount is < 1 or > 999)
      return Fail($"AHX position count {positionCount} is outside 1..999.", out error);

    var restartPosition = BinaryPrimitives.ReadUInt16BigEndian(source[8..10]);
    if (restartPosition >= positionCount)
      return Fail($"AHX restart position {restartPosition} is outside the position list.", out error);

    var trackLength = source[10];
    if (trackLength is < 1 or > 64)
      return Fail($"AHX track length {trackLength} is outside 1..64.", out error);

    var maxTrack = source[11];
    var instrumentCount = source[12];
    if (instrumentCount > 63)
      return Fail($"AHX instrument count {instrumentCount} is outside 0..63.", out error);
    var subsongCount = source[13];

    var cursor = HeaderSize;
    var subsongBytes = checked(subsongCount * 2);
    if (!Take(source, ref cursor, subsongBytes, out var subsongs))
      return Fail("AHX subsong table is truncated.", out error);
    for (var offset = 0; offset < subsongs.Length; offset += 2) {
      var start = BinaryPrimitives.ReadUInt16BigEndian(subsongs[offset..]);
      if (start >= positionCount)
        return Fail($"AHX subsong start {start} is outside the position list.", out error);
    }

    var positionBytes = checked(positionCount * Channels * 2);
    if (!Take(source, ref cursor, positionBytes, out var positions))
      return Fail("AHX position table is truncated.", out error);
    for (var offset = 0; offset < positions.Length; offset += 2)
      if (positions[offset] > maxTrack)
        return Fail($"AHX position references track {positions[offset]} above maximum track {maxTrack}.", out error);

    var trackBytesPerTrack = checked(trackLength * 3);
    var storedTrackCount = checked(maxTrack + (trackZeroStored ? 1 : 0));
    var storedTrackBytes = checked(storedTrackCount * trackBytesPerTrack);
    if (!Take(source, ref cursor, storedTrackBytes, out var storedTracks))
      return Fail("AHX track data is truncated.", out error);

    var logicalTracks = new byte[checked((maxTrack + 1) * trackBytesPerTrack)];
    if (trackZeroStored)
      storedTracks.CopyTo(logicalTracks);
    else if (!storedTracks.IsEmpty)
      storedTracks.CopyTo(logicalTracks.AsSpan(trackBytesPerTrack));

    var instrumentsStart = cursor;
    for (var i = 0; i < instrumentCount; ++i) {
      if (source.Length - cursor < InstrumentHeaderSize)
        return Fail($"AHX instrument {i + 1} header is truncated.", out error);
      var playlistLength = source[cursor + 21];
      var instrumentBytes = checked(InstrumentHeaderSize + playlistLength * 4);
      if (source.Length - cursor < instrumentBytes)
        return Fail($"AHX instrument {i + 1} playlist is truncated.", out error);
      cursor += instrumentBytes;
    }

    var actualNamesOffset = cursor;
    var instruments = source[instrumentsStart..actualNamesOffset].ToArray();
    var names = source[actualNamesOffset..].ToArray();
    if (!HasNameCount(names, instrumentCount + 1))
      return Fail($"AHX names section does not contain {instrumentCount + 1} NUL-terminated names.", out error);

    module = new AhxModule {
      Version = version,
      DeclaredNamesOffset = declaredNamesOffset,
      TrackZeroStored = trackZeroStored,
      SpeedMultiplier = speedMultiplier,
      PositionCount = positionCount,
      RestartPosition = restartPosition,
      TrackLength = trackLength,
      MaxTrack = maxTrack,
      InstrumentCount = instrumentCount,
      SubsongCount = subsongCount,
      Subsongs = subsongs.ToArray(),
      Positions = positions.ToArray(),
      LogicalTracks = logicalTracks,
      Instruments = instruments,
      Names = names,
      ActualNamesOffset = actualNamesOffset,
    };
    return true;
  }

  internal static AhxModule FromParts(
    byte version,
    int speedMultiplier,
    int restartPosition,
    int trackLength,
    int maxTrack,
    int instrumentCount,
    int subsongCount,
    bool trackZeroStored,
    byte[] subsongs,
    byte[] positions,
    byte[] logicalTracks,
    byte[] instruments,
    byte[] names
  ) {
    ArgumentNullException.ThrowIfNull(subsongs);
    ArgumentNullException.ThrowIfNull(positions);
    ArgumentNullException.ThrowIfNull(logicalTracks);
    ArgumentNullException.ThrowIfNull(instruments);
    ArgumentNullException.ThrowIfNull(names);

    if (version is not (0 or 1))
      throw new InvalidDataException("AHX version must be 0 or 1.");
    if (speedMultiplier is < 1 or > 4)
      throw new InvalidDataException("AHX speed multiplier must be 1..4.");
    if (version == 0 && speedMultiplier != 1)
      throw new InvalidDataException("AHX0 supports only the 1x / 50 Hz timing mode.");
    if (trackLength is < 1 or > 64)
      throw new InvalidDataException("AHX track length must be 1..64.");
    if (maxTrack is < 0 or > 255)
      throw new InvalidDataException("AHX maximum track number must be 0..255.");
    if (instrumentCount is < 0 or > 63)
      throw new InvalidDataException("AHX instrument count must be 0..63.");
    if (subsongCount is < 0 or > 255)
      throw new InvalidDataException("AHX subsong count must be 0..255.");
    if (subsongs.Length != checked(subsongCount * 2))
      throw new InvalidDataException("subsongs.bin length does not match the metadata subsong count.");
    if (positions.Length == 0 || positions.Length % (Channels * 2) != 0)
      throw new InvalidDataException("positions.bin must contain one or more complete 8-byte positions.");

    var positionCount = positions.Length / (Channels * 2);
    if (positionCount > 999)
      throw new InvalidDataException("AHX supports at most 999 positions.");
    if (restartPosition < 0 || restartPosition >= positionCount)
      throw new InvalidDataException("AHX restart position is outside the position list.");
    for (var offset = 0; offset < subsongs.Length; offset += 2)
      if (BinaryPrimitives.ReadUInt16BigEndian(subsongs.AsSpan(offset, 2)) >= positionCount)
        throw new InvalidDataException("A subsong start is outside the position list.");
    for (var offset = 0; offset < positions.Length; offset += 2)
      if (positions[offset] > maxTrack)
        throw new InvalidDataException("A position references a track above the declared maximum track.");

    var logicalTrackBytes = checked((maxTrack + 1) * trackLength * 3);
    if (logicalTracks.Length != logicalTrackBytes)
      throw new InvalidDataException("tracks.bin must contain every logical track from 0 through max_track.");
    ValidateInstrumentBlob(instruments, instrumentCount);
    if (!HasNameCount(names, instrumentCount + 1))
      throw new InvalidDataException("names.bin does not contain the title plus every instrument name.");

    return new AhxModule {
      Version = version,
      DeclaredNamesOffset = 0,
      TrackZeroStored = trackZeroStored,
      SpeedMultiplier = speedMultiplier,
      PositionCount = positionCount,
      RestartPosition = restartPosition,
      TrackLength = trackLength,
      MaxTrack = maxTrack,
      InstrumentCount = instrumentCount,
      SubsongCount = subsongCount,
      Subsongs = subsongs,
      Positions = positions,
      LogicalTracks = logicalTracks,
      Instruments = instruments,
      Names = names,
      ActualNamesOffset = 0,
    };
  }

  internal byte[] Write(byte version, int speedMultiplier, bool storeTrackZero) {
    ValidateWriteCombination(version, speedMultiplier, storeTrackZero);

    var trackBytesPerTrack = checked(this.TrackLength * 3);
    var storedTrackBytes = checked((this.MaxTrack + (storeTrackZero ? 1 : 0)) * trackBytesPerTrack);
    var namesOffset = checked(HeaderSize + this.Subsongs.Length + this.Positions.Length + storedTrackBytes + this.Instruments.Length);
    var totalLength = checked(namesOffset + this.Names.Length);
    var output = new byte[totalLength];

    "THX"u8.CopyTo(output);
    output[3] = version;
    BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), unchecked((ushort)namesOffset));
    var speedCode = speedMultiplier - 1;
    output[6] = (byte)((storeTrackZero ? 0x80 : 0) | (speedCode << 5) | ((this.PositionCount >> 8) & 0x0f));
    output[7] = (byte)this.PositionCount;
    BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), checked((ushort)this.RestartPosition));
    output[10] = checked((byte)this.TrackLength);
    output[11] = checked((byte)this.MaxTrack);
    output[12] = checked((byte)this.InstrumentCount);
    output[13] = checked((byte)this.SubsongCount);

    var cursor = HeaderSize;
    this.Subsongs.CopyTo(output, cursor);
    cursor += this.Subsongs.Length;
    this.Positions.CopyTo(output, cursor);
    cursor += this.Positions.Length;

    if (storeTrackZero) {
      this.LogicalTracks.CopyTo(output, cursor);
      cursor += this.LogicalTracks.Length;
    } else {
      var withoutTrackZero = this.LogicalTracks.AsSpan(trackBytesPerTrack);
      withoutTrackZero.CopyTo(output.AsSpan(cursor));
      cursor += withoutTrackZero.Length;
    }

    this.Instruments.CopyTo(output, cursor);
    cursor += this.Instruments.Length;
    this.Names.CopyTo(output, cursor);
    return output;
  }

  internal void ValidateWriteCombination(byte version, int speedMultiplier, bool storeTrackZero) {
    if (version is not (0 or 1))
      throw new InvalidDataException("AHX output version must be AHX0 or AHX1.");
    if (speedMultiplier is < 1 or > 4)
      throw new InvalidDataException("AHX speed multiplier must be 1..4.");
    if (version == 0 && speedMultiplier != 1)
      throw new InvalidDataException("AHX0 supports only the 1x / 50 Hz timing mode.");
    if (!storeTrackZero && !this.IsTrackZeroEmpty())
      throw new InvalidDataException("Track 0 can be omitted only when every row is empty.");
    if (version == 0)
      this.ValidateAhx0Compatibility();
  }

  internal bool IsTrackZeroEmpty() {
    var length = checked(this.TrackLength * 3);
    foreach (var value in this.LogicalTracks.AsSpan(0, length))
      if (value != 0)
        return false;
    return true;
  }

  private void ValidateAhx0Compatibility() {
    for (var offset = 0; offset < this.LogicalTracks.Length; offset += 3) {
      var command = this.LogicalTracks[offset + 1] & 0x0f;
      var data = this.LogicalTracks[offset + 2];
      if (command == 4)
        throw new InvalidDataException("AHX1 command 4 prevents conversion to AHX0.");
      if (command == 0x0d && data != 0)
        throw new InvalidDataException("AHX0 permits command D only with data 00.");
    }

    var cursor = 0;
    for (var instrument = 0; instrument < this.InstrumentCount; ++instrument) {
      var header = this.Instruments.AsSpan(cursor, InstrumentHeaderSize);
      if ((header[1] & 0xf8) != 0 || header[12] != 0 || (header[14] & 0xf0) != 0 || header[19] != 0)
        throw new InvalidDataException($"Instrument {instrument + 1} uses AHX1-only filter/hard-cut fields.");

      var playlistLength = header[21];
      cursor += InstrumentHeaderSize;
      for (var row = 0; row < playlistLength; ++row, cursor += 4) {
        var value = BinaryPrimitives.ReadUInt32BigEndian(this.Instruments.AsSpan(cursor, 4));
        var effect2 = (int)((value >> 29) & 0x07);
        var effect1 = (int)((value >> 26) & 0x07);
        var data1 = (byte)(value >> 8);
        var data2 = (byte)value;
        if (((effect1 is 0 or 4) && data1 != 0) || ((effect2 is 0 or 4) && data2 != 0))
          throw new InvalidDataException($"Instrument {instrument + 1} playlist uses an AHX1-only filter/modulation effect.");
      }
    }
  }

  private static void ValidateInstrumentBlob(ReadOnlySpan<byte> data, int count) {
    var cursor = 0;
    for (var i = 0; i < count; ++i) {
      if (data.Length - cursor < InstrumentHeaderSize)
        throw new InvalidDataException($"instruments.bin is truncated at instrument {i + 1}.");
      var playlistLength = data[cursor + 21];
      var size = checked(InstrumentHeaderSize + playlistLength * 4);
      if (data.Length - cursor < size)
        throw new InvalidDataException($"instruments.bin is truncated in instrument {i + 1}'s playlist.");
      cursor += size;
    }
    if (cursor != data.Length)
      throw new InvalidDataException("instruments.bin contains trailing bytes beyond the declared instruments.");
  }

  private static bool Take(ReadOnlySpan<byte> source, ref int cursor, int count, out ReadOnlySpan<byte> value) {
    if (count < 0 || cursor < 0 || source.Length - cursor < count) {
      value = default;
      return false;
    }
    value = source.Slice(cursor, count);
    cursor += count;
    return true;
  }

  private static bool HasNameCount(ReadOnlySpan<byte> names, int requiredCount) {
    var count = 0;
    foreach (var value in names)
      if (value == 0 && ++count >= requiredCount)
        return true;
    return requiredCount == 0;
  }

  private static bool Fail(string message, out string? error) {
    error = message;
    return false;
  }

  private static string SanitizeMetadataText(string value) {
    var builder = new StringBuilder(value.Length);
    foreach (var c in value)
      builder.Append(c is '\r' or '\n' ? ' ' : c);
    return builder.ToString().Trim();
  }
}
