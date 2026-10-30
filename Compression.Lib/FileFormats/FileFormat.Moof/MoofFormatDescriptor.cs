#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Moof;

/// <summary>
/// Applesauce MOOF Macintosh 3.5" disk image. Lists <c>metadata.ini</c> (INFO fields and every
/// track's bit count), <c>info.bin</c>, <c>tmap.bin</c>, <c>flux.bin</c> (when present),
/// <c>meta.ini</c> and <c>tracks/trackNNN.bin</c>. Creates images from those parts and edits
/// tracks; every rebuild gets a fresh TRK table and CRC.
/// </summary>
public sealed partial class MoofFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.ini" };

  [GeneratedRegex(@"^(?:.*/)?track(\d{3})\.bin$", RegexOptions.IgnoreCase)]
  private static partial Regex TrackName();

  public string Id => "Moof";
  public string DisplayName => "Applesauce MOOF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".moof";
  public IReadOnlyList<string> Extensions => [".moof"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x4D, 0x4F, 0x4F, 0x46, 0xFF, 0x0A, 0x0D, 0x0A], Confidence: 0.97)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bitstream", "Bitstream/flux tracks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Applesauce MOOF Macintosh 3.5\" disk image (INFO/TMAP/TRKS/FLUX/META)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription => "accepts: tracks/trackNNN.bin, info.bin, tmap.bin, flux.bin, meta.ini, metadata.ini (track_NNN_bits)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    if (TrackName().IsMatch(name) || Path.GetFileName(name).ToLowerInvariant() is "info.bin" or "tmap.bin" or "flux.bin" or "meta.ini" or "metadata.ini") {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not a MOOF member; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.moof", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var image = this.Apply(new MoofImage(), inputs, freshMap: true);
    image.Write(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var image = this.Apply(Read(archive), inputs, freshMap: false);
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var image = Read(archive);
    foreach (var raw in entryNames) {
      var name = raw.Replace('\\', '/');
      var m = TrackName().Match(name);
      if (m.Success) {
        var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        image.Tracks.Remove(t);
        for (var i = 0; i < MoofImage.MapSize; ++i) {
          if (image.Tmap[i] == t) image.Tmap[i] = 0xFF;
          if (image.FluxMap is not null && image.FluxMap[i] == t) image.FluxMap[i] = 0xFF;
        }
      } else if (Path.GetFileName(name).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) image.Meta = null;
      else if (Path.GetFileName(name).Equals("flux.bin", StringComparison.OrdinalIgnoreCase)) image.FluxMap = null;
      else if (!Synthetic.Contains(Path.GetFileName(name))) throw new NotSupportedException($"MOOF: '{raw}' cannot be removed.");
    }
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  private MoofImage Apply(MoofImage image, IReadOnlyList<ArchiveInputInfo> inputs, bool freshMap) {
    var bitCounts = new Dictionary<int, uint>();
    var newTracks = new Dictionary<int, byte[]>();
    var tmapGiven = false;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var name = input.ArchiveName.Replace('\\', '/');
      var content = input.ReadContent();
      var m = TrackName().Match(name);
      if (m.Success) { newTracks[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = content; continue; }
      switch (Path.GetFileName(name).ToLowerInvariant()) {
        case "info.bin": image.Info = Exact(content, MoofImage.InfoSize, "info.bin"); break;
        case "tmap.bin": image.Tmap = Exact(content, MoofImage.MapSize, "tmap.bin"); tmapGiven = true; break;
        case "flux.bin": image.FluxMap = Exact(content, MoofImage.MapSize, "flux.bin"); break;
        case "meta.ini": image.Meta = FromMetaIni(Encoding.UTF8.GetString(content)); break;
        case "metadata.ini":
          foreach (var line in Encoding.UTF8.GetString(content).Split('\n')) {
            var l = line.Trim();
            if (!l.StartsWith("track_", StringComparison.Ordinal) || !l.Contains("_bits=", StringComparison.Ordinal)) continue;
            bitCounts[int.Parse(l.AsSpan(6, 3), CultureInfo.InvariantCulture)] = uint.Parse(l[(l.IndexOf('=') + 1)..], CultureInfo.InvariantCulture);
          }
          break;
      }
    }
    foreach (var (t, data) in newTracks) {
      var bits = bitCounts.TryGetValue(t, out var b) ? b
        : image.FluxMap is not null && Array.IndexOf(image.FluxMap, (byte)t) >= 0 ? (uint)data.Length : (uint)data.Length * 8;
      image.Tracks[t] = (data, bits);
    }
    foreach (var (t, bits) in bitCounts)
      if (!newTracks.ContainsKey(t) && image.Tracks.TryGetValue(t, out var existing))
        image.Tracks[t] = (existing.Data, bits);
    if (freshMap && !tmapGiven) {
      // Track index i is track i / 2, side i % 2 — the same order the map uses.
      image.Tmap = MoofImage.EmptyMap();
      foreach (var t in image.Tracks.Keys) if (t < MoofImage.MapSize) image.Tmap[t] = (byte)t;
    }
    return image;
  }

  private static byte[] Exact(byte[] content, int size, string name)
    => content.Length == size ? content : throw new ArgumentException($"MOOF {name} is {size} bytes, not {content.Length}.");

  private static string? FromMetaIni(string ini) {
    var lines = ini.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains('=') && !l.StartsWith('['))
      .Select(l => { var i = l.IndexOf('='); return l[..i].Trim() + "\t" + l[(i + 1)..].Trim(); }).ToList();
    return lines.Count == 0 ? null : string.Join("\n", lines) + "\n";
  }

  private static string ToMetaIni(string meta) {
    var sb = new StringBuilder("[Meta]\n");
    foreach (var line in meta.Split('\n')) {
      var tab = line.IndexOf('\t');
      if (tab <= 0) continue;
      sb.Append(line[..tab].Trim()).Append('=').Append(line[(tab + 1)..].TrimEnd('\r').Trim()).Append('\n');
    }
    return sb.ToString();
  }

  private static MoofImage Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return MoofImage.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(MoofImage image) {
    yield return ("metadata.ini", Encoding.UTF8.GetBytes(image.RenderMetadata()));
    yield return ("info.bin", image.Info);
    yield return ("tmap.bin", image.Tmap);
    if (image.FluxMap is not null) yield return ("flux.bin", image.FluxMap);
    if (image.Meta is not null) yield return ("meta.ini", Encoding.UTF8.GetBytes(ToMetaIni(image.Meta)));
    foreach (var (t, (data, _)) in image.Tracks)
      yield return ($"tracks/track{t:D3}.bin", data);
  }
}
