#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.CloneCd;

/// <summary>A track of a CloneCD image and where its sectors sit in the <c>.img</c>.</summary>
/// <param name="Number">Track number (1–99).</param>
/// <param name="Session">Session the track belongs to.</param>
/// <param name="Kind">Audio, Mode 1 or Mode 2.</param>
/// <param name="Index0">First pre-gap sector stored in the <c>.img</c>, or <see langword="null"/> when none is known.</param>
/// <param name="Index1">First sector with user data (the TOC point).</param>
/// <param name="End">First sector after the track's stored region: the next track's point or the session's lead-out.</param>
/// <param name="ImgSector">Index of the <see cref="Index1"/> sector in the <c>.img</c>.</param>
/// <param name="DataEnd">First sector after the user data: the next track's index 0 when the control file names it, else <see cref="End"/>.</param>
public sealed record CloneCdTrack(int Number, int Session, CloneCdTrackKind Kind, int? Index0, int Index1, int End, long ImgSector, int DataEnd) {
  /// <summary>The archive name of the track: <c>track-NN.iso</c>/<c>.wav</c>/<c>.mode2</c>, under <c>session-NN/</c> after the first session.</summary>
  public string EntryName {
    get {
      var ext = this.Kind switch { CloneCdTrackKind.Audio => ".wav", CloneCdTrackKind.Mode1 => ".iso", _ => ".mode2" };
      var leaf = string.Create(CultureInfo.InvariantCulture, $"track-{this.Number:D2}{ext}");
      return this.Session == 1 ? leaf : string.Create(CultureInfo.InvariantCulture, $"session-{this.Session:D2}/{leaf}");
    }
  }
}

/// <summary>
/// The track map of a CloneCD image set, read from the TOC entries of the <c>.ccd</c> (and its <c>[TRACK n]</c>
/// sections when present). The <c>.img</c> holds each session from its first track's point to its lead-out, the
/// sessions one after the other: lead-outs, lead-ins and the first pre-gap of every session are not stored.
/// </summary>
public sealed class CloneCdLayout {
  private const int RawSectorSize = CloneCdWriter.RawSectorSize;
  private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  /// <summary>Gets the tracks in disc order.</summary>
  public IReadOnlyList<CloneCdTrack> Tracks { get; }

  /// <summary>Gets the number of sectors the <c>.img</c> must hold.</summary>
  public long ImgSectors { get; }

  private CloneCdLayout(IReadOnlyList<CloneCdTrack> tracks, long sectors) {
    this.Tracks = tracks;
    this.ImgSectors = sectors;
  }

  /// <summary>
  /// Reads the track map from the control file text; <paramref name="img"/> (optional) tells data tracks of a control
  /// file without <c>[TRACK n]</c> sections apart by their sector mode. Returns <see langword="null"/> when the TOC is
  /// incomplete or inconsistent, or the data tracks are scrambled.
  /// </summary>
  public static CloneCdLayout? TryParse(string ccd, Stream? img = null) {
    try {
      return Parse(ccd, img);
    } catch (Exception e) when (e is FormatException or OverflowException or InvalidDataException or KeyNotFoundException or IOException) {
      return null;
    }
  }

  private static CloneCdLayout? Parse(string ccd, Stream? img) {
    var sections = Sections(ccd);
    if (sections.TryGetValue("Disc", out var disc) && disc.TryGetValue("DataTracksScrambled", out var scrambled) && scrambled.Trim() != "0")
      return null;

    var points = new List<(int Session, int Point, int Control, int Lba)>();
    foreach (var (name, values) in sections) {
      if (!name.StartsWith("Entry ", StringComparison.OrdinalIgnoreCase)) continue;
      var adr = Number(values["ADR"]);
      if ((adr & 0x0F) != 1) continue;
      var lba = values.TryGetValue("PMin", out var m)
        ? (Number(m) * 60 + Number(values["PSec"])) * 75 + Number(values["PFrame"]) - 150
        : Number(values["PLBA"]);
      points.Add((Number(values["Session"]), Number(values["Point"]), Number(values["Control"]), lba));
    }

    var leadOuts = points.Where(p => p.Point == 0xA2).GroupBy(p => p.Session).ToDictionary(g => g.Key, g => g.First().Lba);
    var trackPoints = points.Where(p => p.Point is >= 1 and <= 99)
      .GroupBy(p => p.Point).Select(g => g.First()).OrderBy(p => p.Point).ToList();
    if (trackPoints.Count == 0) return null;

    var tracks = new List<CloneCdTrack>();
    long imgSector = 0;
    for (var i = 0; i < trackPoints.Count; ++i) {
      var p = trackPoints[i];
      if (!leadOuts.TryGetValue(p.Session, out var leadOut)) return null;
      var hasNext = i + 1 < trackPoints.Count && trackPoints[i + 1].Session == p.Session;
      var end = hasNext ? trackPoints[i + 1].Lba : leadOut;
      if (end <= p.Lba || (i > 0 && p.Lba < tracks[^1].End) || (i > 0 && p.Session < tracks[^1].Session)) return null;

      sections.TryGetValue(string.Create(CultureInfo.InvariantCulture, $"TRACK {p.Point}"), out var info);
      int? index0 = info != null && info.TryGetValue("INDEX 0", out var i0) ? Number(i0) : null;
      var dataEnd = end;
      if (hasNext && sections.TryGetValue(string.Create(CultureInfo.InvariantCulture, $"TRACK {trackPoints[i + 1].Point}"), out var nextInfo) &&
          nextInfo.TryGetValue("INDEX 0", out var n0) && Number(n0) is var nextIndex0 && nextIndex0 > p.Lba && nextIndex0 <= end)
        dataEnd = nextIndex0;

      CloneCdTrackKind kind;
      if (info != null && info.TryGetValue("MODE", out var mode))
        kind = Number(mode) switch {
          0 => CloneCdTrackKind.Audio,
          1 => CloneCdTrackKind.Mode1,
          2 => CloneCdTrackKind.Mode2,
          _ => throw new InvalidDataException("CloneCD: unknown track mode."),
        };
      else if ((p.Control & 0x04) == 0)
        kind = CloneCdTrackKind.Audio;
      else
        kind = ModeAt(img, imgSector) == 2 ? CloneCdTrackKind.Mode2 : CloneCdTrackKind.Mode1;

      tracks.Add(new CloneCdTrack(p.Point, p.Session, kind, index0, p.Lba, end, imgSector, dataEnd));
      imgSector += end - p.Lba;
    }
    return new CloneCdLayout(tracks, imgSector);
  }

  private static int ModeAt(Stream? img, long sector) {
    if (img == null || !img.CanSeek || (sector + 1) * RawSectorSize > img.Length) return 1;
    var head = new byte[16];
    img.Position = sector * RawSectorSize;
    img.ReadExactly(head);
    return head.AsSpan(0, 12).SequenceEqual(Sync) ? head[15] : 1;
  }

  /// <summary>
  /// Reads a track's user data from the <c>.img</c>: PCM as a WAV file for audio, 2,048 bytes per sector for Mode 1,
  /// and 2,336 bytes (subheader and data) per sector for Mode 2. Trailing sectors that are not of the track's mode
  /// (a following track's pre-gap the control file does not mark) are left out; any other sector of the wrong mode
  /// is refused rather than returned as data.
  /// </summary>
  public byte[] ReadTrack(CloneCdTrack track, Stream img) {
    ArgumentNullException.ThrowIfNull(track);
    ArgumentNullException.ThrowIfNull(img);
    if (this.ImgSectors * RawSectorSize > img.Length)
      throw new InvalidDataException("CloneCD: the .img is shorter than the control file describes.");
    var count = track.DataEnd - track.Index1;
    var raw = new byte[(long)count * RawSectorSize];
    img.Position = track.ImgSector * RawSectorSize;
    img.ReadExactly(raw);

    if (track.Kind == CloneCdTrackKind.Audio) return Wave(raw);

    var mode = (byte)track.Kind;
    bool IsMode(int s) => raw.AsSpan(s * RawSectorSize, 12).SequenceEqual(Sync) && raw[s * RawSectorSize + 15] == mode;
    var used = count;
    while (used > 0 && !IsMode(used - 1)) --used;
    if (used == 0) throw new InvalidDataException($"CloneCD: track {track.Number} holds no Mode {mode} sectors.");
    var unit = track.Kind == CloneCdTrackKind.Mode1 ? CloneCdWriter.UserDataSize : CloneCdWriter.Mode2SectorSize;
    var result = new byte[(long)used * unit];
    for (var s = 0; s < used; ++s) {
      if (!IsMode(s)) throw new InvalidDataException($"CloneCD: sector {track.Index1 + s} of track {track.Number} is not a Mode {mode} sector.");
      raw.AsSpan(s * RawSectorSize + 16, unit).CopyTo(result.AsSpan(s * unit));
    }
    return result;
  }

  /// <summary>
  /// The length <see cref="ReadTrack"/> returns, found from the sector headers alone (the trailing sectors of another
  /// mode are left out the same way); 0 when the track holds no sector of its mode.
  /// </summary>
  public long ExtractedLength(CloneCdTrack track, Stream img) {
    ArgumentNullException.ThrowIfNull(track);
    ArgumentNullException.ThrowIfNull(img);
    var count = track.DataEnd - track.Index1;
    if (track.Kind == CloneCdTrackKind.Audio) return 44 + (long)count * RawSectorSize;
    var head = new byte[16];
    var used = count;
    for (; used > 0; --used) {
      img.Position = (track.ImgSector + used - 1) * RawSectorSize;
      img.ReadExactly(head);
      if (head.AsSpan(0, 12).SequenceEqual(Sync) && head[15] == (byte)track.Kind) break;
    }
    return (long)used * (track.Kind == CloneCdTrackKind.Mode1 ? CloneCdWriter.UserDataSize : CloneCdWriter.Mode2SectorSize);
  }

  /// <summary>Wraps CD-DA (44.1 kHz, 16-bit, stereo, little-endian) in a RIFF WAVE header.</summary>
  public static byte[] Wave(ReadOnlySpan<byte> pcm) {
    var wav = new byte[44 + pcm.Length];
    var w = wav.AsSpan();
    "RIFF"u8.CopyTo(w);
    BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (uint)(36 + pcm.Length));
    "WAVEfmt "u8.CopyTo(w[8..]);
    BinaryPrimitives.WriteUInt32LittleEndian(w[16..], 16);
    BinaryPrimitives.WriteUInt16LittleEndian(w[20..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(w[22..], 2);
    BinaryPrimitives.WriteUInt32LittleEndian(w[24..], 44100);
    BinaryPrimitives.WriteUInt32LittleEndian(w[28..], 44100 * 4);
    BinaryPrimitives.WriteUInt16LittleEndian(w[32..], 4);
    BinaryPrimitives.WriteUInt16LittleEndian(w[34..], 16);
    "data"u8.CopyTo(w[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(w[40..], (uint)pcm.Length);
    pcm.CopyTo(w[44..]);
    return wav;
  }

  /// <summary>Returns the PCM of a WAV file in CD-DA format (44.1 kHz, 16-bit, stereo, uncompressed); anything else is refused.</summary>
  public static byte[] PcmFromWave(ReadOnlySpan<byte> wav) {
    if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
      throw new InvalidDataException("CloneCD: an audio track needs a RIFF WAVE file.");
    var formatSeen = false;
    for (var at = 12; at + 8 <= wav.Length;) {
      var id = wav.Slice(at, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]);
      var body = at + 8;
      if (size > (uint)(wav.Length - body)) throw new InvalidDataException("CloneCD: a WAVE chunk runs past the end of the file.");
      if (id.SequenceEqual("fmt "u8)) {
        if (size < 16) throw new InvalidDataException("CloneCD: the WAVE format chunk is too short.");
        var f = wav.Slice(body, 16);
        if (BinaryPrimitives.ReadUInt16LittleEndian(f) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(f[2..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(f[4..]) != 44100 || BinaryPrimitives.ReadUInt16LittleEndian(f[14..]) != 16)
          throw new NotSupportedException("CloneCD: an audio track needs CD-DA PCM (44.1 kHz, 16-bit, stereo).");
        formatSeen = true;
      } else if (id.SequenceEqual("data"u8)) {
        if (!formatSeen) throw new InvalidDataException("CloneCD: the WAVE data chunk comes before its format chunk.");
        return wav.Slice(body, (int)size).ToArray();
      }
      at = body + (int)size + (int)(size & 1);
    }
    throw new InvalidDataException("CloneCD: the WAVE file has no data chunk.");
  }

  private static Dictionary<string, Dictionary<string, string>> Sections(string text) {
    var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string>? current = null;
    foreach (var rawLine in text.Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0) continue;
      if (line.StartsWith('[') && line.EndsWith(']')) {
        var name = line[1..^1].Trim();
        if (!sections.TryGetValue(name, out current)) sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
        continue;
      }
      var eq = line.IndexOf('=');
      if (eq > 0 && current != null) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
    }
    return sections;
  }

  private static int Number(string value) {
    value = value.Trim();
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
      : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
  }
}
