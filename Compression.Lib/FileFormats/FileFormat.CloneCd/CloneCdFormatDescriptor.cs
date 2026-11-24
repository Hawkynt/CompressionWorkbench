#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CloneCd;

/// <summary>
/// CloneCD CCD descriptor. A <c>.ccd</c> file is an INI-style text document with
/// <c>[CloneCD]</c>, <c>[Disc]</c>, <c>[Session N]</c>, <c>[Entry N]</c> and
/// <c>[TRACK N]</c> sections describing the layout of an accompanying raw
/// <c>.img</c> sector image (and optional <c>.sub</c> subchannel file).
///
/// <para>This descriptor parses the CCD text and surfaces a <c>metadata.ini</c>
/// distilling the version, disc/session/track counts and per-track modes, plus a
/// verbatim <c>FULL.ccd</c> entry. When the referenced <c>.img</c> / <c>.sub</c>
/// sit next to the <c>.ccd</c> on disk they are also surfaced as data entries, and every
/// track of the TOC is listed as <c>track-NN.iso</c> (Mode 1), <c>track-NN.wav</c> (CD-DA)
/// or <c>track-NN.mode2</c> (Mode 2 subheader and data), under <c>session-NN/</c> after the
/// first session (layout in <c>docs/CLONECD-ON-DISK.md</c>).
/// The raw <c>.ccd</c> never throws on malformed input — it degrades to
/// <c>parse_status=partial</c>.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/CloneCD</c> — background on the creating tool</description></item>
///   <item><description>CCD control-file layout documented by the disc-preservation community (redump.org guides); Elaborate Bytes never published a spec</description></item>
/// </list>
/// </summary>
public sealed class CloneCdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "CloneCd";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CloneCD CCD";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".ccd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".ccd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("[CloneCD]"u8.ToArray(), Confidence: 0.85),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "CloneCD CCD INI descriptor for a raw .img sector image + optional .sub subchannel.";

  // CCD parsing is done against the in-memory text; companion .img/.sub are
  // discovered only when the descriptor knows the source path. The registry
  // hands us a Stream, so co-located file discovery is best-effort via a
  // FileStream's Name when available.

  private sealed record CcdSection(string Name, Dictionary<string, string> Values);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var metadataSize = Encoding.UTF8.GetByteCount(BuildMetadataIni(ReadText(stream, out _)));
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.ccd", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", metadataSize, metadataSize, "Stored", false, false, null),
    };

    var idx = 2;
    foreach (var (name, len) in DiscoverCompanions(stream))
      entries.Add(new ArchiveEntryInfo(idx++, name, len, len, "Stored", false, false, null));
    foreach (var (track, length) in Tracks(stream))
      entries.Add(new ArchiveEntryInfo(idx++, track.EntryName, length, (long)(track.DataEnd - track.Index1) * CloneCdWriter.RawSectorSize,
        track.Kind.ToString(), false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var text = ReadText(stream, out var full);
    if (Wants(files, "FULL.ccd"))
      WriteFile(outputDir, "FULL.ccd", full);

    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(text)));

    foreach (var (name, _) in DiscoverCompanions(stream)) {
      if (!Wants(files, name)) continue;
      var path = CompanionPath(stream, name);
      if (path != null && File.Exists(path))
        WriteFile(outputDir, name, File.ReadAllBytes(path));
    }

    foreach (var (track, _) in Tracks(stream))
      if (Wants(files, track.EntryName))
        WriteFile(outputDir, track.EntryName, ReadTrack(stream, track));
  }

  /// <summary>
  /// Opens only the requested logical entry. Raw companion tracks are streamed
  /// directly from their sibling files instead of being copied to a temporary
  /// extraction directory for every FUSE read handle.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    if (string.Equals(entryName, "FULL.ccd", StringComparison.Ordinal)) {
      if (archive is FileStream file)
        return new FileStream(file.Name, FileMode.Open, FileAccess.Read,
          FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
      ReadText(archive, out var full);
      return new MemoryStream(full, writable: false);
    }

    if (string.Equals(entryName, "metadata.ini", StringComparison.Ordinal))
      return new MemoryStream(
        Encoding.UTF8.GetBytes(BuildMetadataIni(ReadText(archive, out _))),
        writable: false);

    if (DiscoverCompanions(archive).Any(companion =>
          string.Equals(companion.Name, entryName, StringComparison.Ordinal)) &&
        CompanionPath(archive, entryName) is { } path)
      return new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);

    foreach (var (track, _) in Tracks(archive))
      if (string.Equals(track.EntryName, entryName, StringComparison.Ordinal))
        return new MemoryStream(ReadTrack(archive, track), writable: false);

    throw new FileNotFoundException($"CloneCD entry '{entryName}' does not exist.", entryName);
  }

  /// <summary>Gets the size limit of a created image set.</summary>
  public long? MaxTotalArchiveSize => null;

  /// <summary>Gets what <see cref="Create"/> accepts.</summary>
  public string AcceptedInputsDescription =>
    "accepts: tracks in disc order - .iso (Mode 1, whole 2048-byte sectors), .wav (CD-DA PCM: 44.1 kHz, 16-bit, stereo), " +
    ".mode2 (Mode 2, whole 2336-byte sectors: subheader and data); tracks under session-NN/ go to session NN; " +
    "the .img and .sub are written beside the .ccd";

  /// <summary>Accepts ISO images, CD-DA WAV files and Mode 2 sector files, at the root or under <c>session-NN/</c>.</summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.IsDirectory || TrackPath(input.ArchiveName) is { Kind: not null, Session: > 0 }) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is no CloneCD track; {this.AcceptedInputsDescription}";
    return false;
  }

  /// <summary>
  /// Writes the disc the inputs describe, one track per file in input order, sessions from their <c>session-NN/</c>
  /// folders: the control file to <paramref name="output"/>, and the raw sectors and subchannel to <c>.img</c> and
  /// <c>.sub</c> files beside it, so the output has to be a file.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).ToList();
    if (files.Count == 0) throw new ArgumentException("CloneCD: at least one track is needed.");
    var sessions = new SortedDictionary<int, List<CloneCdTrackSource>>();
    foreach (var input in files) {
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var (session, kind) = TrackPath(input.ArchiveName);
      var content = input.ReadContent();
      var data = kind == CloneCdTrackKind.Audio ? CloneCdLayout.PcmFromWave(content) : content;
      if (!sessions.TryGetValue(session, out var list)) sessions[session] = list = [];
      list.Add(new CloneCdTrackSource(kind!.Value, data));
    }
    if (sessions.Keys.Where((k, i) => k != i + 1).Any())
      throw new ArgumentException("CloneCD: sessions must be numbered 1, 2, 3 ... without gaps.");
    if (output is not FileStream file)
      throw new NotSupportedException("CloneCD: an image set is three files (.ccd, .img, .sub), so it can only be created at a file path.");
    var set = CloneCdWriter.Build([.. sessions.Values]);
    var stem = Path.Combine(Path.GetDirectoryName(file.Name) ?? ".", Path.GetFileNameWithoutExtension(file.Name));
    File.WriteAllBytes(stem + ".img", set.Img);
    File.WriteAllBytes(stem + ".sub", set.Sub);
    output.Write(set.Ccd);
  }

  /// <summary>The session (1 at the root, NN under <c>session-NN/</c>) and the track kind its extension names.</summary>
  private static (int Session, CloneCdTrackKind? Kind) TrackPath(string archiveName) {
    var parts = archiveName.Replace('\\', '/').Trim('/').Split('/');
    var session = 1;
    if (parts.Length == 2 && parts[0].StartsWith("session-", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(parts[0].AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
      session = n;
    else if (parts.Length != 1)
      return (0, null);
    CloneCdTrackKind? kind = Path.GetExtension(parts[^1]).ToLowerInvariant() switch {
      ".iso" => CloneCdTrackKind.Mode1,
      ".wav" => CloneCdTrackKind.Audio,
      ".mode2" => CloneCdTrackKind.Mode2,
      _ => null,
    };
    return (session, kind);
  }

  /// <summary>The tracks of the image set and their extracted lengths, when the <c>.img</c> sits beside the control file and matches it.</summary>
  private static List<(CloneCdTrack Track, long Length)> Tracks(Stream stream) {
    var result = new List<(CloneCdTrack, long)>();
    if (ImgPath(stream) is not { } imgPath || !File.Exists(imgPath)) return result;
    var text = ReadText(stream, out _);
    using var img = File.OpenRead(imgPath);
    var layout = CloneCdLayout.TryParse(text, img);
    if (layout == null || layout.ImgSectors * CloneCdWriter.RawSectorSize > img.Length) return result;
    foreach (var track in layout.Tracks)
      if (layout.ExtractedLength(track, img) is var length and > 0)
        result.Add((track, length));
    return result;
  }

  private static byte[] ReadTrack(Stream stream, CloneCdTrack track) {
    using var img = File.OpenRead(ImgPath(stream)!);
    return CloneCdLayout.TryParse(ReadText(stream, out _), img)!.ReadTrack(track, img);
  }

  private static string? ImgPath(Stream stream)
    => SourceDirectory(stream) is { } dir && SourceBaseName(stream) is { } baseName ? Path.Combine(dir, baseName + ".img") : null;

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static IEnumerable<(string Name, long Length)> DiscoverCompanions(Stream stream) {
    var dir = SourceDirectory(stream);
    var baseName = SourceBaseName(stream);
    if (dir == null || baseName == null) yield break;
    foreach (var ext in new[] { ".img", ".sub" }) {
      var candidate = Path.Combine(dir, baseName + ext);
      if (File.Exists(candidate))
        yield return (baseName + ext, new FileInfo(candidate).Length);
    }
  }

  private static string? CompanionPath(Stream stream, string name) {
    var dir = SourceDirectory(stream);
    return dir == null ? null : Path.Combine(dir, name);
  }

  private static string? SourceDirectory(Stream stream)
    => stream is FileStream fs ? Path.GetDirectoryName(fs.Name) : null;

  private static string? SourceBaseName(Stream stream)
    => stream is FileStream fs ? Path.GetFileNameWithoutExtension(fs.Name) : null;

  private static List<CcdSection> Parse(string text, out bool partial) {
    partial = false;
    var sections = new List<CcdSection>();
    CcdSection? current = null;
    try {
      foreach (var rawLine in text.Split('\n')) {
        var line = rawLine.Trim().TrimEnd('\r');
        if (line.Length == 0) continue;
        if (line.StartsWith('[') && line.EndsWith(']')) {
          current = new CcdSection(line[1..^1].Trim(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
          sections.Add(current);
          continue;
        }
        var eq = line.IndexOf('=');
        if (eq <= 0 || current == null) { partial = true; continue; }
        current.Values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
      }
    } catch {
      partial = true;
    }
    return sections;
  }

  private static string BuildMetadataIni(string text) {
    var sections = Parse(text, out var partial);
    var sb = new StringBuilder();
    sb.Append("[CloneCd]\n");

    var header = sections.FirstOrDefault(s => s.Name.Equals("CloneCD", StringComparison.OrdinalIgnoreCase));
    if (header != null && header.Values.TryGetValue("Version", out var ver))
      sb.Append(CultureInfo.InvariantCulture, $"version={ver}\n");

    var disc = sections.FirstOrDefault(s => s.Name.Equals("Disc", StringComparison.OrdinalIgnoreCase));
    if (disc != null) {
      if (disc.Values.TryGetValue("TocEntries", out var toc)) sb.Append(CultureInfo.InvariantCulture, $"toc_entries={toc}\n");
      if (disc.Values.TryGetValue("Sessions", out var sess)) sb.Append(CultureInfo.InvariantCulture, $"sessions={sess}\n");
      if (disc.Values.TryGetValue("DataTracksScrambled", out var ds)) sb.Append(CultureInfo.InvariantCulture, $"data_tracks_scrambled={ds}\n");
    }

    var sessionCount = sections.Count(s => s.Name.StartsWith("Session", StringComparison.OrdinalIgnoreCase));
    var tracks = sections.Where(s => s.Name.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase)).ToList();
    sb.Append(CultureInfo.InvariantCulture, $"session_count={sessionCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"track_count={tracks.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(partial ? "partial" : "ok")}\n");

    foreach (var t in tracks) {
      var num = t.Name["TRACK ".Length..].Trim();
      sb.Append(CultureInfo.InvariantCulture, $"\n[Track{num}]\n");
      if (t.Values.TryGetValue("MODE", out var mode)) sb.Append(CultureInfo.InvariantCulture, $"mode={mode}\n");
      if (t.Values.TryGetValue("INDEX 1", out var idx1)) sb.Append(CultureInfo.InvariantCulture, $"index1={idx1}\n");
      if (t.Values.TryGetValue("INDEX 0", out var idx0)) sb.Append(CultureInfo.InvariantCulture, $"index0={idx0}\n");
    }
    return sb.ToString();
  }

  private static string ReadText(Stream stream, out byte[] full) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    full = ms.ToArray();
    return Encoding.UTF8.GetString(full);
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;
}
