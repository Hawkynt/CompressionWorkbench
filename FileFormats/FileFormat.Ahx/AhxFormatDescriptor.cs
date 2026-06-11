#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ahx;

/// <summary>
/// Exposes an Amiga AHX / THX synth-tracker module as a read-only pseudo-archive of
/// <c>FULL.ahx</c>, <c>metadata.ini</c> and the raw position/track/instrument blocks.
/// The big-endian, offset-based AHX layout was recovered through binary inspection
/// of the documented THX file format and the OpenMPT loader. No synth is emulated;
/// every offset read is clamped, and a malformed module surfaces
/// FULL + metadata(parse_status=partial) instead of throwing.
/// </summary>
public sealed class AhxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Ahx";
  public string DisplayName => "AHX / THX Synth-Tracker";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ahx";
  public IReadOnlyList<string> Extensions => [".ahx", ".thx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("THX"u8.ToArray(), Offset: 0, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "Amiga AHX / THX synth-tracker module surfaced as a read-only pseudo-archive " +
    "(FULL + metadata + raw position/track/instrument blocks); the synth is never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Decompose(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in Decompose(ReadAll(stream))) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private readonly record struct Entry(string Name, byte[] Data, string Kind);

  private static List<Entry> Decompose(byte[] f) {
    var entries = new List<Entry> { new("FULL.ahx", f, "Track") };
    var meta = new StringBuilder().AppendLine("[ahx]");
    var ok = false;

    try {
      // Header: "THX" + version byte, then (big-endian) name/title pointer u16 @4,
      // flags/speed-multiplier @6, then length/restart/track/positions/instr counts.
      if (f.Length >= 14 && f[0] == 'T' && f[1] == 'H' && f[2] == 'X') {
        var version = f[3];
        var titleOff = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4, 2));
        // Word @6: top bit = TrackLength-0 omitted flag; rest = speed multiplier.
        var flags = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(6, 2));
        var lenPosNr = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(8, 2));
        var restart = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(10, 2));
        var trackLen = f[12];
        var trackNr = f[13];
        var instrNr = f.Length >= 16 ? f[14] : 0;
        var subSongNr = f.Length >= 16 ? f[15] : 0;

        meta.Append("magic = THX\n");
        meta.Append("version = ").Append(version).Append('\n');
        meta.Append("positions = ").Append(lenPosNr).Append('\n');
        meta.Append("restart = ").Append(restart).Append('\n');
        meta.Append("track_length = ").Append(trackLen).Append('\n');
        meta.Append("num_tracks = ").Append(trackNr).Append('\n');
        meta.Append("num_instruments = ").Append(instrNr).Append('\n');
        meta.Append("subsongs = ").Append(subSongNr).Append('\n');
        meta.Append("speed_multiplier = ").Append((flags >> 13) & 0x07).Append('\n');

        // Subsong table (subSongNr u16) starts at offset 16.
        var pos = 16 + subSongNr * 2;

        // Position list: lenPosNr positions x 4 tracks x 2 bytes (track, transpose).
        var posBytes = lenPosNr * 4 * 2;
        if (InRange(f, pos, posBytes) && posBytes > 0)
          entries.Add(new("positions.bin", f.AsSpan(pos, posBytes).ToArray(), "Pattern"));
        pos += posBytes;

        // Track data: (TrackNr [+1 when track 0 not omitted]) tracks x trackLen x 3 bytes.
        var trackZeroOmitted = (flags & 0x8000) != 0;
        var totalTracks = trackNr + (trackZeroOmitted ? 0 : 1);
        var trackBytes = totalTracks * trackLen * 3;
        if (InRange(f, pos, trackBytes) && trackBytes > 0)
          entries.Add(new("tracks.bin", f.AsSpan(pos, trackBytes).ToArray(), "Pattern"));
        pos += trackBytes;

        // Instrument data: variable-length; surface the remaining bytes up to the
        // title block (titleOff) when that is sane, else to EOF.
        var instrEnd = titleOff is > 0 && titleOff <= f.Length && titleOff > pos
          ? titleOff
          : f.Length;
        if (InRange(f, pos, instrEnd - pos) && instrEnd - pos > 0)
          entries.Add(new("instruments.bin", f.AsSpan(pos, instrEnd - pos).ToArray(), "Sample"));

        // Title + instrument names live as NUL-separated strings at titleOff.
        if (titleOff is > 0 && titleOff < f.Length) {
          var title = ReadCString(f, titleOff);
          if (title.Length > 0) meta.Append("title = ").Append(title).Append('\n');
        }

        ok = true;
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  private static string ReadCString(byte[] f, int off) {
    var sb = new StringBuilder();
    for (var i = off; i < f.Length; ++i) {
      var b = f[i];
      if (b == 0) break;
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static bool InRange(byte[] f, int off, int len) =>
    off >= 0 && len >= 0 && (long)off + len <= f.Length;
}
