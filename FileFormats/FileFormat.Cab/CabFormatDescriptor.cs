#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cab;

public sealed class CabFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {
  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => CabLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag: extracts then re-creates the CAB in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the CAB per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new CabReader(stream);
        return r.Entries.Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new CabWriter(CabCompressionType.MsZip);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  public string Id => "Cab";
  public string DisplayName => "CAB";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cab";
  public IReadOnlyList<string> Extensions => [".cab"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'S', (byte)'C', (byte)'F'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("mszip", "MS-ZIP"), new("lzx", "LZX"), new("quantum", "Quantum"), new("none", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Cabinet archive, used in Windows installers";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CabReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, -1,
      "CAB", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CabReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var compType = options.MethodName switch {
      "lzx" => CabCompressionType.Lzx,
      "quantum" => CabCompressionType.Quantum,
      "none" or "store" => CabCompressionType.None,
      _ => CabCompressionType.MsZip,
    };
    var lzxWindow = options.Level.HasValue ? Math.Clamp(options.Level.Value, 15, 21) : 15;
    var quantumLevel = options.Level.HasValue ? Math.Clamp(options.Level.Value, 1, 7) : 4;
    var w = new CabWriter(compType, lzxWindowBits: lzxWindow, quantumWindowLevel: quantumLevel);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
