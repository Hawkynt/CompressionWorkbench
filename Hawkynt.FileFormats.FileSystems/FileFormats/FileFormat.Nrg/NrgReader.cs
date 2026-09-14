using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nrg;

/// <summary>
/// Reads Nero Burning ROM NRG disc images, including DAO/TAO session and track topology,
/// raw CD-DA/data tracks, and ISO 9660 file trees from every readable data track.
/// </summary>
public sealed class NrgReader : IDisposable {
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int RawSectorWithSubchannelSize = 2448;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;
  private const int MaxCdTextBytes = 4 * 1024 * 1024;
  private const int MaxCueEntries = 256;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly Dictionary<(int Session, int Track), NrgTrackInfo> _tracksByNumber;
  private bool _disposed;

  private readonly record struct FooterInfo(int Version, long TrailerOffset, long FooterOffset);
  private readonly record struct ChunkRef(long PayloadStart, uint PayloadLength);
  private readonly record struct CueEntry(byte AdrCtl, int TrackNumber, byte Index, int Lba);
  private sealed record ParsedDescriptor(List<NrgSessionInfo> Sessions, byte[] CdText);

  /// <summary>Gets the NRG format version detected from the footer (1 or 2), or 0 if no valid footer was found.</summary>
  public int Version { get; }

  /// <summary>Gets all reconstructed sessions in physical disc order.</summary>
  public IReadOnlyList<NrgSessionInfo> Sessions { get; }

  /// <summary>Gets all reconstructed tracks, flattened in physical order.</summary>
  public IReadOnlyList<NrgTrackInfo> Tracks { get; }

  /// <summary>Gets the raw CD-TEXT pack bytes, or an empty memory when no CDTX chunk was present.</summary>
  public ReadOnlyMemory<byte> CdText { get; }

  /// <summary>
  /// Gets the archive view. The first ISO 9660 tree remains at the archive root for compatibility;
  /// later ISO trees are namespaced below <c>.nrg/session-XX/track-YY/iso</c>. Audio and non-ISO
  /// data tracks are exposed as exact stored raw-track files below <c>.nrg/session-XX/tracks</c>.
  /// </summary>
  public IReadOnlyList<NrgEntry> Entries { get; }

  /// <summary>Initializes a new <see cref="NrgReader"/> from an NRG stream.</summary>
  public NrgReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("NRG reading requires a readable, seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;

    var footer = ReadFooter(stream);
    this.Version = footer.Version;

    var parsed = ReadDescriptor(stream, footer);
    if (parsed.Sessions.Count == 0 && TryCreateLegacyIsoSession(stream, footer, out var legacySession))
      parsed.Sessions.Add(legacySession);

    var sessions = parsed.Sessions.ToArray();
    var tracks = sessions.SelectMany(static session => session.Tracks).ToArray();
    foreach (var track in tracks) {
      track.HasIso9660 = CanContainIsoUserData(track) && TryProbe(stream, track);
      if (track.HasIso9660)
        track.IsoExtentLbaBase = DetectIsoExtentLbaBase(stream, track);
    }

    this.Sessions = sessions;
    this.Tracks = tracks;
    this.CdText = parsed.CdText;
    this._tracksByNumber = tracks.ToDictionary(static track => (track.SessionNumber, track.TrackNumber));

    var entries = new List<NrgEntry>();
    var firstIso = true;
    foreach (var track in tracks) {
      if (track.HasIso9660) {
        var prefix = firstIso ? "" : $".nrg/session-{track.SessionNumber:D2}/track-{track.TrackNumber:D2}/iso";
        TryParseIso9660(track, prefix, entries);
        firstIso = false;
      } else {
        entries.Add(CreateRawTrackEntry(track));
      }
    }

    this.Entries = entries;
  }

  /// <summary>Extracts one archive-view entry.</summary>
  public byte[] Extract(NrgEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new ArgumentException("Cannot extract a directory entry.", nameof(entry));
    if (entry.Size == 0)
      return [];
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("NRG entries larger than 2 GiB require a streaming extraction API.");

    var track = ResolveTrack(entry.SessionNumber, entry.TrackNumber);
    return entry.Kind == NrgEntryKind.RawTrack
      ? ReadTrackData(track, checked((int)entry.Size))
      : ReadFileData(track, entry.StartLba, checked((int)entry.Size));
  }

  /// <summary>Extracts the exact stored index-1/program bytes of one track.</summary>
  public byte[] ExtractTrack(NrgTrackInfo track) {
    ArgumentNullException.ThrowIfNull(track);
    var resolved = ResolveTrack(track.SessionNumber, track.TrackNumber);
    if (resolved.StoredLength > int.MaxValue)
      throw new NotSupportedException("NRG tracks larger than 2 GiB require CopyTrackTo for streaming extraction.");
    return ReadTrackData(resolved, checked((int)resolved.StoredLength));
  }

  /// <summary>
  /// Copies one track without transcoding. CD-DA therefore remains 2,352-byte raw audio sectors
  /// (or 2,448-byte sectors when subchannel data is stored). Set <paramref name="includePregap"/>
  /// to include the stored index-0/pregap bytes as well.
  /// </summary>
  public void CopyTrackTo(NrgTrackInfo track, Stream output, bool includePregap = false) {
    ArgumentNullException.ThrowIfNull(track);
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite)
      throw new ArgumentException("Track extraction requires a writable output stream.", nameof(output));

    var resolved = ResolveTrack(track.SessionNumber, track.TrackNumber);
    var start = includePregap ? resolved.PregapOffset : resolved.DataOffset;
    CopyRange(this._stream, output, start, checked(resolved.EndOffset - start));
  }

  private NrgTrackInfo ResolveTrack(int? sessionNumber, int? trackNumber) {
    if (sessionNumber is null || trackNumber is null ||
        !this._tracksByNumber.TryGetValue((sessionNumber.Value, trackNumber.Value), out var track))
      throw new InvalidDataException("The NRG entry does not refer to a track in this image.");
    return track;
  }

  private static FooterInfo ReadFooter(Stream stream) {
    if (stream.Length >= 12) {
      stream.Position = stream.Length - 12;
      Span<byte> footer = stackalloc byte[12];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NER5"u8)) {
        var offset = BinaryPrimitives.ReadUInt64BigEndian(footer[4..]);
        var footerOffset = stream.Length - 12;
        if (offset <= (ulong)footerOffset)
          return new(2, checked((long)offset), footerOffset);
      }
    }

    if (stream.Length >= 8) {
      stream.Position = stream.Length - 8;
      Span<byte> footer = stackalloc byte[8];
      if (ReadExactly(stream, footer) && footer[..4].SequenceEqual("NERO"u8)) {
        var offset = BinaryPrimitives.ReadUInt32BigEndian(footer[4..]);
        var footerOffset = stream.Length - 8;
        if (offset <= footerOffset)
          return new(1, offset, footerOffset);
      }
    }

    return new(0, stream.Length, stream.Length);
  }

  private static ParsedDescriptor ReadDescriptor(Stream stream, FooterInfo footer) {
    var sessions = new List<NrgSessionInfo>();
    var cdText = Array.Empty<byte>();
    if (footer.Version == 0 || footer.TrailerOffset >= footer.FooterOffset)
      return new(sessions, cdText);

    var cueChunks = new List<ChunkRef>();
    var daoChunks = new List<ChunkRef>();
    var etnChunks = new List<ChunkRef>();
    var oldFormat = footer.Version == 1;
    var position = footer.TrailerOffset;
    Span<byte> header = stackalloc byte[8];

    while (position <= footer.FooterOffset - header.Length) {
      stream.Position = position;
      if (!ReadExactly(stream, header))
        break;

      var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
      var payloadStart = position + header.Length;
      if (payloadStart > footer.FooterOffset || (long)payloadLength > footer.FooterOffset - payloadStart)
        break;
      var payloadEnd = payloadStart + payloadLength;
      var chunk = new ChunkRef(payloadStart, payloadLength);

      if ((oldFormat && header[..4].SequenceEqual("CUES"u8)) ||
          (!oldFormat && header[..4].SequenceEqual("CUEX"u8))) {
        cueChunks.Add(chunk);
      } else if ((oldFormat && header[..4].SequenceEqual("DAOI"u8)) ||
                 (!oldFormat && header[..4].SequenceEqual("DAOX"u8))) {
        daoChunks.Add(chunk);
      } else if ((oldFormat && header[..4].SequenceEqual("ETNF"u8)) ||
                 (!oldFormat && header[..4].SequenceEqual("ETN2"u8))) {
        etnChunks.Add(chunk);
      } else if (header[..4].SequenceEqual("CDTX"u8) && payloadLength <= MaxCdTextBytes) {
        cdText = new byte[checked((int)payloadLength)];
        stream.Position = payloadStart;
        if (!ReadExactly(stream, cdText))
          cdText = Array.Empty<byte>();
      }

      position = payloadEnd;
      if (header[..4].SequenceEqual("END!"u8))
        break;
    }

    var nextTrackNumber = 1;
    for (var ordinal = 0; ; ++ordinal) {
      NrgSessionInfo? session;
      if (ordinal < cueChunks.Count) {
        if (ordinal >= daoChunks.Count)
          break;

        var cueChunk = cueChunks[ordinal];
        var daoChunk = daoChunks[ordinal];
        var cue = ReadCue(stream, cueChunk.PayloadStart, cueChunk.PayloadLength, oldFormat);
        session = ReadDaoSession(
          stream, daoChunk.PayloadStart, daoChunk.PayloadLength,
          isV2: !oldFormat,
          hardEnd: footer.TrailerOffset,
          sessionNumber: ordinal + 1,
          fallbackFirstTrack: nextTrackNumber,
          cue: cue);
      } else if (ordinal < etnChunks.Count) {
        var etnChunk = etnChunks[ordinal];
        session = ReadEtnSession(
          stream, etnChunk.PayloadStart, etnChunk.PayloadLength,
          isV2: !oldFormat,
          hardEnd: footer.TrailerOffset,
          sessionNumber: ordinal + 1,
          firstTrackNumber: nextTrackNumber);
      } else {
        break;
      }

      if (session is null)
        break;

      sessions.Add(session);
      if (session.Tracks.Count != 0)
        nextTrackNumber = session.Tracks[^1].TrackNumber + 1;
    }

    return new(sessions, cdText);
  }

  private static List<CueEntry> ReadCue(Stream stream, long payloadStart, uint payloadLength, bool oldFormat) {
    const int recordSize = 8;
    var count = Math.Min(checked((int)(payloadLength / recordSize)), MaxCueEntries);
    var result = new List<CueEntry>(count);
    Span<byte> record = stackalloc byte[recordSize];
    for (var entryIndex = 0; entryIndex < count; ++entryIndex) {
      stream.Position = payloadStart + (long)entryIndex * recordSize;
      if (!ReadExactly(stream, record))
        break;

      var rawTrack = record[1];
      var trackNumber = rawTrack switch {
        0xAA => -1,
        _ => FromBcd(rawTrack),
      };
      var index = FromBcd(record[2]);
      if (trackNumber < -1 || index < 0)
        continue;

      int lba;
      if (oldFormat) {
        var minute = record[5];
        var second = record[6];
        var frame = record[7];
        if (second >= 60 || frame >= 75)
          continue;
        lba = ((minute * 60 + second) * 75 + frame) - 150;
      } else {
        lba = BinaryPrimitives.ReadInt32BigEndian(record[4..]);
      }

      result.Add(new(record[0], trackNumber, checked((byte)index), lba));
    }
    return result;
  }

  private static NrgSessionInfo? ReadDaoSession(
      Stream stream,
      long payloadStart,
      uint payloadLength,
      bool isV2,
      long hardEnd,
      int sessionNumber,
      int fallbackFirstTrack,
      IReadOnlyList<CueEntry>? cue) {
    const int headerSize = 22;
    var recordSize = isV2 ? 42 : 30;
    if (payloadLength < headerSize)
      return null;

    Span<byte> header = stackalloc byte[headerSize];
    stream.Position = payloadStart;
    if (!ReadExactly(stream, header))
      return null;

    var firstTrack = header[20] is >= 1 and <= 99 ? header[20] : fallbackFirstTrack;
    var declaredLastTrack = header[21];
    var recordCount = checked((int)(((long)payloadLength - headerSize) / recordSize));
    if (declaredLastTrack is >= 1 and <= 99 && declaredLastTrack >= firstTrack)
      recordCount = Math.Min(recordCount, declaredLastTrack - firstTrack + 1);
    recordCount = Math.Min(recordCount, 99 - firstTrack + 1);
    if (recordCount <= 0)
      return null;

    var tracks = new List<NrgTrackInfo>(recordCount);
    var record = new byte[recordSize];
    for (var recordIndex = 0; recordIndex < recordCount; ++recordIndex) {
      stream.Position = payloadStart + headerSize + (long)recordIndex * recordSize;
      if (!ReadExactly(stream, record))
        break;

      var span = record.AsSpan();
      var declaredSectorSize = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(12, 2));
      var mode = span[14];
      if (!TryGetSectorGeometry(mode, declaredSectorSize, out var sectorSize, out var userDataOffset))
        continue;

      long pregapOffset;
      long dataOffset;
      long endOffset;
      if (isV2) {
        pregapOffset = BinaryPrimitives.ReadInt64BigEndian(span.Slice(18, 8));
        dataOffset = BinaryPrimitives.ReadInt64BigEndian(span.Slice(26, 8));
        endOffset = BinaryPrimitives.ReadInt64BigEndian(span.Slice(34, 8));
      } else {
        pregapOffset = BinaryPrimitives.ReadInt32BigEndian(span.Slice(18, 4));
        dataOffset = BinaryPrimitives.ReadInt32BigEndian(span.Slice(22, 4));
        endOffset = BinaryPrimitives.ReadInt32BigEndian(span.Slice(26, 4));
      }

      if (pregapOffset < 0 || pregapOffset > dataOffset || dataOffset > endOffset || endOffset > hardEnd)
        continue;

      var trackNumber = firstTrack + recordIndex;
      var index0 = FindCue(cue, trackNumber, 0);
      var index1 = FindCue(cue, trackNumber, 1);
      tracks.Add(new NrgTrackInfo {
        SessionNumber = sessionNumber,
        TrackNumber = trackNumber,
        ModeCode = mode,
        SectorSize = sectorSize,
        UserDataOffset = userDataOffset,
        PregapOffset = pregapOffset,
        DataOffset = dataOffset,
        EndOffset = endOffset,
        Index0Lba = index0?.Lba,
        Index1Lba = index1?.Lba ?? index0?.Lba,
        AdrCtl = index1?.AdrCtl ?? index0?.AdrCtl,
        Isrc = ReadAsciiField(span[..12]),
      });
    }

    if (tracks.Count == 0)
      return null;

    var leadIn = FindCue(cue, 0, 0)?.Lba;
    var leadOut = FindCue(cue, -1, 1)?.Lba ?? DeriveLeadOut(tracks[^1]);
    return new NrgSessionInfo {
      SessionNumber = sessionNumber,
      LeadInLba = leadIn,
      LeadOutLba = leadOut,
      Mcn = ReadAsciiField(header.Slice(4, 13)),
      Tracks = tracks.ToArray(),
    };
  }

  private static NrgSessionInfo? ReadEtnSession(
      Stream stream,
      long payloadStart,
      uint payloadLength,
      bool isV2,
      long hardEnd,
      int sessionNumber,
      int firstTrackNumber) {
    var recordSize = isV2 ? 32 : 20;
    var recordCount = Math.Min(checked((int)(payloadLength / recordSize)), 99 - firstTrackNumber + 1);
    if (recordCount <= 0)
      return null;

    var tracks = new List<NrgTrackInfo>(recordCount);
    var record = new byte[recordSize];
    for (var recordIndex = 0; recordIndex < recordCount; ++recordIndex) {
      stream.Position = payloadStart + (long)recordIndex * recordSize;
      if (!ReadExactly(stream, record))
        break;

      var span = record.AsSpan();
      long offset;
      long length;
      byte mode;
      int lba;
      if (isV2) {
        offset = BinaryPrimitives.ReadInt64BigEndian(span);
        length = BinaryPrimitives.ReadInt64BigEndian(span[8..]);
        mode = span[19];
        lba = BinaryPrimitives.ReadInt32BigEndian(span[20..]);
      } else {
        offset = BinaryPrimitives.ReadInt32BigEndian(span);
        length = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
        mode = span[11];
        lba = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
      }

      if (length < 0 || offset < 0 || offset > hardEnd || length > hardEnd - offset)
        continue;
      if (!TryGetSectorGeometry(mode, declaredSectorSize: 0, out var sectorSize, out var userDataOffset))
        continue;

      tracks.Add(new NrgTrackInfo {
        SessionNumber = sessionNumber,
        TrackNumber = firstTrackNumber + recordIndex,
        ModeCode = mode,
        SectorSize = sectorSize,
        UserDataOffset = userDataOffset,
        PregapOffset = offset,
        DataOffset = offset,
        EndOffset = offset + length,
        Index0Lba = lba,
        Index1Lba = lba,
      });
    }

    if (tracks.Count == 0)
      return null;

    return new NrgSessionInfo {
      SessionNumber = sessionNumber,
      LeadOutLba = DeriveLeadOut(tracks[^1]),
      Tracks = tracks.ToArray(),
    };
  }

  private static CueEntry? FindCue(IReadOnlyList<CueEntry>? cue, int trackNumber, byte index) {
    if (cue is null)
      return null;
    for (var i = cue.Count - 1; i >= 0; --i)
      if (cue[i].TrackNumber == trackNumber && cue[i].Index == index)
        return cue[i];
    return null;
  }

  private static int? DeriveLeadOut(NrgTrackInfo track) {
    if (track.Index1Lba is not { } index1 || track.SectorSize <= 0)
      return null;
    var sectors = track.StoredLength / track.SectorSize;
    var result = (long)index1 + sectors;
    return result is >= int.MinValue and <= int.MaxValue ? (int)result : null;
  }

  private static bool TryCreateLegacyIsoSession(Stream stream, FooterInfo footer, out NrgSessionInfo session) {
    session = null!;
    var end = footer.Version != 0 ? footer.TrailerOffset : stream.Length;
    if (!TryDetectSectorGeometry(stream, 0, end, out var sectorSize, out var userDataOffset, out var modeCode))
      return false;

    var track = new NrgTrackInfo {
      SessionNumber = 1,
      TrackNumber = 1,
      ModeCode = modeCode,
      SectorSize = sectorSize,
      UserDataOffset = userDataOffset,
      PregapOffset = 0,
      DataOffset = 0,
      EndOffset = end,
      Index0Lba = 0,
      Index1Lba = 0,
      HasIso9660 = true,
    };
    session = new NrgSessionInfo {
      SessionNumber = 1,
      LeadOutLba = DeriveLeadOut(track),
      Tracks = [track],
    };
    return true;
  }

  private static bool TryDetectSectorGeometry(
      Stream stream,
      long trackOffset,
      long dataEnd,
      out int sectorSize,
      out int userDataOffset,
      out byte modeCode) {
    if (TryProbe(stream, trackOffset, RawSectorSize, Mode1DataOffset, dataEnd)) {
      (sectorSize, userDataOffset, modeCode) = (RawSectorSize, Mode1DataOffset, (byte)NrgTrackMode.Mode1Raw);
      return true;
    }
    if (TryProbe(stream, trackOffset, RawSectorSize, Mode2Form1DataOffset, dataEnd)) {
      (sectorSize, userDataOffset, modeCode) = (RawSectorSize, Mode2Form1DataOffset, (byte)NrgTrackMode.Mode2Raw);
      return true;
    }
    if (TryProbe(stream, trackOffset, SectorSize2336, 8, dataEnd)) {
      (sectorSize, userDataOffset, modeCode) = (SectorSize2336, 8, (byte)NrgTrackMode.Mode2Form2);
      return true;
    }
    if (TryProbe(stream, trackOffset, Iso9660SectorSize, 0, dataEnd)) {
      (sectorSize, userDataOffset, modeCode) = (Iso9660SectorSize, 0, (byte)NrgTrackMode.Mode1);
      return true;
    }

    sectorSize = 0;
    userDataOffset = 0;
    modeCode = 0;
    return false;
  }

  private static bool TryGetSectorGeometry(byte mode, int declaredSectorSize, out int sectorSize, out int userDataOffset) {
    (sectorSize, userDataOffset) = mode switch {
      0x00 or 0x02 => (Iso9660SectorSize, 0),
      0x03 => (SectorSize2336, 8),
      0x05 => (RawSectorSize, Mode1DataOffset),
      0x06 => (RawSectorSize, Mode2Form1DataOffset),
      0x07 => (RawSectorSize, 0),
      0x0F => (RawSectorWithSubchannelSize, Mode1DataOffset),
      0x10 => (RawSectorWithSubchannelSize, 0),
      0x11 => (RawSectorWithSubchannelSize, Mode2Form1DataOffset),
      _ => declaredSectorSize switch {
        Iso9660SectorSize or SectorSize2336 or RawSectorSize or RawSectorWithSubchannelSize => (declaredSectorSize, 0),
        _ => (0, 0),
      },
    };
    return sectorSize != 0;
  }

  private static bool CanContainIsoUserData(NrgTrackInfo track)
    => !track.IsAudio && track.UserDataOffset >= 0 && track.UserDataOffset <= track.SectorSize - Iso9660SectorSize;

  private static bool TryProbe(Stream stream, NrgTrackInfo track)
    => TryProbe(stream, track.DataOffset, track.SectorSize, track.UserDataOffset, track.EndOffset);

  private static bool TryProbe(Stream stream, long trackOffset, int sectorSize, int userDataOffset, long dataEnd) {
    if (trackOffset < 0 || sectorSize <= 0 || userDataOffset < 0 || userDataOffset > sectorSize - Iso9660SectorSize)
      return false;
    if (PvdLba > (long.MaxValue - trackOffset - userDataOffset) / sectorSize)
      return false;
    var pvdPosition = trackOffset + (long)PvdLba * sectorSize + userDataOffset;
    if (pvdPosition < trackOffset || dataEnd < 6 || pvdPosition > dataEnd - 6)
      return false;

    Span<byte> signature = stackalloc byte[6];
    stream.Position = pvdPosition;
    return ReadExactly(stream, signature) &&
           signature[0] == 1 && signature[1..].SequenceEqual("CD001"u8);
  }

  private static int DetectIsoExtentLbaBase(Stream stream, NrgTrackInfo track) {
    if (track.Index1Lba is not { } baseLba || baseLba <= 0)
      return 0;

    var pvd = ReadSectorRelative(stream, track, PvdLba);
    if (pvd is null || pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
      return 0;

    var rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
    if (rootExtent > int.MaxValue || rootExtent < baseLba)
      return 0;

    var absoluteRelative = (long)rootExtent - baseLba;
    return LooksLikeRootDirectory(stream, track, absoluteRelative, rootExtent) ? baseLba : 0;
  }

  private static bool LooksLikeRootDirectory(Stream stream, NrgTrackInfo track, long relativeLba, uint expectedExtent) {
    var sector = ReadSectorRelative(stream, track, relativeLba);
    if (sector is null || sector[0] < 34)
      return false;

    var recordLength = sector[0];
    if (recordLength > sector.Length)
      return false;
    var record = sector.AsSpan(0, recordLength);
    if (record[32] != 1 || record[33] != 0)
      return false;

    return BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4)) == expectedExtent &&
           BinaryPrimitives.ReadUInt32BigEndian(record.Slice(6, 4)) == expectedExtent;
  }

  private void TryParseIso9660(NrgTrackInfo track, string prefix, List<NrgEntry> entries) {
    var pvd = ReadSectorRelative(this._stream, track, PvdLba);
    if (pvd is null || pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
      return;

    var rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
    var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
    if (rootLba > int.MaxValue || rootSize > int.MaxValue)
      return;

    WalkDirectory(track, (int)rootLba, (int)rootSize, prefix, entries, []);
  }

  private void WalkDirectory(
      NrgTrackInfo track,
      int dirLba,
      int dirSize,
      string parentPath,
      List<NrgEntry> entries,
      HashSet<int> visited) {
    if (dirLba < 0 || dirSize <= 0 || !visited.Add(dirLba))
      return;

    var bytesRead = 0;
    var currentLba = dirLba;
    var bufferOffset = 0;
    byte[]? sector = null;

    while (bytesRead < dirSize) {
      if (sector is null || bufferOffset >= Iso9660SectorSize) {
        sector = ReadSector(track, currentLba);
        if (sector is null)
          return;
        if (currentLba == int.MaxValue)
          return;
        ++currentLba;
        bufferOffset = 0;
      }

      var recordLength = sector[bufferOffset];
      if (recordLength == 0) {
        var remaining = Iso9660SectorSize - bufferOffset;
        bytesRead += remaining;
        bufferOffset = Iso9660SectorSize;
        continue;
      }

      if (recordLength < 34 || bufferOffset + recordLength > Iso9660SectorSize) {
        var remaining = Iso9660SectorSize - bufferOffset;
        bytesRead += remaining;
        bufferOffset = Iso9660SectorSize;
        continue;
      }

      var record = sector.AsSpan(bufferOffset, recordLength);
      ParseDirectoryRecord(track, record, parentPath, entries, visited);
      bytesRead += recordLength;
      bufferOffset += recordLength;
    }
  }

  private void ParseDirectoryRecord(
      NrgTrackInfo track,
      ReadOnlySpan<byte> record,
      string parentPath,
      List<NrgEntry> entries,
      HashSet<int> visited) {
    var idLength = record[32];
    if (idLength == 0 || 33 + idLength > record.Length)
      return;
    if (idLength == 1 && (record[33] == 0x00 || record[33] == 0x01))
      return;

    var dataLba = BinaryPrimitives.ReadUInt32LittleEndian(record[2..6]);
    var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(record[10..14]);
    if (dataLba > int.MaxValue || dataLength > int.MaxValue)
      return;

    var isDirectory = (record[25] & 0x02) != 0;
    var rawName = Encoding.ASCII.GetString(record.Slice(33, idLength));
    var name = StripVersionSuffix(rawName);
    if (string.IsNullOrEmpty(name))
      return;

    var fullPath = parentPath.Length > 0 ? $"{parentPath}/{name}" : name;
    entries.Add(new NrgEntry {
      Name = name,
      FullPath = fullPath,
      IsDirectory = isDirectory,
      Kind = isDirectory ? NrgEntryKind.IsoDirectory : NrgEntryKind.IsoFile,
      Size = isDirectory ? 0 : dataLength,
      StartLba = (int)dataLba,
      SessionNumber = track.SessionNumber,
      TrackNumber = track.TrackNumber,
    });

    if (isDirectory)
      WalkDirectory(track, (int)dataLba, (int)dataLength, fullPath, entries, visited);
  }

  private static NrgEntry CreateRawTrackEntry(NrgTrackInfo track) {
    var kind = track.IsAudio ? "audio" : "data";
    var subchannel = track.HasSubchannel ? "-subchannel" : "";
    var name = $"track-{track.TrackNumber:D2}-{kind}{subchannel}.bin";
    return new NrgEntry {
      Name = name,
      FullPath = $".nrg/session-{track.SessionNumber:D2}/tracks/{name}",
      Kind = NrgEntryKind.RawTrack,
      Size = track.StoredLength,
      StartLba = track.Index1Lba ?? 0,
      SessionNumber = track.SessionNumber,
      TrackNumber = track.TrackNumber,
    };
  }

  private byte[] ReadTrackData(NrgTrackInfo track, int size) {
    if (size < 0 || size > track.StoredLength)
      throw new InvalidDataException("NRG raw-track entry exceeds its descriptor bounds.");
    var result = new byte[size];
    this._stream.Position = track.DataOffset;
    if (!ReadExactly(this._stream, result))
      throw new InvalidDataException("NRG track is truncated.");
    return result;
  }

  private byte[] ReadFileData(NrgTrackInfo track, int startLba, int size) {
    var result = new byte[size];
    var written = 0;
    var lba = startLba;
    while (written < size) {
      var sector = ReadSector(track, lba);
      if (sector is null)
        throw new InvalidDataException("NRG file extent runs past its data track.");

      var toCopy = Math.Min(Iso9660SectorSize, size - written);
      sector.AsSpan(0, toCopy).CopyTo(result.AsSpan(written));
      written += toCopy;
      if (written < size) {
        if (lba == int.MaxValue)
          throw new InvalidDataException("NRG file extent LBA overflowed.");
        ++lba;
      }
    }
    return result;
  }

  private byte[]? ReadSector(NrgTrackInfo track, int isoLba) {
    if (!TryNormalizeIsoLba(track, isoLba, out var relativeLba))
      return null;
    return ReadSectorRelative(this._stream, track, relativeLba);
  }

  private static byte[]? ReadSectorRelative(Stream stream, NrgTrackInfo track, long relativeLba) {
    if (relativeLba < 0 || relativeLba >= track.SectorCount || track.SectorSize <= 0)
      return null;
    if (relativeLba > (long.MaxValue - track.DataOffset) / track.SectorSize)
      return null;

    var sectorStart = track.DataOffset + relativeLba * track.SectorSize;
    if (track.UserDataOffset > long.MaxValue - sectorStart)
      return null;
    var dataStart = sectorStart + track.UserDataOffset;
    if (sectorStart < track.DataOffset || dataStart < sectorStart || dataStart > track.EndOffset - Iso9660SectorSize)
      return null;

    stream.Position = dataStart;
    var buffer = new byte[Iso9660SectorSize];
    return ReadExactly(stream, buffer) ? buffer : null;
  }

  private static bool TryNormalizeIsoLba(NrgTrackInfo track, int isoLba, out long relativeLba) {
    relativeLba = (long)isoLba - track.IsoExtentLbaBase;
    return relativeLba >= 0 && relativeLba < track.SectorCount;
  }

  private static void CopyRange(Stream input, Stream output, long offset, long length) {
    if (offset < 0 || length < 0 || offset > input.Length || length > input.Length - offset)
      throw new InvalidDataException("NRG track range exceeds the source stream.");

    input.Position = offset;
    var buffer = new byte[128 * 1024];
    while (length > 0) {
      var requested = (int)Math.Min(length, buffer.Length);
      var read = input.Read(buffer, 0, requested);
      if (read == 0)
        throw new EndOfStreamException("NRG track is truncated.");
      output.Write(buffer, 0, read);
      length -= read;
    }
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        return false;
      offset += read;
    }
    return true;
  }

  private static string? ReadAsciiField(ReadOnlySpan<byte> field) {
    var length = field.IndexOf((byte)0);
    if (length < 0)
      length = field.Length;
    while (length > 0 && field[length - 1] == (byte)' ')
      --length;
    return length == 0 ? null : Encoding.ASCII.GetString(field[..length]);
  }

  private static int FromBcd(byte value) {
    var high = value >> 4;
    var low = value & 0x0F;
    return high <= 9 && low <= 9 ? high * 10 + low : -2;
  }

  private static string StripVersionSuffix(string name) {
    var separator = name.IndexOf(';');
    return separator >= 0 ? name[..separator] : name;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
