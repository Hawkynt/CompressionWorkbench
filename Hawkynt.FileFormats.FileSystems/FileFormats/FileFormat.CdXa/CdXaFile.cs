#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Cdi;

namespace FileFormat.CdXa;

/// <summary>
/// A RIFF CDXA file: how a CD-ROM XA (Mode 2) file, typically a Video CD MPEG track <c>AVSEQnn.DAT</c>,
/// appears when it is copied off a disc. Layout: <c>RIFF</c> size <c>CDXA</c>, a 16-byte <c>fmt </c>
/// chunk (the file's XA directory attributes) and a <c>data</c> chunk of raw 2352-byte sectors. A
/// sector is 12 sync bytes, a BCD MSF address and mode byte (2), the 8-byte subheader (file, channel,
/// submode, coding info, twice), 2324 bytes of Form 2 user data and the 4-byte EDC over subheader and
/// user data (ECMA-130). Layout in <c>docs/CDXA-ON-DISK.md</c>.
/// </summary>
public sealed class CdXaFile {
  public const int SectorSize = 2352;
  public const int UserDataSize = 2324;
  public const int FmtSize = 16;

  /// <summary>Submode bits (CD-ROM XA): end of record, video, audio, data, trigger, Form 2, real time, end of file.</summary>
  public const byte EndOfRecord = 0x01, Video = 0x02, Audio = 0x04, Form2 = 0x20, RealTime = 0x40, EndOfFile = 0x80;

  private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  /// <summary>Where the end-of-record bit goes: on the last sector of each stream, or only on the last data sector.</summary>
  public enum RecordEnds { LastOfEachStream, LastSector }

  /// <summary>The parameters that, with the MPEG packs, determine every sector of a Video CD track.</summary>
  public sealed record Layout(int StartFrame, byte FileNumber, int LeadSectors, int TrailSectors, RecordEnds RecordEnds);

  public byte[] Fmt { get; init; } = new byte[FmtSize];

  /// <summary>The raw sectors (the data chunk).</summary>
  public byte[] Data { get; init; } = [];

  public int SectorCount => this.Data.Length / SectorSize;

  public static CdXaFile Read(ReadOnlySpan<byte> file) {
    if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("CDXA"u8))
      throw new InvalidDataException("CD-XA: missing 'RIFF' ... 'CDXA' header.");
    var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
    if (riffEnd > file.Length) throw new InvalidDataException("CD-XA: the RIFF size runs past the end of the file.");
    byte[]? fmt = null, data = null;
    var pos = 12;
    while (pos + 8 <= riffEnd) {
      var id = file.Slice(pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(file[(pos + 4)..]);
      if (pos + 8 + (long)size > riffEnd) throw new InvalidDataException("CD-XA: a chunk runs past the RIFF size.");
      var body = file.Slice(pos + 8, (int)size);
      if (id.SequenceEqual("fmt "u8)) fmt = body.ToArray();
      else if (id.SequenceEqual("data"u8)) data = body.ToArray();
      pos += 8 + (int)size + (int)(size & 1);
    }
    if (fmt is not { Length: FmtSize }) throw new InvalidDataException("CD-XA: the fmt chunk is missing or not 16 bytes.");
    if (data is null) throw new InvalidDataException("CD-XA: the data chunk is missing.");
    if (data.Length % SectorSize != 0) throw new InvalidDataException("CD-XA: the data chunk is not whole 2352-byte sectors.");
    for (var s = 0; s < data.Length / SectorSize; ++s) {
      var sector = data.AsSpan(s * SectorSize, SectorSize);
      if (!sector[..12].SequenceEqual(Sync) || sector[15] != 2) throw new InvalidDataException($"CD-XA: sector {s} is not a Mode 2 sector.");
    }
    return new CdXaFile { Fmt = fmt, Data = data };
  }

  public void Write(Stream output) {
    if (this.Fmt.Length != FmtSize) throw new InvalidDataException("CD-XA: the fmt chunk must be 16 bytes.");
    var head = new byte[44];
    "RIFF"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)(36 + this.Data.Length));
    "CDXA"u8.CopyTo(head.AsSpan(8));
    "fmt "u8.CopyTo(head.AsSpan(12));
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), FmtSize);
    this.Fmt.CopyTo(head, 20);
    "data"u8.CopyTo(head.AsSpan(36));
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(40), (uint)this.Data.Length);
    output.Write(head);
    output.Write(this.Data);
  }

  private ReadOnlySpan<byte> UserData(int s) => this.Data.AsSpan(s * SectorSize + 24, UserDataSize);

  private bool IsEmpty(int s) => !this.UserData(s).ContainsAnyExcept((byte)0);

  /// <summary>The user data from the first to the last sector that is not all zero, as cdxa2mpeg extracts it.</summary>
  public byte[] Track() {
    var first = -1;
    var last = -1;
    for (var s = 0; s < this.SectorCount; ++s)
      if (!this.IsEmpty(s)) {
        if (first < 0) first = s;
        last = s;
      }
    if (first < 0) return [];
    var track = new byte[(last - first + 1) * UserDataSize];
    for (var s = first; s <= last; ++s) this.UserData(s).CopyTo(track.AsSpan((s - first) * UserDataSize));
    return track;
  }

  /// <summary>The layout this file was written with, when rebuilding it from its track reproduces it exactly.</summary>
  public Layout? DetectLayout() {
    if (this.SectorCount == 0) return null;
    var lead = 0;
    while (lead < this.SectorCount && this.IsEmpty(lead)) ++lead;
    var trail = 0;
    while (trail < this.SectorCount - lead && this.IsEmpty(this.SectorCount - 1 - trail)) ++trail;
    var startFrame = FrameOf(this.Data.AsSpan(12, 3));
    if (startFrame < 0) return null;
    var track = this.Track();
    foreach (var ends in (RecordEnds[])[RecordEnds.LastSector, RecordEnds.LastOfEachStream]) {
      var layout = new Layout(startFrame, this.Data[16], lead, trail, ends);
      try {
        if (Build(track, layout).AsSpan().SequenceEqual(this.Data)) return layout;
      } catch (InvalidDataException) {
        return null;
      }
    }
    return null;
  }

  /// <summary>
  /// The sectors of a Video CD MPEG track: <paramref name="layout"/>.LeadSectors empty sectors, one sector per
  /// MPEG pack (video packs 0x62/0x0F, audio packs 0x64/0x7F, channel 1), then the trailing empty sectors, the last
  /// of which ends the file. Empty sectors are channel 0, submode 0x60.
  /// </summary>
  public static byte[] Build(ReadOnlySpan<byte> mpeg, Layout layout) {
    if (layout.TrailSectors < 1) throw new InvalidDataException("CD-XA: a track needs at least one trailing sector to carry the end of file.");
    var packs = SplitPacks(mpeg);
    if (packs.Count == 0) throw new InvalidDataException("CD-XA: no MPEG packs to write.");
    var total = layout.LeadSectors + packs.Count + layout.TrailSectors;
    var data = new byte[(long)total * SectorSize];
    var lastVideo = packs.FindLastIndex(p => p.Kind == Video);
    var lastAudio = packs.FindLastIndex(p => p.Kind == Audio);
    for (var s = 0; s < total; ++s) {
      var sector = data.AsSpan(s * SectorSize, SectorSize);
      byte channel = 0, submode = RealTime | Form2, coding = 0;
      var p = s - layout.LeadSectors;
      if (p >= 0 && p < packs.Count) {
        var (offset, length, kind) = packs[p];
        mpeg.Slice(offset, length).CopyTo(sector[24..]);
        channel = 1;
        submode |= kind;
        coding = kind == Video ? (byte)0x0F : (byte)0x7F;
        var endsRecord = layout.RecordEnds == RecordEnds.LastSector ? p == packs.Count - 1 : p == lastVideo || p == lastAudio;
        if (endsRecord) submode |= EndOfRecord;
      } else if (s == total - 1) {
        submode |= EndOfFile;
      }
      Sync.CopyTo(sector);
      WriteMsf(sector[12..], layout.StartFrame + s);
      sector[15] = 2;
      sector[16] = sector[20] = layout.FileNumber;
      sector[17] = sector[21] = channel;
      sector[18] = sector[22] = submode;
      sector[19] = sector[23] = coding;
      BinaryPrimitives.WriteUInt32LittleEndian(sector[2348..], CdiCdSectorIntegrity.ComputeEdc(sector[16..2348]));
    }
    return data;
  }

  /// <summary>
  /// Splits an MPEG program stream into its packs, skipping zero padding between them. Each pack is
  /// classified by its first PES stream (0xE0-0xEF video, 0xC0-0xDF audio) or, when it carries none, by
  /// the first stream its system header lists.
  /// </summary>
  public static List<(int Offset, int Length, byte Kind)> SplitPacks(ReadOnlySpan<byte> mpeg) {
    var packs = new List<(int, int, byte)>();
    var pos = 0;
    while (pos < mpeg.Length) {
      if (!IsStartCode(mpeg, pos, 0xBA)) {
        var next = pos;
        while (next < mpeg.Length && mpeg[next] == 0 && !IsStartCode(mpeg, next, 0xBA)) ++next;
        if (next == mpeg.Length) break;
        if (!IsStartCode(mpeg, next, 0xBA)) throw new InvalidDataException($"CD-XA: no MPEG pack at offset {pos}.");
        pos = next;
      }
      var start = pos;
      if (pos + 5 > mpeg.Length) throw new InvalidDataException("CD-XA: truncated pack header.");
      pos += (mpeg[pos + 4] & 0xC0) == 0x40
        ? 14 + (pos + 14 <= mpeg.Length ? mpeg[pos + 13] & 7 : 0) // MPEG-2
        : 12; // MPEG-1
      byte kind = 0;
      byte listed = 0;
      while (pos + 4 <= mpeg.Length && mpeg[pos] == 0 && mpeg[pos + 1] == 0 && mpeg[pos + 2] == 1 && mpeg[pos + 3] != 0xBA) {
        var id = mpeg[pos + 3];
        if (id == 0xB9) { pos += 4; continue; }
        if (pos + 6 > mpeg.Length) throw new InvalidDataException("CD-XA: truncated PES header.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(mpeg[(pos + 4)..]);
        if (id == 0xBB && listed == 0 && pos + 13 <= mpeg.Length) listed = mpeg[pos + 12];
        if (kind == 0) kind = Classify(id);
        pos += 6 + length;
      }
      if (pos > mpeg.Length) throw new InvalidDataException("CD-XA: a PES packet runs past the end of the stream.");
      if (pos - start > UserDataSize) throw new InvalidDataException($"CD-XA: the pack at offset {start} is {pos - start} bytes; a sector holds {UserDataSize}.");
      if (kind == 0) kind = Classify(listed);
      if (kind == 0) throw new InvalidDataException($"CD-XA: the pack at offset {start} carries neither video nor audio.");
      packs.Add((start, pos - start, kind));
    }
    return packs;
  }

  private static byte Classify(byte streamId) => streamId switch {
    >= 0xE0 and <= 0xEF => Video,
    >= 0xC0 and <= 0xDF => Audio,
    _ => 0,
  };

  private static bool IsStartCode(ReadOnlySpan<byte> d, int at, byte id)
    => at + 4 <= d.Length && d[at] == 0 && d[at + 1] == 0 && d[at + 2] == 1 && d[at + 3] == id;

  /// <summary>Frames (75 per second) of a BCD minute/second/frame address, or -1 when it is not BCD.</summary>
  public static int FrameOf(ReadOnlySpan<byte> msf) {
    int Bcd(byte b) => (b >> 4) <= 9 && (b & 15) <= 9 ? (b >> 4) * 10 + (b & 15) : -1;
    var (m, s, f) = (Bcd(msf[0]), Bcd(msf[1]), Bcd(msf[2]));
    return m < 0 || s is < 0 or > 59 || f is < 0 or > 74 ? -1 : (m * 60 + s) * 75 + f;
  }

  public static void WriteMsf(Span<byte> msf, int frame) {
    if (frame is < 0 or >= 100 * 60 * 75) throw new InvalidDataException("CD-XA: sector address out of range.");
    static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
    msf[0] = Bcd(frame / 4500);
    msf[1] = Bcd(frame / 75 % 60);
    msf[2] = Bcd(frame % 75);
  }

  public static string Msf(int frame) => $"{frame / 4500:D2}:{frame / 75 % 60:D2}:{frame % 75:D2}";

  public static int ParseMsf(string text) {
    var parts = text.Split(':');
    if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var m) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var s)
        || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var f) || m is < 0 or > 99 || s is < 0 or > 59 || f is < 0 or > 74)
      throw new ArgumentException($"CD-XA: '{text}' is not an MM:SS:FF address.");
    return (m * 60 + s) * 75 + f;
  }

  /// <summary>The fmt chunk of a Form 2 file: owner 0/0, attributes 0x1555 (as on Video CD tracks), "XA", the file number.</summary>
  public static byte[] DefaultFmt(byte fileNumber) => [0, 0, 0, 0, 0x15, 0x55, (byte)'X', (byte)'A', fileNumber, 0, 0, 0, 0, 0, 0, 0];

  public string RenderMetadata(Layout? layout) {
    var sb = new StringBuilder("[CdXa]\n");
    sb.Append(CultureInfo.InvariantCulture, $"sectors={this.SectorCount}\nfmt={Convert.ToHexString(this.Fmt)}\n");
    if (this.SectorCount > 0) {
      var first = FrameOf(this.Data.AsSpan(12, 3));
      if (first >= 0) sb.Append("first_sector_msf=").Append(Msf(first)).Append('\n');
      sb.Append(CultureInfo.InvariantCulture, $"file_number={this.Data[16]}\n");
    }
    if (layout is null) {
      sb.Append("layout=other\n");
    } else {
      sb.Append(CultureInfo.InvariantCulture, $"layout=vcd\nlead_sectors={layout.LeadSectors}\ntrail_sectors={layout.TrailSectors}\n");
      sb.Append("record_ends=").Append(layout.RecordEnds == RecordEnds.LastSector ? "last_sector" : "last_of_each_stream").Append('\n');
    }
    return sb.ToString();
  }
}
