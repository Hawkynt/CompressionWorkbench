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
/// sit next to the <c>.ccd</c> on disk they are also surfaced as data entries.
/// The raw <c>.ccd</c> never throws on malformed input — it degrades to
/// <c>parse_status=partial</c>.</para>
/// </summary>
public sealed class CloneCdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "CloneCd";
  public string DisplayName => "CloneCD CCD";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ccd";
  public IReadOnlyList<string> Extensions => [".ccd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("[CloneCD]"u8.ToArray(), Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "CloneCD CCD INI descriptor for a raw .img sector image + optional .sub subchannel.";

  // CCD parsing is done against the in-memory text; companion .img/.sub are
  // discovered only when the descriptor knows the source path. The registry
  // hands us a Stream, so co-located file discovery is best-effort via a
  // FileStream's Name when available.

  private sealed record CcdSection(string Name, Dictionary<string, string> Values);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.ccd", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };

    var idx = 2;
    foreach (var (name, len) in DiscoverCompanions(stream))
      entries.Add(new ArchiveEntryInfo(idx++, name, len, len, "Stored", false, false, null));
    return entries;
  }

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
  }

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
