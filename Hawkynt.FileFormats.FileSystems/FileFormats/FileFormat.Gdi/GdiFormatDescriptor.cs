#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gdi;

/// <summary>One line of a GDI track table.</summary>
public sealed record GdiTrack(int Number, long StartLba, int Type, int SectorSize, string FileName, long Offset) {
  public bool IsData => this.Type == 4;
}

/// <summary>
/// Dreamcast GD-ROM track list (<c>.gdi</c>): a text manifest — a track count, then one line per
/// track (number, start LBA, type 0 audio / 4 data, sector size, file name, offset) — naming the
/// track files that sit beside it. Lists <c>metadata.ini</c> and every track file it can find next
/// to the manifest, byte for byte. Read-only: a single-stream archive cannot write the sibling
/// track files a GDI consists of.
/// </summary>
public sealed class GdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ISyntheticEntryNames {
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "Gdi";
  public string DisplayName => "Dreamcast GDI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gdi";
  public IReadOnlyList<string> Extensions => [".gdi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Track files")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Dreamcast GD-ROM track list (.gdi) with its sibling track files";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  /// <summary>Parses the manifest text; file names may be quoted (and then contain spaces).</summary>
  /// <exception cref="InvalidDataException">The text is not a GDI track table.</exception>
  public static List<GdiTrack> Parse(string text) {
    var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    if (lines.Count == 0 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 99)
      throw new InvalidDataException("GDI: the first line is not a track count.");
    if (lines.Count - 1 < count) throw new InvalidDataException($"GDI: {count} tracks declared, {lines.Count - 1} listed.");
    var tracks = new List<GdiTrack>(count);
    for (var i = 1; i <= count; ++i) {
      var fields = Tokenize(lines[i]);
      if (fields.Count < 6) throw new InvalidDataException($"GDI: track line {i} has {fields.Count} fields, expected 6.");
      try {
        var track = new GdiTrack(int.Parse(fields[0], CultureInfo.InvariantCulture), long.Parse(fields[1], CultureInfo.InvariantCulture),
          int.Parse(fields[2], CultureInfo.InvariantCulture), int.Parse(fields[3], CultureInfo.InvariantCulture), fields[4],
          long.Parse(fields[5], CultureInfo.InvariantCulture));
        if (track.Number != i || track.Type is not (0 or 4) || track.SectorSize is not (2048 or 2336 or 2352) ||
            track.FileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(track.FileName))
          throw new InvalidDataException($"GDI: track line {i} is out of range.");
        tracks.Add(track);
      } catch (FormatException) {
        throw new InvalidDataException($"GDI: track line {i} has a non-numeric field.");
      }
    }
    return tracks;
  }

  private static List<string> Tokenize(string line) {
    var result = new List<string>();
    var i = 0;
    while (i < line.Length) {
      while (i < line.Length && char.IsWhiteSpace(line[i])) ++i;
      if (i >= line.Length) break;
      if (line[i] == '"') {
        var end = line.IndexOf('"', i + 1);
        if (end < 0) throw new InvalidDataException("GDI: unterminated quoted file name.");
        result.Add(line[(i + 1)..end]);
        i = end + 1;
      } else {
        var start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) ++i;
        result.Add(line[start..i]);
      }
    }
    return result;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var (tracks, folder) = Read(stream);
      var result = new List<ArchiveEntryInfo> { new(0, MetadataName, 0, 0, "stored", false, false, null) };
      foreach (var t in tracks) {
        var path = folder is null ? null : Path.Combine(folder, t.FileName);
        if (path is null || !File.Exists(path)) continue;
        var size = new FileInfo(path).Length;
        result.Add(new(result.Count, t.FileName, size, size, t.IsData ? "data" : "audio", false, false, null));
      }
      return result;
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.gdi", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (tracks, folder) = Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(tracks, folder));
    foreach (var t in tracks) {
      if (files is { Length: > 0 } && !MatchesFilter(t.FileName, files)) continue;
      var path = folder is null ? null : Path.Combine(folder, t.FileName);
      if (path is null || !File.Exists(path)) {
        if (files is { Length: > 0 }) throw new FileNotFoundException($"GDI: track file '{t.FileName}' is not beside the manifest.");
        continue;
      }
      using var source = File.OpenRead(path);
      using var target = CreateEntryFile(outputDir, t.FileName);
      source.CopyTo(target);
    }
  }

  private static (List<GdiTrack> Tracks, string? Folder) Read(Stream stream) {
    if (stream.CanSeek) {
      if (stream.Length > 64 * 1024) throw new InvalidDataException("GDI: too large to be a track list.");
      stream.Position = 0;
    }
    using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var tracks = Parse(reader.ReadToEnd());
    var folder = stream is FileStream fs ? Path.GetDirectoryName(Path.GetFullPath(fs.Name)) : null;
    return (tracks, folder);
  }

  private static byte[] Metadata(List<GdiTrack> tracks, string? folder) {
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder("[gdi]\n");
    sb.Append(inv, $"tracks={tracks.Count}\n");
    foreach (var t in tracks) {
      var present = folder is not null && File.Exists(Path.Combine(folder, t.FileName));
      sb.Append(inv, $"[track {t.Number:D2}]\nfile={t.FileName}\nstart_lba={t.StartLba}\ntype={(t.IsData ? "data" : "audio")}\n");
      sb.Append(inv, $"sector_size={t.SectorSize}\noffset={t.Offset}\npresent={(present ? "yes" : "no")}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
