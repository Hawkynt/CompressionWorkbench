using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nrg;

/// <summary>NRG v2 track storage modes understood by Nero-compatible readers.</summary>
public enum NrgTrackMode : byte {
  /// <summary>Cooked Mode 1, 2,048 bytes per sector.</summary>
  Mode1 = 0x00,
  /// <summary>Cooked Mode 2 Form 1, 2,048 bytes per sector.</summary>
  Mode2Form1 = 0x02,
  /// <summary>Mode 2 Form 2 / almost-full sector, 2,336 bytes per sector.</summary>
  Mode2Form2 = 0x03,
  /// <summary>Raw Mode 1, 2,352 bytes per sector.</summary>
  Mode1Raw = 0x05,
  /// <summary>Raw Mode 2, 2,352 bytes per sector.</summary>
  Mode2Raw = 0x06,
  /// <summary>Raw CD-DA audio, 2,352 bytes per sector.</summary>
  Audio = 0x07,
  /// <summary>Raw Mode 1 plus 96-byte subchannel, 2,448 bytes per sector.</summary>
  Mode1RawWithSubchannel = 0x0F,
  /// <summary>Raw CD-DA audio plus 96-byte subchannel, 2,448 bytes per sector.</summary>
  AudioWithSubchannel = 0x10,
  /// <summary>Raw Mode 2 plus 96-byte subchannel, 2,448 bytes per sector.</summary>
  Mode2RawWithSubchannel = 0x11,
}

/// <summary>One track to be written into an NRG disc image.</summary>
/// <param name="Mode">On-disc/storage mode.</param>
/// <param name="Data">Track bytes, already encoded in the sector representation selected by <paramref name="Mode"/>.</param>
public sealed record NrgTrackDefinition(NrgTrackMode Mode, byte[] Data) {
  /// <summary>Number of sectors in index 00 before index 01. Defaults to no stored pregap.</summary>
  public int PregapSectors { get; init; }

  /// <summary>
  /// Optional pregap bytes. When supplied they must contain exactly <see cref="PregapSectors"/> sectors,
  /// or, when <see cref="PregapSectors"/> is zero, determine the pregap length themselves. Raw framed
  /// and subchannel modes require explicit bytes because an all-zero sector would not be valid framing.
  /// </summary>
  public byte[]? PregapData { get; init; }

  /// <summary>Optional 12-character ISRC for this track.</summary>
  public string? Isrc { get; init; }

  /// <summary>Whether the Q-subchannel control byte advertises digital-copy permission.</summary>
  public bool CopyPermitted { get; init; }
}

/// <summary>One NRG session. Track numbers are assigned globally and consecutively across sessions.</summary>
/// <param name="Tracks">Tracks in this session.</param>
public sealed record NrgSessionDefinition(IReadOnlyList<NrgTrackDefinition> Tracks) {
  /// <summary>Optional 13-digit media catalog number (MCN/EAN-13).</summary>
  public string? Mcn { get; init; }
}

/// <summary>A complete NRG disc authoring description.</summary>
/// <param name="Sessions">Sessions in physical order.</param>
public sealed record NrgDiscDefinition(IReadOnlyList<NrgSessionDefinition> Sessions) {
  /// <summary>Optional raw CD-TEXT packs. The byte length must be a multiple of 18.</summary>
  public byte[]? CdText { get; init; }
}

/// <summary>
/// Writes Nero NRG v2 images using the DAO profile (<c>CUEX</c> + <c>DAOX</c> per session).
/// This profile can represent multiple sessions, mixed data/audio tracks, pregaps, MCN, ISRC,
/// raw sector modes and optional 96-byte subchannel data without flattening them into one ISO track.
/// </summary>
/// <remarks>
/// Nero never published a normative NRG specification. The field layout used here is the independently
/// reconstructed public layout also consumed by CDEmu/libMirage and libcdio. No implementation code from
/// those projects is copied; this writer is an independent managed implementation of the observable format.
/// </remarks>
public static class NrgWriter {
  private const uint CdRomMediumType = 0x00000400;
  private const int LeadInSectors = 4500;
  private const int FirstLeadOutSectors = 6750;
  private const int LaterLeadOutSectors = 2250;

  private sealed record TrackLayout(
    int TrackNumber,
    NrgTrackDefinition Definition,
    int SectorSize,
    int PregapSectors,
    long PregapOffset,
    long StartOffset,
    long EndOffset,
    int Index0Lba,
    int Index1Lba);

  private sealed record SessionLayout(
    NrgSessionDefinition Definition,
    int LeadInLba,
    int LeadOutLba,
    IReadOnlyList<TrackLayout> Tracks);

  /// <summary>Writes <paramref name="disc"/> as an NRG v2 image.</summary>
  public static void Write(Stream output, NrgDiscDefinition disc) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(disc);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("NRG authoring requires a writable, seekable stream.", nameof(output));

    ValidateDisc(disc);
    output.Position = 0;
    output.SetLength(0);

    var sessions = WriteTrackData(output, disc);
    var trailerOffset = checked((ulong)output.Position);

    // Allocated once, outside the loop: a stackalloc per iteration grows the frame with the
    // session count (CA2014). The four bytes are fully rewritten each time.
    Span<byte> sinf = stackalloc byte[4];
    foreach (var session in sessions) {
      WriteCuex(output, session);
      WriteDaox(output, session);

      BinaryPrimitives.WriteUInt32BigEndian(sinf, checked((uint)session.Tracks.Count));
      WriteChunk(output, "SINF"u8, sinf);
    }

    if (disc.CdText is { Length: > 0 } cdText)
      WriteChunk(output, "CDTX"u8, cdText);

    Span<byte> mtyp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(mtyp, CdRomMediumType);
    WriteChunk(output, "MTYP"u8, mtyp);
    WriteChunk(output, "END!"u8, ReadOnlySpan<byte>.Empty);

    Span<byte> footer = stackalloc byte[12];
    "NER5"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt64BigEndian(footer[4..], trailerOffset);
    output.Write(footer);
  }

  /// <summary>Gets the stored sector size implied by an NRG mode code.</summary>
  public static int GetSectorSize(NrgTrackMode mode) => mode switch {
    NrgTrackMode.Mode1 or NrgTrackMode.Mode2Form1 => 2048,
    NrgTrackMode.Mode2Form2 => 2336,
    NrgTrackMode.Mode1Raw or NrgTrackMode.Mode2Raw or NrgTrackMode.Audio => 2352,
    NrgTrackMode.Mode1RawWithSubchannel or NrgTrackMode.AudioWithSubchannel or NrgTrackMode.Mode2RawWithSubchannel => 2448,
    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported NRG track mode."),
  };

  private static List<SessionLayout> WriteTrackData(Stream output, NrgDiscDefinition disc) {
    var sessions = new List<SessionLayout>(disc.Sessions.Count);
    var nextTrackNumber = 1;
    var previousLeadOutLba = 0;

    for (var sessionIndex = 0; sessionIndex < disc.Sessions.Count; ++sessionIndex) {
      var definition = disc.Sessions[sessionIndex];
      var firstTrackPregap = GetPregapSectors(definition.Tracks[0], GetSectorSize(definition.Tracks[0].Mode));
      int leadInLba;
      int programLba;
      if (sessionIndex == 0) {
        leadInLba = -150;
        programLba = -firstTrackPregap;
      } else {
        var previousLeadOutLength = sessionIndex == 1 ? FirstLeadOutSectors : LaterLeadOutSectors;
        leadInLba = checked(previousLeadOutLba + previousLeadOutLength);
        programLba = checked(leadInLba + LeadInSectors);
      }

      var tracks = new List<TrackLayout>(definition.Tracks.Count);
      foreach (var track in definition.Tracks) {
        var sectorSize = GetSectorSize(track.Mode);
        var pregapSectors = GetPregapSectors(track, sectorSize);
        var pregapOffset = output.Position;
        if (pregapSectors != 0) {
          if (track.PregapData is { Length: > 0 } pregapData)
            output.Write(pregapData);
          else
            WriteZeros(output, checked((long)pregapSectors * sectorSize));
        }

        var startOffset = output.Position;
        output.Write(track.Data);
        var endOffset = output.Position;
        var index0Lba = programLba;
        var index1Lba = checked(index0Lba + pregapSectors);
        var dataSectors = track.Data.Length / sectorSize;
        programLba = checked(index1Lba + dataSectors);

        tracks.Add(new TrackLayout(
          nextTrackNumber++, track, sectorSize, pregapSectors,
          pregapOffset, startOffset, endOffset, index0Lba, index1Lba));
      }

      var leadOutLba = programLba;
      sessions.Add(new SessionLayout(definition, leadInLba, leadOutLba, tracks));
      previousLeadOutLba = leadOutLba;
    }

    return sessions;
  }

  private static void WriteCuex(Stream output, SessionLayout session) {
    var payload = new byte[checked((session.Tracks.Count * 2 + 2) * 8)];
    var position = 0;

    var firstAdrCtl = AdrCtl(session.Tracks[0].Definition);
    WriteCueEntry(payload.AsSpan(position, 8), firstAdrCtl, 0x00, 0x00, session.LeadInLba);
    position += 8;

    foreach (var track in session.Tracks) {
      var adrCtl = AdrCtl(track.Definition);
      var trackBcd = ToBcd(track.TrackNumber);
      WriteCueEntry(payload.AsSpan(position, 8), adrCtl, trackBcd, 0x00, track.Index0Lba);
      position += 8;
      WriteCueEntry(payload.AsSpan(position, 8), adrCtl, trackBcd, 0x01, track.Index1Lba);
      position += 8;
    }

    var lastAdrCtl = AdrCtl(session.Tracks[^1].Definition);
    WriteCueEntry(payload.AsSpan(position, 8), lastAdrCtl, 0xAA, 0x01, session.LeadOutLba);
    WriteChunk(output, "CUEX"u8, payload);
  }

  private static void WriteDaox(Stream output, SessionLayout session) {
    var payload = new byte[checked(22 + session.Tracks.Count * 42)];
    BinaryPrimitives.WriteUInt32BigEndian(payload, checked((uint)payload.Length));

    if (session.Definition.Mcn is { } mcn)
      Encoding.ASCII.GetBytes(mcn, payload.AsSpan(4, 13));

    payload[17] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(18, 2), GetTocType(session));
    payload[20] = checked((byte)session.Tracks[0].TrackNumber);
    payload[21] = checked((byte)session.Tracks[^1].TrackNumber);

    var position = 22;
    foreach (var track in session.Tracks) {
      var record = payload.AsSpan(position, 42);
      if (track.Definition.Isrc is { } isrc)
        Encoding.ASCII.GetBytes(isrc, record[..12]);
      BinaryPrimitives.WriteUInt16BigEndian(record[12..], checked((ushort)track.SectorSize));
      record[14] = (byte)track.Definition.Mode;
      record[15] = 0;
      BinaryPrimitives.WriteUInt16BigEndian(record[16..], 1);
      BinaryPrimitives.WriteUInt64BigEndian(record[18..], checked((ulong)track.PregapOffset));
      BinaryPrimitives.WriteUInt64BigEndian(record[26..], checked((ulong)track.StartOffset));
      BinaryPrimitives.WriteUInt64BigEndian(record[34..], checked((ulong)track.EndOffset));
      position += 42;
    }

    WriteChunk(output, "DAOX"u8, payload);
  }

  private static ushort GetTocType(SessionLayout session) {
    if (session.Tracks.Any(static track => IsMode2(track.Definition.Mode)))
      return 0x2001;
    return session.Tracks.Any(static track => !IsAudio(track.Definition.Mode)) ? (ushort)0x0001 : (ushort)0x0000;
  }

  private static void WriteCueEntry(Span<byte> destination, byte adrCtl, byte track, byte index, int lba) {
    destination[0] = adrCtl;
    destination[1] = track;
    destination[2] = index;
    destination[3] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..], unchecked((uint)lba));
  }

  private static byte AdrCtl(NrgTrackDefinition track) {
    var control = IsAudio(track.Mode) ? 0x00 : 0x40;
    if (track.CopyPermitted)
      control |= 0x20;
    return checked((byte)(control | 0x01));
  }

  private static byte ToBcd(int value) {
    if (value is < 0 or > 99)
      throw new ArgumentOutOfRangeException(nameof(value), value, "BCD track numbers are limited to 0..99.");
    return checked((byte)(((value / 10) << 4) | value % 10));
  }

  private static int GetPregapSectors(NrgTrackDefinition track, int sectorSize) {
    if (track.PregapSectors < 0)
      throw new ArgumentOutOfRangeException(nameof(track.PregapSectors), "Pregap sector count cannot be negative.");
    if (track.PregapData is not { Length: > 0 } bytes) {
      if (track.PregapSectors != 0 && RequiresExplicitPregapData(track.Mode))
        throw new ArgumentException($"NRG {track.Mode} pregaps require explicit PregapData with valid sector framing.", nameof(track));
      return track.PregapSectors;
    }
    if (bytes.Length % sectorSize != 0)
      throw new ArgumentException("NRG pregap data must be an integral number of sectors.", nameof(track));

    var fromBytes = bytes.Length / sectorSize;
    if (track.PregapSectors != 0 && track.PregapSectors != fromBytes)
      throw new ArgumentException("NRG PregapSectors does not match PregapData length.", nameof(track));
    return fromBytes;
  }

  private static bool RequiresExplicitPregapData(NrgTrackMode mode)
    => mode is NrgTrackMode.Mode1Raw or NrgTrackMode.Mode2Raw or
      NrgTrackMode.Mode1RawWithSubchannel or NrgTrackMode.AudioWithSubchannel or NrgTrackMode.Mode2RawWithSubchannel;

  private static void ValidateDisc(NrgDiscDefinition disc) {
    if (disc.Sessions is null || disc.Sessions.Count == 0)
      throw new ArgumentException("An NRG disc must contain at least one session.", nameof(disc));
    if (disc.CdText is { Length: > 0 } cdText && cdText.Length % 18 != 0)
      throw new ArgumentException("NRG CD-TEXT data must contain complete 18-byte packs.", nameof(disc));

    var trackCount = 0;
    foreach (var session in disc.Sessions) {
      if (session is null || session.Tracks is null || session.Tracks.Count == 0)
        throw new ArgumentException("Every NRG session must contain at least one track.", nameof(disc));
      ValidateMcn(session.Mcn);

      foreach (var track in session.Tracks) {
        if (track is null)
          throw new ArgumentException("NRG tracks cannot be null.", nameof(disc));
        var sectorSize = GetSectorSize(track.Mode);
        if (track.Data is null || track.Data.Length == 0 || track.Data.Length % sectorSize != 0)
          throw new ArgumentException($"NRG {track.Mode} track data must be a non-empty multiple of {sectorSize} bytes.", nameof(disc));
        _ = GetPregapSectors(track, sectorSize);
        ValidateIsrc(track.Isrc);
      }

      trackCount = checked(trackCount + session.Tracks.Count);
    }

    if (trackCount > 99)
      throw new ArgumentException("Compact Disc NRG images can contain at most 99 tracks.", nameof(disc));
  }

  private static void ValidateMcn(string? mcn) {
    if (mcn is null)
      return;
    if (mcn.Length != 13 || mcn.Any(static c => c is < '0' or > '9'))
      throw new ArgumentException("NRG MCN must contain exactly 13 ASCII digits.", nameof(mcn));
  }

  private static void ValidateIsrc(string? isrc) {
    if (isrc is null)
      return;
    if (isrc.Length != 12 || isrc.Any(static c => c > 0x7F || char.IsControl(c)))
      throw new ArgumentException("NRG ISRC must contain exactly 12 printable ASCII characters.", nameof(isrc));
  }

  private static bool IsAudio(NrgTrackMode mode)
    => mode is NrgTrackMode.Audio or NrgTrackMode.AudioWithSubchannel;

  private static bool IsMode2(NrgTrackMode mode)
    => mode is NrgTrackMode.Mode2Form1 or NrgTrackMode.Mode2Form2 or NrgTrackMode.Mode2Raw or NrgTrackMode.Mode2RawWithSubchannel;

  private static void WriteZeros(Stream output, long length) {
    Span<byte> zeros = stackalloc byte[4096];
    zeros.Clear();
    while (length > 0) {
      var count = (int)Math.Min(length, zeros.Length);
      output.Write(zeros[..count]);
      length -= count;
    }
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload) {
    if (id.Length != 4)
      throw new ArgumentException("NRG chunk ids are exactly four bytes.", nameof(id));

    Span<byte> header = stackalloc byte[8];
    id.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)payload.Length));
    output.Write(header);
    output.Write(payload);
  }
}
