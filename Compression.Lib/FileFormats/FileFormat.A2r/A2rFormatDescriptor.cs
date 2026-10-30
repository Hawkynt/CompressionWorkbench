#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.A2r;

/// <summary>
/// Applesauce A2R flux image, versions 2 and 3. Lists <c>metadata.ini</c> (version, INFO fields and
/// capture counts) and every chunk in file order as <c>chunks/NN_ID.bin</c>; capture chunks (A2R3
/// <c>RWCP</c>/<c>SLVD</c>, A2R2 <c>STRM</c>) become a folder <c>chunks/NN_ID/</c> holding
/// <c>header.bin</c> (A2R3) and one file per capture, <c>MMMM_locLLLLL_kind.bin</c>, which is the
/// capture entry without its mark. Creates images from those parts and edits them; a rebuild of an
/// unedited image is byte-identical.
/// </summary>
public sealed partial class A2rFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.ini" };

  [GeneratedRegex(@"^(?:.*/)?chunks/(\d{2,})_([A-Za-z0-9]{4}|x[0-9A-Fa-f]{8})(?:\.bin|/(header|\d{4,})(?:_[^/]*)?\.bin)$")]
  private static partial Regex MemberName();

  public string Id => "A2r";
  public string DisplayName => "Applesauce A2R";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".a2r";
  public IReadOnlyList<string> Extensions => [".a2r"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x41, 0x32, 0x52, 0x33, 0xFF, 0x0A, 0x0D, 0x0A], Confidence: 0.97),
    new([0x41, 0x32, 0x52, 0x32, 0xFF, 0x0A, 0x0D, 0x0A], Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("flux", "Raw and solved flux captures")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Applesauce A2R flux image, versions 2 (INFO/STRM/META) and 3 (INFO/RWCP/SLVD/META)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "accepts: chunks/NN_ID.bin (raw chunk), chunks/NN_ID/header.bin and chunks/NN_ID/MMMM_*.bin (capture entries), metadata.ini (version=2|3)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    if (MemberName().IsMatch(name) || Path.GetFileName(name).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not an A2R member; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.a2r", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    this.Apply(new Layout(null), inputs).Build().Write(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var image = this.Apply(new Layout(Read(archive)), inputs).Build();
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var layout = new Layout(Read(archive));
    foreach (var raw in entryNames) {
      var name = raw.Replace('\\', '/');
      if (Synthetic.Contains(Path.GetFileName(name))) continue;
      var m = MemberName().Match(name);
      if (!m.Success) throw new NotSupportedException($"A2R: '{raw}' is not a member.");
      var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      if (!layout.Chunks.TryGetValue(n, out var group)) continue;
      if (!m.Groups[3].Success) {
        if (group.Id == "INFO") throw new NotSupportedException("A2R: the INFO chunk is required.");
        layout.Chunks.Remove(n);
      } else if (m.Groups[3].Value == "header") {
        throw new NotSupportedException($"A2R: '{raw}' is part of its capture chunk; remove the captures instead.");
      } else {
        group.Entries?.Remove(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
      }
    }
    WholeImageRebuildCommitter.Replace(archive, layout.Build().Write, candidate => Read(candidate));
  }

  /// <summary>A chunk while it is being edited: raw bytes, or a capture header and numbered entries.</summary>
  private sealed class Group(string id) {
    public string Id { get; } = id;
    public byte[]? Raw { get; set; }
    public byte[]? Header { get; set; }
    public SortedDictionary<int, byte[]>? Entries { get; set; }
  }

  private sealed class Layout {
    public int? Version { get; set; }
    public SortedDictionary<int, Group> Chunks { get; } = new();

    public Layout(A2rImage? image) {
      if (image is null) return;
      this.Version = image.Version;
      for (var i = 0; i < image.Chunks.Count; ++i) {
        var c = image.Chunks[i];
        this.Chunks[i] = c.IsCaptureChunk
          ? new Group(c.Id) { Header = c.Header, Entries = new(c.Entries!.Select((e, k) => (k, e)).ToDictionary(t => t.k, t => t.e)) }
          : new Group(c.Id) { Raw = c.Data };
      }
    }

    public A2rImage Build() {
      var version = this.Version ?? (this.Chunks.Values.Any(g => g.Id == "STRM") ? 2 : 3);
      var image = new A2rImage { Version = version };
      foreach (var g in this.Chunks.Values) {
        if (A2rImage.IsCaptureId(version, g.Id) && g.Raw is not null && g.Header is null && g.Entries is null) {
          image.Chunks.Add(A2rImage.ParseCaptureChunk(version, g.Id, g.Raw));
        } else if (A2rImage.IsCaptureId(version, g.Id)) {
          var header = version == 3 ? g.Header ?? A2rImage.CaptureHeader(g.Id == "SLVD" ? 2 : 1, 62_500) : [];
          image.Chunks.Add(new A2rImage.Chunk(g.Id, [], header, g.Entries is null ? [] : [.. g.Entries.Values]));
        } else {
          if (g.Raw is null) throw new InvalidDataException($"A2R: chunk '{g.Id}' is not a capture chunk in version {version}; give it as one file.");
          image.Chunks.Add(new A2rImage.Chunk(g.Id, g.Raw));
        }
      }
      return image;
    }
  }

  private Layout Apply(Layout layout, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var name = input.ArchiveName.Replace('\\', '/');
      var content = input.ReadContent();
      var m = MemberName().Match(name);
      if (!m.Success) {
        var v = Encoding.UTF8.GetString(content).Split('\n').Select(l => l.Trim())
          .FirstOrDefault(l => l.StartsWith("version=", StringComparison.Ordinal));
        if (v is not null) layout.Version = int.Parse(v[8..], CultureInfo.InvariantCulture) is var ver and (2 or 3)
          ? ver : throw new ArgumentException($"A2R: version {v[8..]} is neither 2 nor 3.");
        continue;
      }
      var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      var id = FromIdName(m.Groups[2].Value);
      if (!layout.Chunks.TryGetValue(n, out var group)) layout.Chunks[n] = group = new Group(id);
      else if (group.Id != id) throw new ArgumentException($"A2R: chunk {n} is '{group.Id}', not '{id}'.");
      if (!m.Groups[3].Success) {
        group.Raw = content;
      } else if (m.Groups[3].Value == "header") {
        group.Header = content.Length == A2rImage.CaptureHeaderSize ? content
          : throw new ArgumentException($"A2R: '{name}' must be {A2rImage.CaptureHeaderSize} bytes.");
      } else {
        if (A2rImage.EntryLength(id, content) != content.Length) throw new ArgumentException($"A2R: '{name}' carries trailing bytes.");
        (group.Entries ??= new())[int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)] = content;
      }
    }
    return layout;
  }

  private static string IdName(string id)
    => id.Length == 4 && id.All(char.IsAsciiLetterOrDigit) ? id : "x" + Convert.ToHexString(Encoding.Latin1.GetBytes(id));

  private static string FromIdName(string name)
    => name.Length == 9 && name[0] == 'x' ? Encoding.Latin1.GetString(Convert.FromHexString(name[1..])) : name;

  private static A2rImage Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return A2rImage.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(A2rImage image) {
    yield return ("metadata.ini", Encoding.UTF8.GetBytes(image.RenderMetadata()));
    for (var i = 0; i < image.Chunks.Count; ++i) {
      var c = image.Chunks[i];
      var stem = $"chunks/{i:D2}_{IdName(c.Id)}";
      if (!c.IsCaptureChunk) {
        yield return (stem + ".bin", c.Data);
        continue;
      }
      if (image.Version == 3) yield return (stem + "/header.bin", c.Header!);
      for (var k = 0; k < c.Entries!.Count; ++k) {
        var e = c.Entries[k];
        yield return ($"{stem}/{k:D4}_loc{A2rImage.Location(c.Id, e):D5}_{A2rImage.KindName(c.Id, e)}.bin", e);
      }
    }
  }
}
