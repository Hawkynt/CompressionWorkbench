#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using FileFormat.Cdi;

namespace FileFormat.CloneCd;

/// <summary>What a track holds; the value is the CCD <c>MODE=</c> number.</summary>
public enum CloneCdTrackKind {
  /// <summary>CD-DA: 2,352 bytes of 16-bit stereo PCM per sector.</summary>
  Audio = 0,
  /// <summary>Mode 1 data: 2,048 user bytes per sector, EDC and ECC.</summary>
  Mode1 = 1,
  /// <summary>Mode 2 (CD-ROM XA): an 8-byte subheader and 2,328 bytes per sector, Form 1 or Form 2 per sector.</summary>
  Mode2 = 2,
}

/// <summary>
/// One track to write. <see cref="Data"/> is PCM for <see cref="CloneCdTrackKind.Audio"/> (padded with silence to
/// whole sectors), whole 2,048-byte sectors for <see cref="CloneCdTrackKind.Mode1"/>, and whole 2,336-byte sectors
/// (subheader and data, the <c>MODE2/2336</c> convention) for <see cref="CloneCdTrackKind.Mode2"/>; the subheader's
/// Form 2 bit picks Form 2, otherwise the sector is Form 1.
/// </summary>
public sealed record CloneCdTrackSource(CloneCdTrackKind Kind, byte[] Data);

/// <summary>
/// Writes CloneCD image sets: the <c>.ccd</c> control file, the raw <c>.img</c> (2,352-byte sectors with sync,
/// BCD address, EDC and ECC from ECMA-130) and the <c>.sub</c> subchannel (96 bytes per sector: P, Q, then R–W).
/// Discs may hold several sessions, each with audio, Mode 1 and Mode 2 tracks. Layout in
/// <c>docs/CLONECD-ON-DISK.md</c>.
/// </summary>
public static class CloneCdWriter {
  public const int UserDataSize = 2048;
  public const int Mode2SectorSize = 2336;
  public const int RawSectorSize = 2352;
  public const int SubchannelSize = 96;

  /// <summary>Frames before LBA 0: the 2-second pregap of track 1.</summary>
  private const int PregapFrames = 150;

  /// <summary>ECMA-130 22.2: a P flag lasts at least 150 sections and its last ONE is the first section with user data.</summary>
  private const int FlagSections = 150;

  /// <summary>ECMA-130 20: a pre-gap after a different kind of track: 75 sections coded as that track, then 150 as this one.</summary>
  private const int TransitionInterval = 75;

  /// <summary>ECMA-130 20: a data track followed by an audio track ends with a post-gap of 150 sections.</summary>
  private const int PostGapSectors = 150;

  /// <summary>Orange Book session gap: the first lead-out lasts 90 s, later ones 30 s, every lead-in 60 s.</summary>
  private const int FirstLeadOut = 6750, LaterLeadOut = 2250, LeadIn = 4500;

  /// <summary>The highest frame address a CD Q channel can carry (99:59:74).</summary>
  private const int MaxFrame = 100 * 4500 - 1;

  /// <summary>The three files of a CloneCD image.</summary>
  public sealed record ImageSet(byte[] Ccd, byte[] Img, byte[] Sub);

  /// <summary>A track placed on the disc.</summary>
  private sealed record Placed(int Number, int Session, CloneCdTrackKind Kind, byte[] Data, int Sectors,
    int Index1, int Pregap, bool PregapStored, CloneCdTrackKind PregapLeadKind, int LeadInterval, int PostGap) {
    public int Index0 => this.Index1 - this.Pregap;
    public int End => this.Index1 + this.Sectors + this.PostGap;
  }

  /// <summary>Builds the image set of a single-session data disc whose Mode 1 user data is <paramref name="iso"/>.</summary>
  public static ImageSet FromIso(ReadOnlySpan<byte> iso) => Build([[new(CloneCdTrackKind.Mode1, iso.ToArray())]]);

  /// <summary>Builds the image set of a disc with the given sessions, each a list of tracks in disc order.</summary>
  public static ImageSet Build(IReadOnlyList<IReadOnlyList<CloneCdTrackSource>> sessions) {
    ArgumentNullException.ThrowIfNull(sessions);
    var tracks = Plan(sessions);
    var leadOuts = tracks.GroupBy(t => t.Session).ToDictionary(g => g.Key, g => g.Last().End);
    var stored = tracks.Sum(t => (long)(t.End - (t.PregapStored ? t.Index0 : t.Index1)));
    if (stored * RawSectorSize > Array.MaxLength)
      throw new NotSupportedException("CloneCD: the image is too large to build in memory.");

    var img = new byte[stored * RawSectorSize];
    var sub = new byte[stored * SubchannelSize];
    var flags = FlagRanges(tracks, leadOuts);
    var at = 0;
    foreach (var t in tracks) {
      var first = t.PregapStored ? t.Index0 : t.Index1;
      for (var lba = first; lba < t.End; ++lba, ++at) {
        var raw = img.AsSpan(at * RawSectorSize, RawSectorSize);
        var inPregap = lba < t.Index1;
        var leadInterval = inPregap && lba - t.Index0 < t.LeadInterval;
        var kind = leadInterval ? t.PregapLeadKind : t.Kind;
        var userSector = lba - t.Index1;
        var payload = !inPregap && userSector < t.Sectors ? Payload(t, userSector) : default;
        WriteSector(raw, kind, lba, payload);

        var subchannel = sub.AsSpan(at * SubchannelSize, SubchannelSize);
        if (flags.Any(f => lba >= f.From && lba <= f.To)) subchannel[..12].Fill(0xFF);
        var control = Control(leadInterval ? t.PregapLeadKind : t.Kind);
        var relative = inPregap ? t.Index1 - 1 - lba : lba - t.Index1;
        WriteQ(subchannel.Slice(12, 12), control, t.Number, inPregap ? 0 : 1, relative, lba);
      }
    }
    return new ImageSet(Encoding.ASCII.GetBytes(Ccd(tracks, leadOuts)), img, sub);
  }

  /// <summary>The P and Q channels of a single-session disc with one data track of <paramref name="sectors"/> sectors; R–W stay zero.</summary>
  public static byte[] Subchannel(int sectors) => Build([[new(CloneCdTrackKind.Mode1, new byte[(long)sectors * UserDataSize])]]).Sub;

  private static List<Placed> Plan(IReadOnlyList<IReadOnlyList<CloneCdTrackSource>> sessions) {
    if (sessions.Count == 0 || sessions.Any(s => s is null || s.Count == 0))
      throw new ArgumentException("CloneCD: every session needs at least one track.", nameof(sessions));
    var placed = new List<Placed>();
    var number = 1;
    var previousLeadOut = 0;
    for (var s = 0; s < sessions.Count; ++s) {
      Placed? before = null;
      var cursor = s == 0 ? 0 : previousLeadOut + (s == 1 ? FirstLeadOut : LaterLeadOut) + LeadIn + PregapFrames;
      foreach (var source in sessions[s]) {
        ArgumentNullException.ThrowIfNull(source);
        var sectors = SectorCount(source);
        int pregap = 0, lead = 0;
        var leadKind = source.Kind;
        var stored = before != null;
        if (before == null) pregap = PregapFrames;
        else if (before.Kind != source.Kind) {
          if (source.Kind == CloneCdTrackKind.Audio) {
            // The data track before ends with a post-gap; the audio track keeps a 2-second pause.
            before = before with { PostGap = PostGapSectors };
            placed[^1] = before;
            cursor += PostGapSectors;
            pregap = PregapFrames;
          } else {
            pregap = TransitionInterval + PregapFrames;
            lead = TransitionInterval;
            leadKind = before.Kind;
          }
        }
        var index1 = before == null ? cursor : cursor + pregap;
        var track = new Placed(number++, s + 1, source.Kind, source.Data, sectors, index1, pregap, stored, leadKind, lead, 0);
        placed.Add(track);
        before = track;
        cursor = track.End;
      }
      previousLeadOut = cursor;
      if (number > 100) throw new NotSupportedException("CloneCD: a disc holds at most 99 tracks.");
      if (cursor + PregapFrames > MaxFrame) throw new NotSupportedException("CloneCD: the disc is longer than a CD address can reach (99:59:74).");
    }
    return placed;
  }

  private static int SectorCount(CloneCdTrackSource source) {
    var unit = source.Kind switch {
      CloneCdTrackKind.Audio => RawSectorSize,
      CloneCdTrackKind.Mode1 => UserDataSize,
      CloneCdTrackKind.Mode2 => Mode2SectorSize,
      _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "CloneCD: unknown track kind."),
    };
    var length = source.Data?.LongLength ?? 0;
    if (length == 0)
      throw new InvalidDataException($"CloneCD: a {source.Kind} track needs at least one sector of data.");
    if (source.Kind != CloneCdTrackKind.Audio && length % unit != 0)
      throw new InvalidDataException($"CloneCD: a {source.Kind} track needs whole {unit}-byte sectors; got {length} bytes.");
    var sectors = (length + unit - 1) / unit;
    if (sectors > MaxFrame) throw new NotSupportedException("CloneCD: the track is longer than a CD can hold.");
    return (int)sectors;
  }

  private static ReadOnlySpan<byte> Payload(Placed t, int sector) {
    var unit = t.Kind switch { CloneCdTrackKind.Audio => RawSectorSize, CloneCdTrackKind.Mode1 => UserDataSize, _ => Mode2SectorSize };
    var start = (long)sector * unit;
    return t.Data.AsSpan((int)start, (int)Math.Min(unit, t.Data.LongLength - start));
  }

  /// <summary>
  /// Fills one raw sector at <paramref name="lba"/>: audio as PCM (silence when <paramref name="payload"/> is empty),
  /// data with sync, address, mode, the payload (zero user data in a pre- or post-gap) and EDC/ECC.
  /// </summary>
  public static void WriteSector(Span<byte> raw, CloneCdTrackKind kind, int lba, ReadOnlySpan<byte> payload) {
    raw[..RawSectorSize].Clear();
    if (kind == CloneCdTrackKind.Audio) {
      payload.CopyTo(raw);
      return;
    }
    raw[1..11].Fill(0xFF);
    WriteMsf(raw[12..], lba + PregapFrames);
    raw[15] = (byte)kind;
    payload.CopyTo(raw[16..]);
    if (kind == CloneCdTrackKind.Mode1) CdiCdSectorIntegrity.RegenerateMode1(raw);
    else if ((raw[18] & 0x20) != 0) CdiCdSectorIntegrity.RegenerateMode2Form2(raw);
    else CdiCdSectorIntegrity.RegenerateMode2Form1(raw);
  }

  /// <summary>
  /// ECMA-130 22.2: a flag of at least 150 sections (or the whole pause when longer) ends on the first section with
  /// user data of every track, and on the first section of each lead-out.
  /// </summary>
  private static List<(int From, int To)> FlagRanges(List<Placed> tracks, Dictionary<int, int> leadOuts) {
    var ranges = tracks.Select(t => (t.Index1 - Math.Max(FlagSections - 1, t.Pregap), t.Index1)).ToList();
    ranges.AddRange(leadOuts.Values.Select(l => (l - (FlagSections - 1), l)));
    return ranges;
  }

  private static byte Control(CloneCdTrackKind kind) => kind == CloneCdTrackKind.Audio ? (byte)0x00 : (byte)0x04;

  /// <summary>Q, mode 1 (position): control/ADR, track, index, time in the track, zero, absolute time, inverted CRC.</summary>
  private static void WriteQ(Span<byte> q, byte control, int track, int index, int relative, int lba) {
    q[0] = (byte)(control << 4 | 1);
    q[1] = Bcd(track);
    q[2] = Bcd(index);
    WriteMsf(q[3..], relative);
    q[6] = 0;
    WriteMsf(q[7..], lba + PregapFrames);
    var crc = (ushort)~Crc16(q[..10]);
    q[10] = (byte)(crc >> 8);
    q[11] = (byte)crc;
  }

  /// <summary>The control file: per session the A0/A1/A2 points and the tracks, B0 on every session but the last.</summary>
  private static string Ccd(List<Placed> tracks, Dictionary<int, int> leadOuts) {
    var sessions = tracks.GroupBy(t => t.Session).ToList();
    var discType = tracks.Any(t => t.Kind == CloneCdTrackKind.Mode2) ? 0x20 : 0x00;
    var entries = new List<string>();
    foreach (var session in sessions) {
      var first = session.First();
      var last = session.Last();
      entries.Add(Entry(session.Key, 0xA0, 1, Control(first.Kind), 0, 0, 0, -PregapFrames, 0, first.Number, discType, 0));
      entries.Add(Entry(session.Key, 0xA1, 1, Control(last.Kind), 0, 0, 0, -PregapFrames, 0, last.Number, 0, 0));
      var (lm, ls, lf) = Msf(leadOuts[session.Key] + PregapFrames);
      entries.Add(Entry(session.Key, 0xA2, 1, Control(last.Kind), 0, 0, 0, -PregapFrames, 0, lm, ls, lf));
      foreach (var t in session) {
        var (m, s, f) = Msf(t.Index1 + PregapFrames);
        entries.Add(Entry(session.Key, t.Number, 1, Control(t.Kind), 0, 0, 0, -PregapFrames, 0, m, s, f));
      }
      if (session.Key != sessions[^1].Key) {
        // B0 (ADR 5): where the next program area starts, and the outermost lead-out a disc may have.
        var next = tracks.First(t => t.Session == session.Key + 1);
        var (am, asec, af) = Msf(next.Index0 + PregapFrames);
        entries.Add(Entry(session.Key, 0xB0, 5, Control(last.Kind), am, asec, af, next.Index0, 1, 79, 59, 74));
      }
    }

    var sb = new StringBuilder();
    sb.Append("[CloneCD]\r\nVersion=3\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"[Disc]\r\nTocEntries={entries.Count}\r\nSessions={sessions.Count}\r\nDataTracksScrambled=0\r\nCDTextLength=0\r\n");
    foreach (var session in sessions)
      sb.Append(CultureInfo.InvariantCulture, $"[Session {session.Key}]\r\nPreGapMode={(int)session.First().Kind}\r\nPreGapSubC=0\r\n");
    for (var i = 0; i < entries.Count; ++i)
      sb.Append(CultureInfo.InvariantCulture, $"[Entry {i}]\r\n").Append(entries[i]);
    foreach (var t in tracks) {
      sb.Append(CultureInfo.InvariantCulture, $"[TRACK {t.Number}]\r\nMODE={(int)t.Kind}\r\n");
      if (t.PregapStored && t.Pregap > 0) sb.Append(CultureInfo.InvariantCulture, $"INDEX 0={t.Index0}\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"INDEX 1={t.Index1}\r\n");
    }
    return sb.ToString();
  }

  private static string Entry(int session, int point, int adr, int control, int aMin, int aSec, int aFrame, int aLba, int zero, int pMin, int pSec, int pFrame)
    => string.Create(CultureInfo.InvariantCulture,
      $"Session={session}\r\nPoint=0x{point:x2}\r\nADR=0x{adr:x2}\r\nControl=0x{control:x2}\r\nTrackNo=0\r\n" +
      $"AMin={aMin}\r\nASec={aSec}\r\nAFrame={aFrame}\r\nALBA={aLba}\r\nZero={zero}\r\n" +
      $"PMin={pMin}\r\nPSec={pSec}\r\nPFrame={pFrame}\r\nPLBA={(pMin * 60 + pSec) * 75 + pFrame - PregapFrames}\r\n");

  private static (int Min, int Sec, int Frame) Msf(int frames) => (frames / 4500, frames / 75 % 60, frames % 75);

  private static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);

  private static void WriteMsf(Span<byte> msf, int frames) {
    msf[0] = Bcd(frames / 4500);
    msf[1] = Bcd(frames / 75 % 60);
    msf[2] = Bcd(frames % 75);
  }

  /// <summary>CRC-16/CCITT (polynomial 0x1021, initial value 0), as the Q channel uses it before inversion.</summary>
  public static ushort Crc16(ReadOnlySpan<byte> data) {
    var crc = 0;
    foreach (var b in data) {
      crc ^= b << 8;
      for (var i = 0; i < 8; ++i) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
    }
    return (ushort)crc;
  }
}
