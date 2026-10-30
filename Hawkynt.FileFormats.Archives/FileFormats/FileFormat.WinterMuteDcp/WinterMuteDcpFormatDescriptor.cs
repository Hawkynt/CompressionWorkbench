#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WinterMuteDcp;

/// <summary>
/// WinterMute Engine resource package (<c>.dcp</c>), the data container of WME 1.x adventure games.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/retrodump/Wintermute-Engine</c> — WME 1.x source (MIT), from which reader and writer are converted</description></item>
///   <item><description><c>https://archive.org/details/wintermute_engine</c> — WME DevKit 1.10.1, whose engine is the oracle</description></item>
/// </list>
/// </summary>
public sealed class WinterMuteDcpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "WinterMuteDcp";
  public string DisplayName => "WinterMute DCP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dcp";
  public IReadOnlyList<string> Extensions => [".dcp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xDE, 0xAD, 0xC0, 0xDE, (byte)'J', (byte)'U', (byte)'N', (byte)'K'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "zlib"), new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "WinterMute Engine 1.x package (versions 0x100 and 0x200), stored and zlib files. Create options: method, " +
    "name (the package name, which must equal the file name without .dcp; default data), description, gameversion. " +
    "Master indexes are listed but their files live in other packages.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DcpReader(stream);
    return [.. r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Length, e.StoredLength,
      e.IsCompressed ? "zlib" : "stored", false, false, e.TimeDate1 != 0 ? DateTimeOffset.FromUnixTimeSeconds(e.TimeDate1).UtcDateTime : null))];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DcpReader(stream);
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
      var m => throw new NotSupportedException($"WinterMute DCP has no method '{m}'; use deflate or store."),
    };
    var gameVersion = options.GetString("gameversion") is { } gv
      ? uint.TryParse(gv, out var v) ? v : throw new ArgumentException($"WinterMute game version '{gv}' is not a number.")
      : 1u;
    var info = new DcpPackageInfo(options.GetString("name") ?? "data", gameVersion, Description: options.GetString("description") ?? "");
    var entries = FilesOnly(inputs).Select(f => DcpWriteEntry.FromData(f.Name.Replace('\\', '/'), f.Data, compress)).ToList();
    DcpWriter.Write(output, info, entries);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, entries => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var n = name.Replace('\\', '/');
        var replacement = DcpWriteEntry.FromData(n, data, compress: true);
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

  private static void Rewrite(Stream archive, Action<List<DcpWriteEntry>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var r = new DcpReader(archive);
    if (!r.HoldsItsOwnData)
      throw new NotSupportedException("WinterMute master index: its files live in other packages.");
    if (r.Version != DcpReader.Version2)
      throw new NotSupportedException("WinterMute package version 0x100: rewriting it would turn it into a 0x200 package.");
    var dir = r.Directories[0];
    var entries = dir.Entries.Select(e => new DcpWriteEntry(e.Name, e.Length, r.ReadStored(e), e.IsCompressed, e.Flags, e.TimeDate1, e.TimeDate2)).ToList();
    edit(entries);
    using var rebuilt = new MemoryStream();
    DcpWriter.Write(rebuilt, new DcpPackageInfo(dir.Name, r.GameVersion, r.Priority, dir.Cd, r.CreationTime, r.Description, r.Pad), entries);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }
}
