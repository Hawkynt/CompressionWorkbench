#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Chm;

public sealed class ChmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the CHM archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the CHM archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ChmReader(stream);
        return r.Entries.Where(e => e.Size > 0 && !e.Path.StartsWith("::")).Select(e => (e.Path, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ChmWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms, useLzx: false);
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new ChmReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Path);
    }
  }

  public string Id => "Chm";
  public string DisplayName => "CHM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".chm";
  public IReadOnlyList<string> Extensions => [".chm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ITSF"u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("chm", "CHM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft Compiled HTML Help";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ChmReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size,
      e.Section == 0 ? "Stored" : "LZX", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ChmReader(stream);
    foreach (var e in r.Entries) {
      if (e.Size == 0) continue;
      if (e.Path.StartsWith("::")) continue; // skip internal entries
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      try {
        WriteFile(outputDir, e.Path.TrimStart('/'), r.Extract(e));
      } catch { /* skip entries that can't be decompressed */ }
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Default: section 0 (stored). Set MethodName="lzx" for LZX-compressed section 1.
    var w = new ChmWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    var useLzx = string.Equals(options.MethodName, "lzx", StringComparison.OrdinalIgnoreCase);
    w.WriteTo(output, useLzx);
  }
}
