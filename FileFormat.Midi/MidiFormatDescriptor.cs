#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.Midi;
using Compression.Registry;

namespace FileFormat.Midi;

/// <summary>
/// Surfaces a Standard MIDI File as an archive: one <c>FULL.mid</c>, one
/// <c>track_NN_&lt;name&gt;.mid</c> per <c>MTrk</c> chunk (re-wrapped as a format-0
/// single-track file), one <c>metadata.ini</c> carrying song title / copyright /
/// tempo / time signature, and <c>lyrics.txt</c> if lyric meta-events are present.
/// </summary>
public sealed class MidiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveWriteConstraints {
  public string Id => "Midi";
  public string DisplayName => "MIDI (Standard MIDI File)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mid";
  public IReadOnlyList<string> Extensions => [".mid", ".midi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MThd"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Standard MIDI File; per-track extraction + tempo/lyrics.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.mid", "Track", blob),
    };

    var codec = new MidiCodec();
    var header = codec.ReadHeader(blob);
    var tracks = codec.FindTracks(blob);

    string? songTitle = null;
    string? copyright = null;
    double? bpm = null;
    string? timeSig = null;
    string? keySig = null;
    var lyricsBuilder = new StringBuilder();
    var trackNames = new Dictionary<int, string>();

    foreach (var t in tracks) {
      foreach (var ev in codec.ParseMetaEvents(blob, t)) {
        switch (ev.Type) {
          case 0x01: lyricsBuilder.AppendLine("[text] " + Encoding.UTF8.GetString(ev.Data)); break;
          case 0x02: copyright ??= Encoding.UTF8.GetString(ev.Data); break;
          case 0x03:
            var name = Encoding.UTF8.GetString(ev.Data);
            trackNames[t.Index] = name;
            if (t.Index == 0) songTitle ??= name;
            break;
          case 0x05: lyricsBuilder.AppendLine(Encoding.UTF8.GetString(ev.Data)); break;
          case 0x51:
            if (bpm == null && ev.Data.Length >= 3) {
              var microsPerQuarter = (ev.Data[0] << 16) | (ev.Data[1] << 8) | ev.Data[2];
              if (microsPerQuarter > 0)
                bpm = 60_000_000.0 / microsPerQuarter;
            }
            break;
          case 0x58:
            if (timeSig == null && ev.Data.Length >= 2)
              timeSig = $"{ev.Data[0]}/{1 << ev.Data[1]}";
            break;
          case 0x59:
            if (keySig == null && ev.Data.Length >= 2)
              keySig = $"{(sbyte)ev.Data[0]} sharps/flats, {(ev.Data[1] == 0 ? "major" : "minor")}";
            break;
        }
      }
    }

    // Per-track format-0 files.
    foreach (var t in tracks) {
      trackNames.TryGetValue(t.Index, out var name);
      var safeName = Sanitize(name) ?? "untitled";
      var trackBytes = codec.ExtractTrackBytes(blob, t);
      var trackFile = codec.BuildSingleTrackFile(trackBytes, header.Division);
      entries.Add(($"track_{t.Index:D2}_{safeName}.mid", "Track", trackFile));
    }

    // Metadata ini.
    var ini = new StringBuilder();
    ini.AppendLine("; SMF metadata");
    ini.Append("format=").AppendLine(header.Format.ToString(CultureInfo.InvariantCulture));
    ini.Append("tracks=").AppendLine(tracks.Count.ToString(CultureInfo.InvariantCulture));
    ini.Append("division=").AppendLine(header.Division.ToString(CultureInfo.InvariantCulture));
    if (songTitle != null) ini.Append("title=").AppendLine(songTitle);
    if (copyright != null) ini.Append("copyright=").AppendLine(copyright);
    if (bpm.HasValue) ini.Append("tempo_bpm=").AppendLine(bpm.Value.ToString("0.00", CultureInfo.InvariantCulture));
    if (timeSig != null) ini.Append("time_signature=").AppendLine(timeSig);
    if (keySig != null) ini.Append("key_signature=").AppendLine(keySig);
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    if (lyricsBuilder.Length > 0)
      entries.Add(("lyrics.txt", "Tag", Encoding.UTF8.GetBytes(lyricsBuilder.ToString())));

    return entries;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "MIDI archive accepts: FULL.mid, track_NN_*.mid, metadata.ini, lyrics.txt";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = System.IO.Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.mid" or "metadata.ini" or "lyrics.txt" ||
        (name.StartsWith("track_") && name.EndsWith(".mid"))) {
      reason = null; return true;
    }
    reason = $"not a MIDI-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static string? Sanitize(string? s) {
    if (string.IsNullOrEmpty(s)) return null;
    var sb = new StringBuilder(Math.Min(s.Length, 40));
    foreach (var c in s) {
      if (sb.Length >= 40) break;
      if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
    }
    return sb.Length > 0 ? sb.ToString().Trim('_') : null;
  }
}
