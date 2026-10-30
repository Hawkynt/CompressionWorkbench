#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.LiveMaker;

/// <summary>
/// LiveMaker "VF" resource archive — <c>game.dat</c> of LiveMaker 2/3 (Live Novel) games, the same
/// archive appended to a game executable, and LiveMaker's save files.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/morkt/GARbro</c> — GARbro (MIT), <c>ArcFormats/LiveMaker/ArcVF.cs</c>, from which the reader is converted; also an oracle</description></item>
///   <item><description><c>https://github.com/pmrowla/pylivemaker</c> — pylivemaker (GPL-3.0-or-later), used only as an oracle</description></item>
/// </list>
/// </summary>
public sealed class LiveMakerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "LiveMaker";
  public string DisplayName => "LiveMaker VF archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dat";
  // .dat is generic: recognised by its "vf" + version header only.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'v', (byte)'f', 0x66, 0, 0, 0], Confidence: 0.8),
    new([(byte)'v', (byte)'f', 0x65, 0, 0, 0], Confidence: 0.8),
    new([(byte)'v', (byte)'f', 0x64, 0, 0, 0], Confidence: 0.8),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "zlib"), new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "LiveMaker game.dat / save archive (versions 100-102), standalone or appended to a game executable: zlib, " +
    "stored and LiveMaker Pro scrambled members, CRC-32 checked. Split archives (.ext index, .001 parts) are not joined.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LiveMakerReader(stream);
    // The directory records no decoded size; stored members have it, the rest are decoded to learn it.
    return [.. r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name,
      e.Method == LiveMakerMethod.Stored ? e.StoredLength : DecodedLength(r, e), e.StoredLength, MethodName(e.Method),
      false, e.EncryptFlag != 0, null))];
  }

  private static long DecodedLength(LiveMakerReader r, LiveMakerEntry e) {
    try {
      return r.Extract(e).Length;
    } catch (InvalidDataException) {
      return -1;
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LiveMakerReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var compress = (options.MethodName ?? "deflate").ToLowerInvariant() switch {
      "deflate" or "zlib" => true,
      "store" or "stored" => false,
      var m => throw new NotSupportedException($"LiveMaker archives have no method '{m}'; use deflate or store."),
    };
    LiveMakerWriter.Write(output, [.. FilesOnly(inputs).Select(f => LiveMakerWriteEntry.FromData(f.Name.Replace('\\', '/'), f.Data, compress))]);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, entries => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var n = name.Replace('\\', '/');
        var replacement = LiveMakerWriteEntry.FromData(n, data, compress: true);
        var at = entries.FindIndex(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) entries[at] = replacement;
        else entries.Add(replacement);
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    Rewrite(archive, entries => entries.RemoveAll(e => drop.Contains(e.Name)));
  }

  private static void Rewrite(Stream archive, Action<List<LiveMakerWriteEntry>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var r = new LiveMakerReader(archive);
    if (r.BaseOffset != 0)
      throw new NotSupportedException("LiveMaker archive appended to an executable: rewriting it would drop the executable.");
    var entries = r.Entries.Select(e => new LiveMakerWriteEntry(e.Name, r.ReadStored(e), e.Method, e.Unknown, e.Checksum, e.EncryptFlag)).ToList();
    edit(entries);
    using var rebuilt = new MemoryStream();
    LiveMakerWriter.Write(rebuilt, entries, r.Version);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private static string MethodName(LiveMakerMethod m) => m switch {
    LiveMakerMethod.Zlib => "zlib",
    LiveMakerMethod.Stored => "stored",
    LiveMakerMethod.Scrambled => "scrambled",
    _ => "scrambled+zlib",
  };
}
